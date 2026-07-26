using System.Diagnostics;
using System.Net;
using System.Text;
using Net.Codecrete.QrCodeGenerator;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Facade;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Models;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.ViewModels;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Views;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Impl.MessageHandling;

internal sealed class XiaohongshuMessageHandler : IDisposable
{
    private static readonly HttpClient ImageHttp = RemoteImageFetchService.CreateImageHttpClient();

    private readonly IBotContext _context;
    private readonly PluginConfig _config;
    private readonly ParseProviderRegistry _providerRegistry;
    private readonly XiaohongshuParseProvider _provider;
    private LocalVideoHttpServer? _localVideoHttpServer;

    public XiaohongshuMessageHandler(IBotContext context, PluginConfig config, ParseProviderRegistry providerRegistry, XiaohongshuParseProvider provider)
    {
        _context = context;
        _config = config;
        _providerRegistry = providerRegistry;
        _provider = provider;
    }

    public async Task ParseAndReplyAsync(MessageEvent message, string text)
    {
        try
        {
            await TryReactToSourceMessageAsync(message, "351");
            var media = await _providerRegistry.ParseAsync(text);
            if (media.ProviderPayload is not XiaohongshuParseResult result)
            {
                await TryReactToSourceMessageAsync(message, "9");
                await ReplyAsync(message, "小红书链接已识别，但发送流程尚未接入。");
                return;
            }

            if (result.IsVideo && _config.SendVideoSegment)
            {
                await SendVideoFlowAsync(message, result);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            if (result.IsGallery)
            {
                await SendGalleryForwardAsync(message, result);
                await SendGalleryCardAsync(message, result);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            await ReplyAsync(message, FormatResult(result));
            await TryReactToSourceMessageAsync(message, "426");
        }
        catch (XiaohongshuSignRequiredException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "小红书解析需要 xhshow sign 服务：" + ex.Message);
        }
        catch (XiaohongshuParseException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "小红书解析失败：" + ex.Message);
        }
        catch (TaskCanceledException)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "小红书解析超时，请稍后重试。若经常失败，请检查 Cookie / sign 服务。");
        }
        catch (Exception ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            BotLog.Error($"MyParser 小红书解析异常：{ex}");
            await ReplyAsync(message, "小红书解析异常：" + ex.Message);
        }
    }

    public async Task HandleLoginAsync(MessageEvent message)
    {
        try
        {
            var session = await _provider.Parser.GenerateQrLoginSessionAsync();
            await ReplyAsync(message,
                "小红书扫码登录\n"
                + "请用小红书 App 扫描下面二维码，并在 3 分钟内确认登录。\n"
                + "如果触发安全验证，请改用浏览器登录后复制 Cookie。\n"
                + $"如果二维码图片无法显示，请打开：{session.Url}");
            await SendQrImageAsync(message, session.Url, $"xhs_qr_{session.QrId}");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var current = session;
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                var poll = await _provider.Parser.PollQrLoginAsync(current, cts.Token);
                current = current with { Cookie = poll.Cookie };
                if (poll.NeedVerify)
                {
                    await ReplyAsync(message, poll.Message);
                    return;
                }

                if (poll.IsLogin)
                {
                    SaveXiaohongshuCookieToPluginDirectory();
                    await ReplyAsync(message, $"小红书登录成功：{poll.UserName ?? "已登录"}。Cookie 已保存到插件 cookies/xiaohongshu.txt。");
                    return;
                }

                BotLog.Info($"MyParser 小红书二维码轮询: status={poll.CodeStatus}, message={poll.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            await ReplyAsync(message, "小红书登录二维码已超时，请重新发送登录命令。");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小红书扫码登录失败：{ex}");
            await ReplyAsync(message, "小红书扫码登录失败：" + ex.Message);
        }
    }

    private async Task SendVideoFlowAsync(MessageEvent message, XiaohongshuParseResult result)
    {
        string? fileUploadInfo = null;
        try
        {
            _ = StartSendCoverOrCardAsync(message, result);
            var videoSegment = await BuildVideoSegmentAsync(result);
            await SendVideoMessageAsync(message, result, videoSegment);

            if (_config.UploadVideoAsFile && !_config.UploadVideoAsFileOnlyOnVideoSendFailure && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
            {
                fileUploadInfo = await UploadVideoFileAsync(message, result);
                BotLog.Info($"MyParser 小红书文件上传完成: note_id={result.NoteId}, {fileUploadInfo}");
            }

            if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
            {
                _localVideoHttpServer?.UnregisterFile(result.LocalVideoPath);
                result.LocalVideoRegisteredToHttpServer = false;
            }

            LocalMediaCleanup.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "xiaohongshu");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小红书视频消息发送失败: note_id={result.NoteId}, error={ex.Message}");
            if (_config.UploadVideoAsFile && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
            {
                try
                {
                    fileUploadInfo = await UploadVideoFileAsync(message, result);
                    if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
                    {
                        _localVideoHttpServer?.UnregisterFile(result.LocalVideoPath);
                        result.LocalVideoRegisteredToHttpServer = false;
                    }

                    LocalMediaCleanup.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "xiaohongshu");
                    BotLog.Info($"MyParser 小红书 VideoSegment 失败后文件上传完成: note_id={result.NoteId}, {fileUploadInfo}");
                    return;
                }
                catch (Exception uploadEx)
                {
                    throw new XiaohongshuParseException("视频发送失败，文件上传也失败：" + uploadEx.Message);
                }
            }

            throw;
        }
    }

    private Task StartSendCoverOrCardAsync(MessageEvent message, XiaohongshuParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CoverUrl) && result.Images.Count == 0)
        {
            return Task.CompletedTask;
        }

        return MessageHandlerCommon.RunLoggedBackgroundAsync($"小红书封面卡片异步发送: note_id={result.NoteId}", () => SendCoverOrCardAsync(message, result));
    }

    private async Task<VideoSegment> BuildVideoSegmentAsync(XiaohongshuParseResult result)
    {
        var (fileUri, localPath) = await _provider.Parser.DownloadVideoAsync(result);
        result.LocalVideoFileUri = fileUri;
        result.LocalVideoPath = localPath;
        var fileSize = new FileInfo(localPath).Length;
        string videoUri;
        string uriMode;
        if (_config.FileProtocol == VideoSegmentFileProtocol.Base64)
        {
            videoUri = "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(localPath));
            uriMode = "base64";
        }
        else if (_config.FileProtocol == VideoSegmentFileProtocol.Http)
        {
            videoUri = GetLocalVideoHttpServer().RegisterFile(localPath);
            result.LocalVideoRegisteredToHttpServer = true;
            uriMode = "http";
        }
        else
        {
            videoUri = fileUri;
            uriMode = "file";
        }

        BotLog.Info($"MyParser 小红书 VideoSegment URI 模式：{uriMode}, file_mb={fileSize / 1024d / 1024d:F2}, uri_preview={MediaUriUtilities.PreviewUri(videoUri)}");
        // TODO: 新 SDK 的 VideoSegment 暂无缩略图字段，原 thumbUri(result.CoverUrl) 无法透传。
        return new VideoSegment(videoUri);
    }

    private async Task SendVideoMessageAsync(MessageEvent message, XiaohongshuParseResult result, VideoSegment videoSegment)
    {
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 小红书 VideoSegment 发送开始: note_id={result.NoteId}, scene={GetMessageScene(message)}, uri_mode={MediaUriUtilities.GetUriMode(videoSegment.Uri)}");
        await _context.Message.ReplyAsync(message, videoSegment);
        BotLog.Info($"MyParser 小红书 VideoSegment 发送完成: note_id={result.NoteId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task SendCoverOrCardAsync(MessageEvent message, XiaohongshuParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CoverUrl))
        {
            return;
        }

        var image = await BuildRemoteImageAsync(result.CoverUrl, result.SourceUrl, $"xhs_cover_{result.NoteId}");
        if (!string.IsNullOrWhiteSpace(image.Uri))
        {
            await SendImageAsync(message, new ImageSegment(image.Uri));
        }
    }

    private async Task SendGalleryForwardAsync(MessageEvent message, XiaohongshuParseResult result)
    {
        var forwarded = new List<QqForwardedMessage>();
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "小红书图文" : result.AuthorName!;
        forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqTextOutgoing(BuildHeaderText(result))]));
        var imageInputs = result.Images.Select((image, index) => (image, Index: index + 1)).ToArray();
        var imageFiles = await MessageFetchConcurrency.SelectParallelOrderedAsync(
            imageInputs,
            MessageFetchConcurrency.DefaultImageConcurrency,
            item => BuildRemoteImageAsync(item.image.Url, result.SourceUrl, $"xhs_image_{result.NoteId}_{item.Index:D2}"));
        foreach (var local in imageFiles)
        {
            if (!string.IsNullOrWhiteSpace(local.Uri))
            {
                forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqImageOutgoing(local.Uri)]));
            }
        }

        if (result.Comments.Count > 0)
        {
            forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqTextOutgoing(BuildCommentsText(result))]));
        }

        if (!string.IsNullOrWhiteSpace(result.SourceUrl))
        {
            forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqTextOutgoing("原文：" + result.SourceUrl)]));
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? "小红书图文" : TrimLine(result.Title!, 48);
        var preview = new[] { "小红书图文", senderName, result.Comments.Count > 0 ? $"前 {result.Comments.Count} 条评论" : $"图片 {result.Images.Count} 张" };
        var summary = $"{result.Images.Count} 张图 · {result.Comments.Count} 条评论";
        var forward = MessageHandlerCommon.BuildForwardSegment(forwarded, title, preview, summary, "小红书图文");
        await _context.Message.ReplyAsync(message, forward);
    }

    private async Task SendGalleryCardAsync(MessageEvent message, XiaohongshuParseResult result)
    {
        var cardUri = await BuildGalleryCardUriAsync(result);
        if (!string.IsNullOrWhiteSpace(cardUri))
        {
            await SendImageAsync(message, new ImageSegment(cardUri));
        }
    }

    private async Task<string> BuildGalleryCardUriAsync(XiaohongshuParseResult result)
    {
        if (_context.Render is null || result.Images.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            var coverTask = BuildRemoteImageAsync(result.Images[0].Url, result.SourceUrl, $"xhs_card_cover_{result.NoteId}");
            var secondTask = BuildRemoteImageAsync(result.Images.ElementAtOrDefault(1)?.Url ?? result.Images[0].Url, result.SourceUrl, $"xhs_card_second_{result.NoteId}");
            var avatarTask = BuildRemoteImageAsync(result.AuthorAvatarUrl, result.SourceUrl, $"xhs_card_avatar_{result.NoteId}");
            await Task.WhenAll(coverTask, secondTask, avatarTask);
            var cover = await coverTask;
            var second = await secondTask;
            var avatar = await avatarTask;
            var vm = new XiaohongshuCardViewModel
            {
                Cover = !string.IsNullOrWhiteSpace(cover.LocalPath) ? RenderBitmapUtilities.DecodeImageFileForRender(cover.LocalPath) : RenderBitmapUtilities.DecodeBase64ImageForRender(cover.Uri),
                SecondImage = !string.IsNullOrWhiteSpace(second.LocalPath) ? RenderBitmapUtilities.DecodeImageFileForRender(second.LocalPath) : RenderBitmapUtilities.DecodeBase64ImageForRender(second.Uri),
                Avatar = !string.IsNullOrWhiteSpace(avatar.LocalPath) ? RenderBitmapUtilities.DecodeImageFileForRender(avatar.LocalPath) : RenderBitmapUtilities.DecodeBase64ImageForRender(avatar.Uri),
                Title = string.IsNullOrWhiteSpace(result.Title) ? "小红书图文" : result.Title!,
                Description = string.IsNullOrWhiteSpace(result.Description) ? "" : result.Description!,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "未知作者" : result.AuthorName!,
                MetaText = $"{result.Images.Count} 图 · {result.Comments.Count} 条评论",
                StatsText = $"{FormatCount(result.LikeCount)}赞 · {FormatCount(result.CollectCount)}收藏 · {FormatCount(result.CommentCount)}评论",
                TagsText = result.Tags.Count > 0 ? string.Join(" ", result.Tags.Take(6).Select(i => "#" + i)) : "# 小红书",
                Comments = result.Comments.Take(10).Select((comment, index) => new XiaohongshuCommentViewModel
                {
                    Index = (index + 1).ToString(),
                    Nickname = comment.User.Nickname,
                    Content = string.IsNullOrWhiteSpace(comment.Content) ? "[空评论]" : comment.Content,
                    Meta = $"{FormatCount(comment.LikeCount)}赞" + (string.IsNullOrWhiteSpace(comment.IpLocation) ? "" : " · " + comment.IpLocation),
                }).ToList(),
            };
            if (vm.Comments.Count == 0)
            {
                vm.Comments.Add(new XiaohongshuCommentViewModel { Index = "1", Nickname = "提示", Content = "没有获取到评论；评论接口需要登录 Cookie、xsec_token 和 xhshow sign 服务。", Meta = "MyParser" });
            }

            var png = await _context.RenderControlPngAsync<XiaohongshuCard>(vm, new ControlRenderOptions(RenderTheme.Dark));
            BotLog.Info($"MyParser 小红书图文卡片渲染完成: note_id={result.NoteId}, comments={result.Comments.Count}, png_kb={png.Length / 1024d:F1}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小红书图文卡片渲染失败: note_id={result.NoteId}, error={ex.Message}");
            return string.Empty;
        }
    }

    private Task<(string Uri, string? LocalPath)> BuildRemoteImageAsync(string? imageUrl, string? referer, string filePrefix)
    {
        return RemoteImageFetchService.BuildRemoteImageAsync(
            ImageHttp,
            "小红书",
            imageUrl,
            referer,
            filePrefix,
            ResolveDownloadDirectory(),
            request =>
            {
                request.Headers.TryAddWithoutValidation("User-Agent", XiaohongshuConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", referer ?? XiaohongshuConstants.Origin + "/");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                if (!string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.XiaohongshuCookie);
                }
            });
    }

    private async Task SendQrImageAsync(MessageEvent message, string text, string fileName)
    {
        var qrFile = await BuildQrImageAsync(text, fileName);
        await SendImageAsync(message, new ImageSegment(qrFile.Uri));
    }

    private static async Task<(string Uri, string Path)> BuildQrImageAsync(string text, string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", "xiaohongshu", "qr");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName + ".png");
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var png = qr.ToPngBitmap(border: 4, scale: 8);
        await File.WriteAllBytesAsync(path, png);
        return ("base64://" + Convert.ToBase64String(png), path);
    }

    private Task SendImageAsync(MessageEvent message, ImageSegment segment)
    {
        return MessageHandlerCommon.SendImageAsync(_context, message, segment);
    }

    private Task<string> UploadVideoFileAsync(MessageEvent message, XiaohongshuParseResult result)
    {
        return MessageHandlerCommon.UploadLocalVideoFileAsync(_context, _config, message, result.LocalVideoPath, "小红书", result.NoteId);
    }

    private Task TryReactToSourceMessageAsync(MessageEvent message, string faceId)
    {
        return MessageHandlerCommon.ReactAsync(_context, message, faceId, "小红书");
    }

    private void SaveXiaohongshuCookieToPluginDirectory()
    {
        var path = ResolveCookiePath("xiaohongshu.txt");
        File.WriteAllText(path, MyParserRuntime.XiaohongshuCookie ?? string.Empty, Encoding.UTF8);
    }

    private string ResolveCookiePath(string fileName)
    {
        return MessageHandlerCommon.ResolveCookiePath(_context, fileName);
    }

    private Task ReplyAsync(MessageEvent message, string text)
    {
        return MessageHandlerCommon.ReplyTextAsync(_context, _config, message, text);
    }

    private LocalVideoHttpServer GetLocalVideoHttpServer()
    {
        return _localVideoHttpServer ??= new LocalVideoHttpServer();
    }

    private static string BuildHeaderText(XiaohongshuParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsVideo ? "小红书视频" : "小红书图文");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine(result.Title);
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine("作者：" + result.AuthorName);
        if (!string.IsNullOrWhiteSpace(result.Description)) sb.AppendLine(TrimLine(result.Description, 600));
        sb.AppendLine($"数据：{FormatCount(result.LikeCount)}赞 / {FormatCount(result.CollectCount)}收藏 / {FormatCount(result.CommentCount)}评论");
        return sb.ToString().TrimEnd();
    }

    private static string BuildCommentsText(XiaohongshuParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("前 10 条评论");
        foreach (var (comment, index) in result.Comments.Take(10).Select((comment, index) => (comment, index + 1)))
        {
            sb.AppendLine($"{index}. {comment.User.Nickname}: {comment.Content}");
        }

        return sb.ToString().TrimEnd();
    }

    private string FormatResult(XiaohongshuParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsVideo ? "小红书视频解析成功" : "小红书解析成功");
        sb.AppendLine($"Note：{result.NoteId}");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine($"标题：{TrimLine(result.Title, 140)}");
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine($"作者：{result.AuthorName}");
        if (!string.IsNullOrWhiteSpace(result.Description)) sb.AppendLine($"摘要：{TrimLine(result.Description, 220)}");
        sb.AppendLine($"图片数：{result.Images.Count}");
        sb.AppendLine($"评论：已获取 {result.Comments.Count} 条");
        sb.AppendLine($"数据：{FormatCount(result.LikeCount)}赞 / {FormatCount(result.CollectCount)}收藏 / {FormatCount(result.CommentCount)}评论");
        if (!string.IsNullOrWhiteSpace(result.SourceUrl)) sb.AppendLine($"链接：{result.SourceUrl}");
        return sb.ToString().TrimEnd();
    }

    private static long GetBotOrSenderId(MessageEvent message) => MessageHandlerCommon.GetBotOrSenderId(message);

    private static string GetMessageScene(MessageEvent message) => MessageHandlerCommon.GetMessageScene(message);

    private string ResolveDownloadDirectory()
    {
        return Path.IsPathRooted(MyParserRuntime.XiaohongshuDownloadDirectory)
            ? MyParserRuntime.XiaohongshuDownloadDirectory
            : Path.Combine(AppContext.BaseDirectory, MyParserRuntime.XiaohongshuDownloadDirectory);
    }

    private static string FormatCount(long value)
    {
        if (value <= 0) return "--";
        if (value >= 100_000_000) return $"{value / 100_000_000d:F1}亿";
        if (value >= 10_000) return $"{value / 10_000d:F1}万";
        return value.ToString();
    }

    private static string SanitizeLocalFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private static string TrimLine(string value, int maxLength)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    public void Dispose()
    {
        _localVideoHttpServer?.Dispose();
        _localVideoHttpServer = null;
    }
}
