using System.Diagnostics;
using System.Text;
using ShiroBot.SDK.Models;
using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.BiliBili.MessageHandling;

internal sealed partial class BilibiliMessageHandler(
    IBotContext context,
    PluginConfig config,
    ParseProviderRegistry providerRegistry,
    IParseProvider bilibiliProvider,
    IProviderHostServices hostServices)
    : ProviderMessageHandlerBase(new ProviderMessageHandlerContext(context, config, providerRegistry, bilibiliProvider, hostServices))
{
    private readonly IProviderHostServices _hostServices = hostServices;

    public override string ProviderId => "bilibili";

    private readonly Lock _subscriptionLock = new();
    private readonly List<IReplySubscription> _replySubscriptions = [];
    
    public override async Task ParseAndReplyAsync(IncomingMessage message, string text, bool silentProviderMismatch = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await TryReactToSourceMessageAsync(message, "351");
            var media = await providerRegistry.ParseAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (media.ProviderPayload is BilibiliArticleParseResult article)
            {
                await SendArticleForwardAsync(message, article);
                await SendArticleDocumentCardAsync(message, article);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            if (media.ProviderPayload is BilibiliLiveParseResult live)
            {
                await SendLiveForwardAsync(message, live);
                await TrySendLiveReplayClipAsync(message, live);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            BilibiliParseResult? episodeVideo = null;
            if (media.ProviderPayload is BilibiliBangumiEpisodeVideoParseResult bangumiEpisode)
            {
                await SendBangumiForwardAsync(message, bangumiEpisode.Bangumi, sendEpHint: false);
                episodeVideo = bangumiEpisode.Video;
            }
            else if (media.ProviderPayload is BilibiliBangumiParseResult bangumi)
            {
                await SendBangumiForwardAsync(message, bangumi);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            if (media.ProviderPayload is BilibiliMultiPageParseResult multiPage)
            {
                await SendMultiPageForwardAsync(message, multiPage);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            var result = episodeVideo ?? media.ProviderPayload as BilibiliParseResult;
            if (result is null)
            {
                return;
            }

            LogBilibiliQualityInfo(result);
            BotLog.Info($"MyParser Bilibili 封面开关: bvid={result.Bvid}, global={config.SendCoverImages}, platform={config.SendBilibiliVideoCover}, has_cover={!string.IsNullOrWhiteSpace(result.CoverUrl)}");
            if (config.IsCoverEnabled("bilibili"))
            {
                _ = StartSendCoverMessageAsync(message, result, cancellationToken);
            }
            if (!config.SendVideoSegment || !result.IsVideo)
            {
                await ReplyAsync(message, FormatBilibiliResult(result, videoDownloadAttempted: false));
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            var videoSent = false;
            var fileUploaded = false;
            string? videoSendError;
            string? fileUploadInfo = null;

            try
            {
                var videoSegment = await BuildVideoSegmentAsync(result);
                await SendVideoMessageAsync(message, result, videoSegment);
                videoSent = true;

                if (config is { UploadVideoAsFile: true, UploadVideoAsFileOnlyOnVideoSendFailure: false } && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                {
                    fileUploadInfo = await UploadVideoFileAsync(message, result);
                    fileUploaded = true;
                    BotLog.Info($"MyParser Bilibili 文件上传完成: bvid={result.Bvid}, {fileUploadInfo}");
                }

                CleanupLocalVideoAfterSend(result);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                videoSendError = ex.Message;
                BotLog.Warning($"MyParser Bilibili VideoSegment 发送未确认: bvid={result.Bvid}, detail={ex.Message}");
                if (config.UploadVideoAsFile && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                {
                    try
                    {
                        fileUploadInfo = await UploadVideoFileAsync(message, result);
                        fileUploaded = true;
                        BotLog.Info($"MyParser Bilibili VideoSegment 未确认后文件上传完成: bvid={result.Bvid}, {fileUploadInfo}");
                        CleanupLocalVideoAfterSend(result);
                        await TryReactToSourceMessageAsync(message, "426");
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception uploadEx)
                    {
                        fileUploadInfo = uploadEx.Message;
                        BotLog.Warning($"MyParser Bilibili VideoSegment 未确认，文件上传也未完成: bvid={result.Bvid}, detail={uploadEx.Message}");
                    }
                }
            }

            await ReplyAsync(message, FormatBilibiliResult(result, true, videoSent, videoSendError, fileUploaded, fileUploadInfo));
            await TryReactToSourceMessageAsync(message, "9");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BilibiliLoginRequiredException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "Bilibili 解析需要登录：" + ex.Message);
        }
        catch (BilibiliParseException ex) when (silentProviderMismatch && IsAutoParseProviderMismatch(ex))
        {
            await _hostServices.RemoveReactionAsync(message, "351", "Bilibili");
            BotLog.Info($"MyParser Bilibili 自动解析忽略非目标链接: error={ex.Message}");
        }
        catch (BilibiliParseException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "Bilibili 解析未完成：" + ex.Message);
        }
        catch (TaskCanceledException)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "Bilibili 解析超时，请稍后再试。若经常超时，请检查 BilibiliCookie、网络和媒体处理配置。");
        }
        catch (Exception ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            BotLog.Error($"MyParser Bilibili 解析异常：{ex}");
            await ReplyAsync(message, "Bilibili 解析异常：" + ex.Message);
        }
    }

    private Task TryReactToSourceMessageAsync(IncomingMessage message, string faceId)
    {
        return _hostServices.ReactAsync(message, faceId, "Bilibili");
    }

    private static bool IsAutoParseProviderMismatch(BilibiliParseException ex)
    {
        var message = ex.Message;
        return message.Contains("无法从输入中提取", StringComparison.OrdinalIgnoreCase)
               || message.Contains("短链接跳转后未找到", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是视频", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是专栏", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是图文", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是动态", StringComparison.OrdinalIgnoreCase)
               || message.Contains("接口错误 -400", StringComparison.OrdinalIgnoreCase)
               || message.Contains("请求错误", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<VideoOutgoingSegment> BuildVideoSegmentAsync(BilibiliParseResult result)
    {
        var videoProvider = string.IsNullOrWhiteSpace(result.SourceUrl)
            ? null
            : providerRegistry.FindProvider(result.SourceUrl) as IParseProviderWithParser;
        var parserObject = videoProvider?.ParserObject;
        if (parserObject is IVideoDownloadGate gate)
        {
            gate.EnsureVideoDownloadAllowed();
        }

        var (fileUri, localPath) = await _hostServices.DownloadMuxedProviderVideoAsync(config, BuildBilibiliMuxedVideoDownloadRequest(result));
        result.LocalVideoPath = localPath;
        result.LocalVideoFileUri = fileUri;
        LogFinalVideoFileInfo(result);

        var segmentResult = await _hostServices.BuildLocalVideoSegmentAsync(config, new ProviderLocalVideoSegmentRequest(
            "Bilibili",
            result.Bvid,
            localPath,
            fileUri,
            result.CoverUrl,
            "bvid"));
        result.LocalVideoRegisteredToHttpServer = segmentResult.RegisteredToHttpServer;
        return segmentResult.Segment;
    }

    private static ProviderMuxedVideoDownloadRequest BuildBilibiliMuxedVideoDownloadRequest(BilibiliParseResult result)
    {
        return new ProviderMuxedVideoDownloadRequest(
            "bilibili",
            "Bilibili",
            result.Bvid,
            $"bilibili:{result.Bvid}:p{result.Page}:cid{result.Cid}",
            result.Title,
            MyParserRuntime.BilibiliDownloadDirectory,
            result.VideoStreams.Select(ToProviderMuxedStream).ToArray(),
            result.AudioStreams.Select(ToProviderMuxedStream).ToArray(),
            (method, url, range) => CreateBilibiliMediaRequest(method, url, result, range),
            "bvid");
    }

    private static ProviderMuxedMediaStream ToProviderMuxedStream(BilibiliMediaStream stream)
    {
        return new ProviderMuxedMediaStream(
            stream.StreamId,
            stream.Url,
            stream.BackupUrls,
            stream.QualityId,
            stream.QualityName,
            stream.Width,
            stream.Height,
            stream.Fps,
            stream.CodecName,
            stream.IsAudio);
    }

    private static HttpRequestMessage CreateBilibiliMediaRequest(HttpMethod method, string url, BilibiliParseResult result, string? range)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", result.SourceUrl ?? $"https://www.bilibili.com/video/{result.Bvid}/");
        request.Headers.TryAddWithoutValidation("Origin", BilibiliConstants.Origin);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
        }

        return request;
    }

    private Task StartSendCoverMessageAsync(IncomingMessage message, BilibiliParseResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.CoverUrl))
        {
            return Task.CompletedTask;
        }

        return _hostServices.RunLoggedBackgroundAsync($"Bilibili 封面卡片异步发送: bvid={result.Bvid}", async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendCoverMessageAsync(message, result);
        });
    }

    private async Task SendVideoMessageAsync(IncomingMessage message, BilibiliParseResult result, VideoOutgoingSegment videoSegment)
    {
        var segments = new OutgoingSegment[] { videoSegment };
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser Bilibili VideoSegment 发送开始: bvid={result.Bvid}, scene={GetMessageScene(message)}, uri_mode={_hostServices.GetUriMode(videoSegment.Uri)}, uri_preview={_hostServices.PreviewUri(videoSegment.Uri)}");
        var response = await context.Message.ReplyAsync(message, segments);
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser Bilibili VideoSegment 发送接口完成: bvid={result.Bvid}, scene={scene}, message_id={response.MessageId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
        EnsureVideoSendAccepted(response.MessageId, scene);
    }

    private void EnsureVideoSendAccepted(string messageId, string scene)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            BotLog.Warning($"MyParser Bilibili VideoSegment 发送返回空 message_id，当前 ShiroBot/适配器可能不返回有效消息 ID；不再按失败处理。scene={scene}");
        }
    }

    private void CleanupLocalVideoAfterSend(BilibiliParseResult result)
    {
        if (result.LocalVideoRegisteredToHttpServer && config.DeleteLocalVideoDelaySeconds <= 0)
        {
            _hostServices.UnregisterLocalVideoFile(result.LocalVideoPath);
            result.LocalVideoRegisteredToHttpServer = false;
        }

        _hostServices.DeleteLocalVideoIfConfigured(config, result.LocalVideoPath, "bilibili");
    }

    private Task<string> UploadVideoFileAsync(IncomingMessage message, BilibiliParseResult result)
    {
        return _hostServices.UploadLocalVideoFileAsync(config, message, result.LocalVideoPath, "Bilibili", result.Bvid);
    }

    private Task<SendMessageResult> SendReplyAsync(IncomingMessage message, string text)
    {
        return _hostServices.ReplyTextAsync(config, message, text);
    }

    private static string NormalizePageReplyText(string text)
    {
        return text.Trim().Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』').Trim();
    }

    private void SubscribeBilibiliPageReply(BilibiliMultiPageParseResult result, MessageReference promptMessage)
    {
        IReplySubscription? subscription;
        subscription = context.Message.SubscribeReply(promptMessage, TimeSpan.FromMinutes(10), async reply =>
        {
            var text = reply.GetPlainText();

            if (!int.TryParse(NormalizePageReplyText(text), out var page) || page <= 0)
            {
                await ReplyAsync(reply, $"请回复 1 到 {result.PageCount} 之间的数字来解析指定分P。");
                return;
            }

            if (page > result.PageCount)
            {
                await ReplyAsync(reply, $"该视频只有 {result.PageCount} 个分P，请回复 1 到 {result.PageCount} 之间的数字。");
                return;
            }

            await ParseAndReplyAsync(reply, $"https://www.bilibili.com/video/{result.Bvid}/?p={page}");
        }, disposeOnReply: false);

        lock (_subscriptionLock)
        {
            _replySubscriptions.Add(subscription);
        }
    }

    private void DisposeReplySubscriptions()
    {
        IReplySubscription[] subscriptions;
        lock (_subscriptionLock)
        {
            subscriptions = _replySubscriptions.ToArray();
            _replySubscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void LogFinalVideoFileInfo(BilibiliParseResult result)
    {
        if (!config.LogSelectedQualityInfo)
        {
            return;
        }

        var selected = result.SelectedVideo;
        var fileSize = !string.IsNullOrWhiteSpace(result.LocalVideoPath) && File.Exists(result.LocalVideoPath) ? new FileInfo(result.LocalVideoPath).Length : 0;
        BotLog.Info(
            "MyParser Bilibili 最终发送视频信息: "
            + $"bvid={result.Bvid}, "
            + $"quality={(selected?.QualityName ?? "unknown")}, "
            + $"fps={(selected?.Fps ?? 0)}, "
            + $"bitrate_kbps={(selected is { Bandwidth: > 0 } ? selected.Bandwidth / 1000 : 0)}, "
            + $"size={(selected is null ? "0x0" : $"{selected.Width}x{selected.Height}")}, "
            + $"codec={(selected?.CodecName ?? "unknown")}, "
            + $"file_mb={(fileSize > 0 ? fileSize / 1024d / 1024d : 0):F2}, "
            + $"file={result.LocalVideoPath}");
    }

    private static void LogBilibiliQualityInfo(BilibiliParseResult result)
    {
        var selected = result.SelectedVideo;
        BotLog.Info($"MyParser Bilibili 选中画质: bvid={result.Bvid}, quality={selected?.QualityName}, fps={selected?.Fps}, size={selected?.Width}x{selected?.Height}, codec={selected?.CodecName}, total_options={result.VideoStreams.Count}");
        foreach (var (stream, index) in result.VideoStreams.Take(12).Select((stream, index) => (stream, index + 1)))
        {
            BotLog.Info($"MyParser Bilibili 可用画质: #{index}, quality={stream.QualityName}, fps={stream.Fps}, bitrate_kbps={stream.Bandwidth / 1000}, size={stream.Width}x{stream.Height}, codec={stream.CodecName}");
        }
    }

    private static IEnumerable<string> SplitText(string text, int chunkSize)
    {
        return ProviderTextUtilities.SplitText(text.Trim(), chunkSize);
    }

    private static string FormatLiveStatus(int status) => status switch
    {
        0 => "未开播",
        1 => "直播中",
        2 => "轮播",
        _ => status.ToString(),
    };

    private static string BuildAuthorMeta(BilibiliParseResult result)
    {
        var parts = new List<string>();
        if (result.ViewCount > 0)
        {
            parts.Add($"{FormatCount(result.ViewCount)}播放");
        }

        if (result.ReplyCount > 0)
        {
            parts.Add($"{FormatCount(result.ReplyCount)}评论");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "Bilibili UP主";
    }

    private static string BuildCardDescription(BilibiliParseResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            return result.Description;
        }

        return string.IsNullOrWhiteSpace(result.PartTitle) ? string.Empty : $"P{result.Page} · {result.PartTitle}";
    }

    private static string FormatDurationText(long seconds)
    {
        if (seconds <= 0)
        {
            return "--:--";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
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

    private string FormatBilibiliResult(BilibiliParseResult result, bool videoDownloadAttempted = false, bool videoSent = false, string? videoSendError = null, bool fileUploaded = false, string? fileUploadInfo = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bilibili 视频解析成功");
        sb.AppendLine($"BV：{result.Bvid}");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine($"标题：{TrimLine(result.Title, 140)}");
        if (!string.IsNullOrWhiteSpace(result.PartTitle)) sb.AppendLine($"分P：P{result.Page} {TrimLine(result.PartTitle, 80)}");
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine($"UP主：{result.AuthorName}");
        var selected = result.SelectedVideo;
        if (selected is not null) sb.AppendLine($"清晰度：{selected.QualityName} {selected.Width}x{selected.Height} {selected.Fps:0.###}fps {selected.CodecName}");

        var videoStatus = videoSent
            ? "视频：已下载音视频流、SharpMP4 合并（不支持时回退 ffmpeg），并已调用 VideoSegment 发送接口"
            : videoDownloadAttempted
                ? $"视频：下载/合并/发送未完成；原因：{TrimLine(videoSendError ?? "未知错误", 100)}"
                : "视频：已解析，未下载发送";
        sb.AppendLine(videoStatus);
        if (config.UploadVideoAsFile)
        {
            sb.AppendLine(fileUploaded ? $"文件上传：已上传为{fileUploadInfo}" : $"文件上传：未执行或未完成；原因：{TrimLine(fileUploadInfo ?? "未知", 80)}");
        }

        return sb.ToString().TrimEnd();
    }


    private static string TrimLine(string value, int maxLength) => ProviderTextUtilities.TrimLine(value, maxLength);

    public override void Dispose()
    {
        DisposeReplySubscriptions();
        
    }
}
