using Shirobot.Plugin.MyParser.Providers.Douyin.Facade;
using Shirobot.Plugin.MyParser.Services;
using Shirobot.Plugin.MyParser.Providers.Douyin.Views;
using Shirobot.Plugin.MyParser.Providers.Douyin.ViewModels;
using Shirobot.Plugin.MyParser.Providers.Douyin.Models;
using Shirobot.Plugin.MyParser.Providers.Douyin.Infrastructure;
using Shirobot.Plugin.MyParser.Utility;
using System.Diagnostics;
using System.Net;
using System.Text;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Providers.Douyin.Impl.MessageHandling;

internal sealed class DouyinMessageHandler : IDisposable
{
    private static readonly HttpClient CoverHttp = RemoteImageFetchService.CreateImageHttpClient();

    private readonly IBotContext _context;
    private readonly PluginConfig _config;
    private readonly ParseProviderRegistry _providerRegistry;
    private readonly DouyinParseProvider _douyinProvider;
    private LocalVideoHttpServer? _localVideoHttpServer;

    public DouyinMessageHandler(
        IBotContext context,
        PluginConfig config,
        ParseProviderRegistry providerRegistry,
        DouyinParseProvider douyinProvider)
    {
        _context = context;
        _config = config;
        _providerRegistry = providerRegistry;
        _douyinProvider = douyinProvider;
    }

    public async Task ParseAndReplyAsync(MessageEvent message, string text)
    {
        await TryReactToSourceMessageAsync(message, "351");
        if (_providerRegistry is null)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await _context.Message.ReplyAsync(message, "解析器尚未初始化，请稍后重试。");
            return;
        }

