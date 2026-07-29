using System.Diagnostics;
using System.Text;
using ShiroBot.AvaloniaSdk;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Views;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;


namespace MyParser.Provider.BiliBili.MessageHandling;

internal sealed partial class BilibiliMessageHandler
{
private async Task TrySendLiveReplayClipAsync(IncomingMessage message, BilibiliLiveParseResult result)
    {
        if (!config.SendBilibiliLiveReplayClip)
        {
            return;
        }

        var shouldCleanup = false;
        try
        {
            var clipSeconds = Math.Clamp(config.BilibiliLiveReplayClipSeconds, 3, 3000);
            await ReplyAsync(message, $"正在从当前直播流可回溯分片中截取最近约 {clipSeconds} 秒，请稍候…");
            var clip = await _hostServices.DownloadLiveReplayClipAsync(config, BuildBilibiliLiveReplayClipDownloadRequest(result));
            result.LocalClipPath = clip.LocalPath;
            result.LocalClipFileUri = clip.FileUri;
            shouldCleanup = true;
            var segment = await BuildLocalVideoSegmentAsync(clip.LocalPath, clip.FileUri, result);
            await SendLiveClipVideoMessageAsync(message, result, segment, clip.Stream);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 直播片段发送未完成: room_id={result.RealRoomId}, detail={ex.Message}");
            await ReplyAsync(message, "直播回看片段截取/发送未完成：" + ex.Message);
        }
        finally
        {
            if (shouldCleanup)
            {
                CleanupLocalLiveClipAfterSend(result);
            }
        }
    }

    private async Task<VideoOutgoingSegment> BuildLocalVideoSegmentAsync(string localPath, string fileUri, BilibiliLiveParseResult result)
    {
        var segmentResult = await _hostServices.BuildLocalVideoSegmentAsync(config, new ProviderLocalVideoSegmentRequest(
            "Bilibili 直播片段",
            result.RealRoomId,
            localPath,
            fileUri,
            result.CoverUrl,
            "room_id"));
        result.LocalClipRegisteredToHttpServer = segmentResult.RegisteredToHttpServer;
        return segmentResult.Segment;
    }