        try
        {
            var media = await _providerRegistry.ParseAsync(text);
            if (media.ProviderPayload is not DouyinParseResult result)
            {
                await TryReactToSourceMessageAsync(message, "9");
                await _context.Message.ReplyAsync(message, $"{media.ProviderName} 已识别，但该平台发送流程尚未接入。");
                return;
            }

            LogDouyinQualityInfo(result);
            var shouldDownloadVideo = _config.SendVideoSegment && result.IsVideo && !result.IsGallery;
            var videoSent = false;
            var fileUploaded = false;
            string? videoSendError = null;
            string? fileUploadInfo = null;

            if (shouldDownloadVideo)
            {
                try
                {
                    _ = StartSendCoverMessageAsync(message, result);
                    var videoSegment = await BuildVideoSegmentAsync(result);
                    if (videoSegment is null)
                    {
                        await TryReactToSourceMessageAsync(message, "9");
                        await _context.Message.ReplyAsync(message, "视频解析成功，但没有生成 VideoSegment。");
                        return;
                    }

                    await SendVideoMessageAsync(message, result, videoSegment);
                    videoSent = true;

                    if (_config.UploadVideoAsFile && !_config.UploadVideoAsFileOnlyOnVideoSendFailure && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                    {
                        try
                        {
                            fileUploadInfo = await UploadVideoFileAsync(message, result);
                            fileUploaded = true;
                            BotLog.Info($"MyParser 文件上传完成: aweme_id={result.AwemeId}, {fileUploadInfo}");
                        }
                        catch (Exception uploadEx)
                        {
                            fileUploadInfo = uploadEx.Message;
                            BotLog.Warning($"MyParser 文件上传失败: aweme_id={result.AwemeId}, error={uploadEx.Message}");
                        }
                    }

                    if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
                    {
                        _localVideoHttpServer?.UnregisterFile(result.LocalVideoPath);
                        result.LocalVideoRegisteredToHttpServer = false;
                    }

                    LocalMediaCleanup.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "douyin");
                    await TryReactToSourceMessageAsync(message, "426");
                    return;
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser 视频消息发送失败：{ex.Message}");
                    if (_config.UploadVideoAsFile && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                    {
                        try
                        {
                            fileUploadInfo = await UploadVideoFileAsync(message, result);
                            fileUploaded = true;
                            BotLog.Info($"MyParser VideoSegment 失败后文件上传完成: aweme_id={result.AwemeId}, {fileUploadInfo}");
                            if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
                            {
                                _localVideoHttpServer?.UnregisterFile(result.LocalVideoPath);
                                result.LocalVideoRegisteredToHttpServer = false;
                            }

                            LocalMediaCleanup.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "douyin");
                            await TryReactToSourceMessageAsync(message, "426");
                            return;
                        }
                        catch (Exception uploadEx)
                        {
                            BotLog.Warning($"MyParser VideoSegment 失败后文件上传也失败: aweme_id={result.AwemeId}, error={uploadEx.Message}");
                            await TryReactToSourceMessageAsync(message, "9");
                            await _context.Message.ReplyAsync(message, "视频发送失败，文件上传也失败：" + uploadEx.Message);
                            return;
                        }
                    }

                    await TryReactToSourceMessageAsync(message, "9");
                    await _context.Message.ReplyAsync(message, "视频发送失败：" + ex.Message);
                    return;
                }
            }

            if (result.IsGallery)
            {
                if (!string.IsNullOrWhiteSpace(result.CoverUrl))
                {
                    await SendCoverMessageAsync(message, result);
                }

                await SendGalleryMessageAsync(message, result);
                if (!string.IsNullOrWhiteSpace(result.MusicUrl))
                {
                    await SendMusicMessageAsync(message, result);
                }

                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            var reply = FormatDouyinResult(result, shouldDownloadVideo, videoSent, videoSendError, fileUploaded, fileUploadInfo);
            if (_config.QuoteReply)
            {
                await _context.Message.QuoteReplyAsync(message, reply);
            }
            else
            {
                await _context.Message.ReplyAsync(message, reply);
            }
            await TryReactToSourceMessageAsync(message, "426");
        }
        catch (DouyinParseException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await _context.Message.ReplyAsync(message, "解析失败：" + ex.Message);
        }
        catch (TaskCanceledException)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await _context.Message.ReplyAsync(message, "解析超时，请稍后重试。若经常失败，请配置有效 DouyinCookie。");
        }
        catch (Exception ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            BotLog.Error($"MyParser 解析异常：{ex}");
            await _context.Message.ReplyAsync(message, "解析异常：" + ex.Message);
        }
    }

    private Task TryReactToSourceMessageAsync(MessageEvent message, string faceId)
    {
        return MessageHandlerCommon.ReactAsync(_context, message, faceId, "Douyin");
    }

    private static void LogDouyinQualityInfo(DouyinParseResult result)
    {
        if (result.Qualities.Count == 0)
        {
            BotLog.Info($"MyParser 抖音解析: aweme_id={result.AwemeId}, type={(result.IsGallery ? "gallery" : "unknown")}, qualities=0");
            return;
        }

        var selected = result.Qualities.First();
        BotLog.Info(
            "MyParser 抖音选中画质: "
            + $"aweme_id={result.AwemeId}, "
            + $"label={selected.Label}, "
            + $"ratio={selected.Ratio}, "
            + $"fps={(selected.Fps > 0 ? selected.Fps : 0)}, "
            + $"bitrate_kbps={(selected.BitRate > 0 ? selected.BitRate / 1000 : 0)}, "
            + $"size={selected.Width}x{selected.Height}, "
            + $"codec={(string.IsNullOrWhiteSpace(selected.Codec) ? "unknown" : selected.Codec)}, "
            + $"gear={selected.GearName}, "
            + $"total_options={result.Qualities.Count}");

        foreach (var (quality, index) in result.Qualities.Take(12).Select((quality, index) => (quality, index + 1)))
        {
            BotLog.Info(
                "MyParser 抖音可用画质: "
                + $"#{index}, "
                + $"label={quality.Label}, "
                + $"ratio={quality.Ratio}, "
                + $"fps={(quality.Fps > 0 ? quality.Fps : 0)}, "
                + $"bitrate_kbps={(quality.BitRate > 0 ? quality.BitRate / 1000 : 0)}, "
                + $"size={quality.Width}x{quality.Height}, "
                + $"codec={(string.IsNullOrWhiteSpace(quality.Codec) ? "unknown" : quality.Codec)}, "
                + $"gear={quality.GearName}, "
                + $"bytevc1={quality.IsByteVc1}");
        }
    }

    private async Task<VideoSegment?> BuildVideoSegmentAsync(DouyinParseResult result)
    {
        if (!_config.SendVideoSegment || _douyinProvider is null || result.IsGallery || !result.IsVideo)
        {
            return null;
        }

        var (fileUri, localPath) = await _douyinProvider.Parser.DownloadVideoAsync(result);
        result.LocalVideoFileUri = fileUri;
        result.LocalVideoPath = localPath;
        LogFinalVideoFileInfo(result);

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

        BotLog.Info($"MyParser VideoSegment URI 模式：{uriMode}, file_mb={fileSize / 1024d / 1024d:F2}, uri_preview={MediaUriUtilities.PreviewUri(videoUri)}");
        // TODO: 新 SDK 的 VideoSegment 暂无缩略图字段，原 thumbUri(result.CoverUrl) 无法透传。
        return new VideoSegment(videoUri);
    }

    private LocalVideoHttpServer GetLocalVideoHttpServer()
    {
        return _localVideoHttpServer ??= new LocalVideoHttpServer();
    }

private void LogFinalVideoFileInfo(DouyinParseResult result)
    {
        if (!_config.LogSelectedQualityInfo)
        {
            return;
        }

        var selected = result.Qualities.FirstOrDefault();
        var fileSize = !string.IsNullOrWhiteSpace(result.LocalVideoPath) && File.Exists(result.LocalVideoPath)
            ? new FileInfo(result.LocalVideoPath).Length
            : 0;
        BotLog.Info(
            "MyParser 最终发送视频信息: "
            + $"aweme_id={result.AwemeId}, "
            + $"quality={(selected?.Label ?? "unknown")}, "
            + $"ratio={(selected?.Ratio ?? "unknown")}, "
            + $"fps={(selected?.Fps ?? 0)}, "
            + $"bitrate_kbps={(selected is { BitRate: > 0 } ? selected.BitRate / 1000 : 0)}, "
            + $"size={(selected is null ? "0x0" : $"{selected.Width}x{selected.Height}")}, "
            + $"codec={(string.IsNullOrWhiteSpace(selected?.Codec) ? "unknown" : selected.Codec)}, "
            + $"file_mb={(fileSize > 0 ? fileSize / 1024d / 1024d : 0):F2}, "
            + $"path={result.LocalVideoPath}");
    }

    private Task StartSendCoverMessageAsync(MessageEvent message, DouyinParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CoverUrl))
        {
            return Task.CompletedTask;
        }

        return MessageHandlerCommon.RunLoggedBackgroundAsync($"抖音封面卡片异步发送: aweme_id={result.AwemeId}", () => SendCoverMessageAsync(message, result));
    }

    private async Task SendVideoMessageAsync(MessageEvent message, DouyinParseResult result, VideoSegment videoSegment)
    {
        var stopwatch = Stopwatch.StartNew();
        var uriMode = MediaUriUtilities.GetUriMode(videoSegment.Uri);
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser VideoSegment 发送开始: aweme_id={result.AwemeId}, scene={scene}, uri_mode={uriMode}, uri_preview={MediaUriUtilities.PreviewUri(videoSegment.Uri)}");

        var response = await _context.Message.ReplyAsync(message, videoSegment);
        BotLog.Info($"MyParser VideoSegment 发送接口完成: aweme_id={result.AwemeId}, scene={scene}, channel_id={message.Channel.Id}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
        EnsureVideoSendAccepted(response.MessageId, scene);
    }

    private void EnsureVideoSendAccepted(string? messageId, string scene)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId == "0")
        {
            throw new InvalidOperationException($"VideoSegment 发送返回 message_id={messageId}，可能被适配器或平台拒绝，按发送失败处理以触发文件上传 fallback。scene={scene}");
        }
    }

    private static string GetMessageScene(MessageEvent message) => MessageHandlerCommon.GetMessageScene(message);