    private async Task SendLiveClipVideoMessageAsync(IncomingMessage message, BilibiliLiveParseResult result, VideoOutgoingSegment videoSegment, ProviderLiveReplayStream stream)
    {
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser Bilibili 直播片段 VideoSegment 发送开始: room_id={result.RealRoomId}, scene={GetMessageScene(message)}, stream={stream.Protocol}/{stream.Format}/{stream.Codec}, qn={stream.CurrentQn}, uri_mode={_hostServices.GetUriMode(videoSegment.Uri)}, uri_preview={_hostServices.PreviewUri(videoSegment.Uri)}");
        var response = await context.Message.ReplyAsync(message, videoSegment);
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser Bilibili 直播片段 VideoSegment 发送接口完成: room_id={result.RealRoomId}, scene={scene}, message_id={response.MessageId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
        EnsureVideoSendAccepted(response.MessageId, scene);
    }

    private static ProviderLiveReplayClipDownloadRequest BuildBilibiliLiveReplayClipDownloadRequest(BilibiliLiveParseResult result)
    {
        return new ProviderLiveReplayClipDownloadRequest(
            "bilibili",
            "Bilibili",
            result.RealRoomId,
            MyParserRuntime.BilibiliDownloadDirectory,
            result.Streams.Select(ToProviderLiveReplayStream).ToArray(),
            (method, url, range) => CreateBilibiliLiveRequest(method, url, result.SourceUrl, range, accept: "application/vnd.apple.mpegurl, application/x-mpegURL, */*", noCache: true),
            (method, url, range) => CreateBilibiliLiveRequest(method, url, result.SourceUrl, range, accept: "*/*", noCache: false),
            StreamCompatibilityRank,
            "room_id");
    }

    private static ProviderLiveReplayStream ToProviderLiveReplayStream(BilibiliLiveStream stream)
    {
        return new ProviderLiveReplayStream(
            stream.Protocol,
            stream.Format,
            stream.Codec,
            stream.CurrentQn,
            stream.CdnIndex,
            stream.Url);
    }

    private static HttpRequestMessage CreateBilibiliLiveRequest(HttpMethod method, string url, string referer, string? range, string accept, bool noCache)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", string.IsNullOrWhiteSpace(referer) ? "https://live.bilibili.com/" : referer);
        request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (noCache)
        {
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        }

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

    private void CleanupLocalLiveClipAfterSend(BilibiliLiveParseResult result)
    {
        if (result.LocalClipRegisteredToHttpServer)
        {
            _hostServices.UnregisterLocalVideoFile(result.LocalClipPath);
            result.LocalClipRegisteredToHttpServer = false;
        }

        DeleteLocalLiveClipNow(result.LocalClipPath);
    }

    private static void DeleteLocalLiveClipNow(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(localPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir)
                && string.Equals(new DirectoryInfo(dir).Parent?.Name, "live-clips", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 直播片段临时文件删除失败: file={localPath}, error={ex.Message}");
        }
    }

    private async Task SendLiveForwardAsync(IncomingMessage message, BilibiliLiveParseResult result)
    {
        var cardUri = await BuildLiveCardUriAsync(result);
        var segment = new ImageOutgoingSegment(cardUri);
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser Bilibili 直播卡片 ImageSegment 发送开始: room_id={result.RealRoomId}, scene={GetMessageScene(message)}, uri_preview={_hostServices.PreviewUri(cardUri)}");

        var response = await context.Message.ReplyAsync(message, segment);
        BotLog.Info($"MyParser Bilibili 直播卡片 ImageSegment 发送接口完成: room_id={result.RealRoomId}, scene={GetMessageScene(message)}, message_id={response.MessageId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task<string> BuildLiveCardUriAsync(BilibiliLiveParseResult result)
    {
        var coverTask = BuildRemoteImageAsync(result.CoverUrl, result.SourceUrl, $"bilibili_live_cover_{result.RealRoomId}");
        var avatarTask = context.Render is null
            ? Task.FromResult(new ProviderImageBuildResult(string.Empty, null))
            : BuildRemoteImageAsync(result.AnchorAvatarUrl, result.SourceUrl, $"bilibili_live_avatar_{result.RealRoomId}");
        var coverImage = await coverTask;
        if (context.Render is null)
        {
            BotLog.Warning($"MyParser Bilibili Avalonia 渲染服务不可用，直接发送直播封面: room_id={result.RealRoomId}");
            return coverImage.Uri;
        }

        try
        {
            var avatarImage = await avatarTask;
            var coverBitmap = !string.IsNullOrWhiteSpace(coverImage.LocalPath)
                ? _hostServices.DecodeImageFileForRender(coverImage.LocalPath)
                : _hostServices.DecodeBase64ImageForRender(coverImage.Uri);
            var avatarBitmap = !string.IsNullOrWhiteSpace(avatarImage.LocalPath)
                ? _hostServices.DecodeImageFileForRender(avatarImage.LocalPath)
                : _hostServices.DecodeBase64ImageForRender(avatarImage.Uri);
            var vm = new BiliLiveCardViewModel
            {
                Cover = coverBitmap,
                Avatar = avatarBitmap,
                KindText = "Bilibili 直播",
                StatusText = FormatLiveStatus(result.LiveStatus),
                RoomIdText = $"房间：{result.RealRoomId}",
                Title = string.IsNullOrWhiteSpace(result.Title) ? "Bilibili 直播" : TrimLine(result.Title!, 80),
                AnchorName = string.IsNullOrWhiteSpace(result.AnchorName) ? "未知主播" : result.AnchorName,
                AudienceText = BuildLiveAudienceText(result),
                WatchedText = BuildLiveWatchedText(result),
                LiveStartTimeText = result.LiveStartTime is null ? "未知" : result.LiveStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                LiveDurationText = result.LiveDuration is null ? "未知" : FormatDuration(result.LiveDuration.Value),
            };
            var png = await context.RenderControlPngAsync<BiliLiveCard>(vm, new ControlRenderOptions(RenderTheme.Auto));
            BotLog.Info($"MyParser Bilibili 直播卡片渲染完成: room_id={result.RealRoomId}, cover_url={result.CoverUrl}, png_kb={png.Length / 1024d:F1}, view={typeof(BiliLiveCard).FullName}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 直播卡片渲染失败，直接发送直播封面: room_id={result.RealRoomId}, cover_url={result.CoverUrl}, error={ex.Message}");
            return coverImage.Uri;
        }
    }

    private static string BuildOfficialLivePlayerUrl(BilibiliLiveParseResult result)
    {
        return $"https://www.bilibili.com/blackboard/live/live-activity-player.html?enterTheRoom=0&cid={result.RealRoomId}";
    }

    private static string BuildLiveAudienceText(BilibiliLiveParseResult result)
    {
        return !string.IsNullOrWhiteSpace(result.RoomAudienceText)
            ? result.RoomAudienceText
            : result.RoomAudienceCount > 0
                ? FormatCount(result.RoomAudienceCount)
                : "未知";
    }

    private static string BuildLiveWatchedText(BilibiliLiveParseResult result)
    {
        return !string.IsNullOrWhiteSpace(result.WatchedText)
            ? result.WatchedText
            : result.WatchedCount > 0
                ? FormatCount(result.WatchedCount)
                : result.OnlineCount > 0
                    ? FormatCount(result.OnlineCount)
                    : "未知";
    }

    private static string BuildLiveHeaderText(BilibiliLiveParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bilibili 直播解析成功");
        sb.AppendLine($"房间：{result.RealRoomId}");
        sb.AppendLine($"状态：{FormatLiveStatus(result.LiveStatus)}");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine($"标题：{TrimLine(result.Title, 120)}");
        if (!string.IsNullOrWhiteSpace(result.AnchorName)) sb.AppendLine($"主播：{result.AnchorName}");
        if (!string.IsNullOrWhiteSpace(result.RoomAudienceText)) sb.AppendLine($"房间观众：{result.RoomAudienceText}");
        else if (result.RoomAudienceCount > 0) sb.AppendLine($"房间观众：{FormatCount(result.RoomAudienceCount)}");
        if (!string.IsNullOrWhiteSpace(result.WatchedText)) sb.AppendLine($"人气/看过：{result.WatchedText}");
        else if (result.WatchedCount > 0) sb.AppendLine($"人气/看过：{FormatCount(result.WatchedCount)}");
        else if (result.OnlineCount > 0) sb.AppendLine($"人气值：{FormatCount(result.OnlineCount)}");
        if (result.LiveStartTime is not null) sb.AppendLine($"开播时间：{result.LiveStartTime:yyyy-MM-dd HH:mm:ss}");
        if (result.LiveDuration is not null) sb.AppendLine($"直播时长：{FormatDuration(result.LiveDuration.Value)}");
        return sb.ToString().TrimEnd();
    }

    private static void AppendRecommendedLiveStreams(StringBuilder sb, BilibiliLiveParseResult result)
    {
        if (result.Streams.Count == 0)
        {
            return;
        }

        var bestQuality = result.Streams
            .OrderByDescending(i => i.CurrentQn)
            .ThenByDescending(StreamCodecQualityRank)
            .ThenBy(StreamCompatibilityRank)
            .ThenBy(i => i.CdnIndex)
            .Take(2)
            .ToList();
        var bestCompatibility = result.Streams
            .OrderBy(StreamCompatibilityRank)
            .ThenByDescending(i => i.CurrentQn)
            .ThenBy(i => i.CdnIndex)
            .FirstOrDefault();

        sb.AppendLine("推荐播放流：");
        foreach (var stream in bestQuality)
        {
            sb.AppendLine($"- 画质优先：{FormatLiveStreamSummary(stream)}");
        }

        if (bestCompatibility is not null && !bestQuality.Contains(bestCompatibility))
        {
            sb.AppendLine($"- 兼容优先：{FormatLiveStreamSummary(bestCompatibility)}");
        }
        else if (bestCompatibility is not null)
        {
            sb.AppendLine($"- 兼容优先：同上 {FormatLiveStreamSummary(bestCompatibility)}");
        }
    }

    private static string FormatLiveStreamSummary(BilibiliLiveStream stream)
    {
        var accept = stream.AcceptQn.Count > 0 ? $", 可选 qn={string.Join('/', stream.AcceptQn)}" : string.Empty;
        return $"{stream.Protocol}/{stream.Format}/{stream.Codec} qn={stream.CurrentQn} CDN#{stream.CdnIndex}{accept}";
    }

    private static int StreamCompatibilityRank(ProviderLiveReplayStream stream)
    {
        return StreamCompatibilityRank(stream.Protocol, stream.Format, stream.Codec);
    }

    private static int StreamCompatibilityRank(BilibiliLiveStream stream)
    {
        return StreamCompatibilityRank(stream.Protocol, stream.Format, stream.Codec);
    }

    private static int StreamCompatibilityRank(string protocol, string format, string codec)
    {
        return (protocol, format, codec) switch
        {
            ("http_hls", "ts", "avc") => 0,
            ("http_hls", "fmp4", "avc") => 1,
            ("http_stream", "flv", "avc") => 2,
            ("http_hls", "ts", "hevc") => 3,
            ("http_hls", "fmp4", "hevc") => 4,
            ("http_stream", "flv", "hevc") => 5,
            ("http_hls", "fmp4", "av1") => 6,
            _ => 99,
        };
    }

    private static int StreamCodecQualityRank(BilibiliLiveStream stream)
    {
        return stream.Codec.ToLowerInvariant() switch
        {
            "av1" => 3,
            "hevc" => 2,
            "avc" => 1,
            _ => 0,
        };
    }

    private static string BuildLiveStreamText(BilibiliLiveStream stream, int index)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#{index} {stream.Protocol}/{stream.Format}/{stream.Codec} qn={stream.CurrentQn} CDN#{stream.CdnIndex}");
        if (stream.AcceptQn.Count > 0)
        {
            sb.AppendLine("可选 qn：" + string.Join(", ", stream.AcceptQn));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}天 {duration:hh\\:mm\\:ss}"
            : duration.ToString(@"hh\:mm\:ss");
    }
}