private async Task SendGalleryMessageAsync(MessageEvent message, DouyinParseResult result)
    {
        if (result.Images.Count == 0)
        {
            await _context.Message.ReplyAsync(message, FormatDouyinResult(result));
            return;
        }

        if (result.Images.Count == 1)
        {
            await SendSingleGalleryImageAsync(message, result, result.Images[0]);
            return;
        }

        var forwardedMessages = new List<QqForwardedMessage>();
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "抖音图文" : result.AuthorName!;

        var imageInputs = result.Images.Select((image, index) => (image, Index: index + 1)).ToArray();
        var imageFiles = await MessageFetchConcurrency.SelectParallelOrderedAsync(
            imageInputs,
            MessageFetchConcurrency.DefaultImageConcurrency,
            item => BuildRemoteImageAsync(item.image.Url, result.SourceUrl, $"douyin_image_{result.AwemeId}_{item.Index:D2}"));
        foreach (var imageFile in imageFiles)
        {
            if (string.IsNullOrWhiteSpace(imageFile.Uri))
            {
                continue;
            }

            forwardedMessages.Add(new QqForwardedMessage(senderId, senderName, [new QqImageOutgoing(imageFile.Uri)]));
        }

        if (forwardedMessages.Count == 0)
        {
            await _context.Message.ReplyAsync(message, FormatDouyinResult(result));
            return;
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? "抖音图文" : TrimLine(result.Title, 48);
        var preview = result.Images.Take(4).Select((_, index) => $"图片 {index + 1}").ToArray();
        var summary = $"共 {result.Images.Count} 张";
        var forward = MessageHandlerCommon.BuildForwardSegment(forwardedMessages, title, preview, summary, "抖音图文");
        var stopwatch = Stopwatch.StartNew();
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser 图文合并转发发送开始: aweme_id={result.AwemeId}, scene={scene}, images={forwardedMessages.Count}/{result.Images.Count}");

        var response = await _context.Message.ReplyAsync(message, forward);
        BotLog.Info($"MyParser 图文合并转发发送完成: aweme_id={result.AwemeId}, scene={scene}, channel_id={message.Channel.Id}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task SendSingleGalleryImageAsync(MessageEvent message, DouyinParseResult result, DouyinImageInfo image)
    {
        var imageFile = await BuildRemoteImageAsync(image.Url, result.SourceUrl, $"douyin_image_{result.AwemeId}_01");
        var stopwatch = Stopwatch.StartNew();
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser 单图图文 ImageSegment 发送开始: aweme_id={result.AwemeId}, scene={scene}, uri_preview={MediaUriUtilities.PreviewUri(imageFile.Uri)}");

        var response = await _context.Message.ReplyAsync(message, new ImageSegment(imageFile.Uri));
        BotLog.Info($"MyParser 单图图文 ImageSegment 发送完成: aweme_id={result.AwemeId}, scene={scene}, channel_id={message.Channel.Id}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task SendMusicMessageAsync(MessageEvent message, DouyinParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.MusicUrl))
        {
            return;
        }

        var segment = new AudioSegment(result.MusicUrl);
        var stopwatch = Stopwatch.StartNew();
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser 图文音乐 RecordSegment 发送开始: aweme_id={result.AwemeId}, scene={scene}, uri_preview={MediaUriUtilities.PreviewUri(result.MusicUrl)}");

        try
        {
            var response = await _context.Message.ReplyAsync(message, segment);
            BotLog.Info($"MyParser 图文音乐 RecordSegment 发送完成: aweme_id={result.AwemeId}, scene={scene}, channel_id={message.Channel.Id}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 图文音乐 RecordSegment 发送失败，回退文本链接: aweme_id={result.AwemeId}, error={ex.Message}");
            await _context.Message.ReplyAsync(message, "音乐：" + result.MusicUrl);
        }
    }

    private static long GetBotOrSenderId(MessageEvent message) => MessageHandlerCommon.GetBotOrSenderId(message);

    private async Task SendCoverMessageAsync(MessageEvent message, DouyinParseResult result)
    {
        var coverUri = await BuildCoverCardUriAsync(result);
        var segment = new ImageSegment(coverUri);
        var stopwatch = Stopwatch.StartNew();
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser 封面卡片 ImageSegment 发送开始: aweme_id={result.AwemeId}, scene={scene}, uri_preview={MediaUriUtilities.PreviewUri(coverUri)}");

        var response = await _context.Message.ReplyAsync(message, segment);
        BotLog.Info($"MyParser 封面卡片 ImageSegment 发送接口完成: aweme_id={result.AwemeId}, scene={scene}, channel_id={message.Channel.Id}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task<string> BuildCoverCardUriAsync(DouyinParseResult result)
    {
        var coverTask = BuildCoverImageAsync(result);
        var avatarTask = _context.Render is null
            ? Task.FromResult<(string Uri, string? LocalPath)>((string.Empty, null))
            : BuildRemoteImageAsync(result.AuthorAvatarUrl, result.SourceUrl, $"douyin_avatar_{result.AwemeId}");
        var coverImage = await coverTask;
        var coverUri = coverImage.Uri;
        if (_context.Render is null)
        {
            BotLog.Warning($"MyParser Avalonia 渲染服务不可用，直接发送原始封面: aweme_id={result.AwemeId}");
            return coverUri;
        }

        try
        {
            var avatarImage = await avatarTask;
            var coverBitmap = !string.IsNullOrWhiteSpace(coverImage.LocalPath)
                ? RenderBitmapUtilities.DecodeImageFileForRender(coverImage.LocalPath)
                : RenderBitmapUtilities.DecodeBase64ImageForRender(coverUri);
            var avatarBitmap = !string.IsNullOrWhiteSpace(avatarImage.LocalPath)
                ? RenderBitmapUtilities.DecodeImageFileForRender(avatarImage.LocalPath)
                : RenderBitmapUtilities.DecodeBase64ImageForRender(avatarImage.Uri);
            BotLog.Info($"MyParser 封面卡片纹理准备: aweme_id={result.AwemeId}, bitmap={(coverBitmap is null ? "null" : "ok")}, avatar={(avatarBitmap is null ? "null" : "ok")}, mode=base64");

            var vm = new DouyinCardViewModel
            {
                Cover = coverBitmap,
                Avatar = avatarBitmap,
                CoverUri = coverUri,
                AlbumId = $"抖音 {result.AwemeId}",
                Title = string.IsNullOrWhiteSpace(result.Title) ? (result.IsGallery ? "抖音图文" : "抖音视频") : result.Title,
                Description = BuildDescriptionText(result),
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "@未知作者" : "@" + result.AuthorName,
                AuthorMeta = BuildAuthorMeta(result),
                DurationText = FormatDurationText(result.DurationMilliseconds),
                PageText = BuildCoverCardSubtitle(result),
                ViewCount = FormatCount(result.PlayCount),
                LikeCount = FormatCount(result.LikeCount),
                CollectCount = FormatCount(result.CollectCount),
                CommentCount = FormatCount(result.CommentCount),
                ShareCount = FormatCount(result.ShareCount),
                MusicText = BuildMusicText(result),
                TagsText = BuildTagsText(result),
            };
            var png = await _context.RenderControlPngAsync<DouyinCard>(vm, new ControlRenderOptions(RenderTheme.Auto));
            var uri = "base64://" + Convert.ToBase64String(png);
            BotLog.Info($"MyParser 封面卡片渲染完成: aweme_id={result.AwemeId}, cover_url={result.CoverUrl}, png_kb={png.Length / 1024d:F1}, view={typeof(DouyinCard).FullName}, mode=base64");
            return uri;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 封面卡片渲染失败，直接发送原始封面: aweme_id={result.AwemeId}, cover_url={result.CoverUrl}, error={ex.Message}");
            return coverUri;
        }
    }

private static string BuildCoverCardSubtitle(DouyinParseResult result)
    {
        var quality = result.Qualities.FirstOrDefault();
        var author = string.IsNullOrWhiteSpace(result.AuthorName) ? "未知作者" : result.AuthorName;
        if (result.IsGallery)
        {
            return $"{author} · 图文 {result.Images.Count} 张";
        }

        return quality is null ? author : $"{author} · {quality.Label}";
    }

    private static string BuildAuthorMeta(DouyinParseResult result)
    {
        var parts = new List<string>();
        if (result.AuthorFollowerCount > 0)
        {
            parts.Add($"{FormatCount(result.AuthorFollowerCount)}粉丝");
        }

        if (!string.IsNullOrWhiteSpace(result.AuthorRegion))
        {
            parts.Add(result.AuthorRegion);
        }

        parts.Add(result.PlayCount > 0 ? $"{FormatCount(result.PlayCount)}播放" : "播放量--");

        return parts.Count > 0 ? string.Join(" · ", parts) : "抖音作者";
    }

    private static string BuildDescriptionText(DouyinParseResult result)
    {
        return string.Empty;
    }

    private static string FormatDurationText(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "--:--";
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }

    private static string BuildMusicText(DouyinParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.MusicTitle) && string.IsNullOrWhiteSpace(result.MusicAuthor))
        {
            return "音乐：--";
        }

        if (string.IsNullOrWhiteSpace(result.MusicAuthor))
        {
            return "音乐：" + TrimLine(result.MusicTitle!, 34);
        }

        if (string.IsNullOrWhiteSpace(result.MusicTitle))
        {
            return "音乐：" + TrimLine(result.MusicAuthor!, 34);
        }

        return "音乐：" + TrimLine($"{result.MusicTitle} · {result.MusicAuthor}", 34);
    }

    private static string BuildTagsText(DouyinParseResult result)
    {
        return result.Tags.Count == 0
            ? "#抖音"
            : TrimLine(string.Join(" ", result.Tags.Take(5).Select(i => "#" + i)), 42);
    }

    private static string FormatCount(long value)
    {
        if (value <= 0)
        {
            return "--";
        }

        if (value >= 100_000_000)
        {
            return $"{value / 100_000_000d:F1}亿";
        }

        if (value >= 10_000)
        {
            return $"{value / 10_000d:F1}万";
        }

        return value.ToString();
    }

    private async Task<(string Uri, string? LocalPath)> BuildCoverImageAsync(DouyinParseResult result)
    {
        var coverUrl = result.CoverUrl ?? throw new InvalidOperationException("封面 URL 为空。");
        return await BuildRemoteImageAsync(coverUrl, result.SourceUrl, $"douyin_cover_{result.AwemeId}");
    }

    private Task<(string Uri, string? LocalPath)> BuildRemoteImageAsync(string? imageUrl, string? referer, string filePrefix)
    {
        return RemoteImageFetchService.BuildRemoteImageAsync(
            CoverHttp,
            "抖音",
            imageUrl,
            referer,
            filePrefix,
            ResolveCoverDownloadDirectory(),
            request =>
            {
                request.Headers.TryAddWithoutValidation("User-Agent", DouyinConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", referer ?? "https://www.douyin.com/");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            });
    }

    private string ResolveCoverDownloadDirectory()
    {
        return MyParserRuntime.DownloadDirectory;
    }

    private static string SanitizeLocalFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

private static void LogCoverImageInfo(DouyinParseResult result, string mode, long bytes, string uri)
    {
        BotLog.Info($"MyParser 封面 ImageSegment URI 模式：aweme_id={result.AwemeId}, mode={mode}, size_kb={(bytes > 0 ? bytes / 1024d : 0):F1}, uri_preview={MediaUriUtilities.PreviewUri(uri)}");
    }

    private Task<string> UploadVideoFileAsync(MessageEvent message, DouyinParseResult result)
    {
        return MessageHandlerCommon.UploadLocalVideoFileAsync(_context, _config, message, result.LocalVideoPath, "抖音", result.AwemeId);
    }

    private string FormatDouyinResult(
        DouyinParseResult result,
        bool videoDownloadAttempted = false,
        bool videoSent = false,
        string? videoSendError = null,
        bool fileUploaded = false,
        string? fileUploadInfo = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsGallery ? "抖音图集解析成功" : "抖音视频解析成功");
        sb.AppendLine($"ID：{result.AwemeId}");

        if (!string.IsNullOrWhiteSpace(result.Title))
        {
            sb.AppendLine($"标题：{TrimLine(result.Title, 140)}");
        }

        if (!string.IsNullOrWhiteSpace(result.AuthorName))
        {
            sb.AppendLine($"作者：{result.AuthorName}");
        }

        if (result.IsGallery)
        {
            sb.AppendLine($"图片数：{result.Images.Count}");
            sb.AppendLine("图集：已解析，未展示直链");
        }
        else if (!string.IsNullOrWhiteSpace(result.VideoUrl))
        {
            var quality = result.Qualities.FirstOrDefault();
            var videoStatus = videoSent
                ? "视频：已下载并已调用 VideoSegment 发送接口"
                : videoDownloadAttempted
                    ? $"视频：下载或发送失败，已隐藏直链；原因：{TrimLine(videoSendError ?? "未知错误", 80)}"
                    : "视频：已解析，未展示直链";
            sb.AppendLine(videoStatus);
            if (_config.UploadVideoAsFile)
            {
                sb.AppendLine(fileUploaded
                    ? $"文件上传：已上传为{fileUploadInfo}"
                    : $"文件上传：失败或未执行；原因：{TrimLine(fileUploadInfo ?? "未知", 80)}");
            }

            if (quality is not null)
            {
                sb.AppendLine($"清晰度：{quality.Label}");
            }
        }
        else
        {
            sb.AppendLine("未提取到视频或图集资源。可尝试配置 DouyinCookie 后重试。");
        }

        if (!string.IsNullOrWhiteSpace(result.MusicUrl))
        {
            sb.AppendLine($"音乐：{result.MusicUrl}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetPlainText(MessageEvent message) => message.GetPlainText();

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
