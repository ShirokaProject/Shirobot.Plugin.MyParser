using System.Net;
using ShiroBot.SDK.Models;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal sealed class UnifiedMediaMessageHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    private readonly ProviderMediaCardRenderer _cardRenderer = new(context.BotContext, context.HostServices);
    public override string ProviderId => PrimaryProvider.Id;

    protected override async Task PresentAsync(
        MessageEvent message,
        ParsedMedia media,
        bool silentProviderMismatch,
        CancellationToken cancellationToken)
    {
        await ReplyAsync(message, FormatSummary(media)).ConfigureAwait(false);

        if (Config.IsCoverEnabled(media.ProviderId))
        {
            await SendCoverAsync(message, media, cancellationToken).ConfigureAwait(false);
        }

        if (media.Kind == ParsedMediaKind.Track
            && Config.SendNetEaseCloudMusicLyricCard
            && media.Attributes.TryGetValue("lyrics", out var lyrics)
            && !string.IsNullOrWhiteSpace(lyrics))
        {
            var translated = media.Attributes.GetValueOrDefault("translated_lyrics");
            var lyricMedia = media with
            {
                Title = $"{media.Title} · 歌词",
                Description = string.IsNullOrWhiteSpace(translated) ? lyrics : lyrics + Environment.NewLine + translated,
                Attributes = new Dictionary<string, string>(),
            };
            var lyricCard = await RenderCardAsync(lyricMedia, ProviderCardPurpose.Lyrics, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lyricCard)) await SendImageAsync(message, new ImageSegment(lyricCard)).ConfigureAwait(false);
        }

        var hasComments = media.Content.Any(block => block.Attributes.GetValueOrDefault("category") == "comment");
        if (hasComments)
        {
            string? commentsCard = null;
            if (CardRenderer is not null)
            {
                try
                {
                    commentsCard = await CardRenderer.RenderAsync(media, ProviderCardPurpose.Comments, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser {media.ProviderName} 评论卡片渲染失败，回退文本: media_id={media.MediaId}, error={ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(commentsCard))
            {
                await SendImageAsync(message, new ImageSegment(commentsCard)).ConfigureAwait(false);
            }
            else
            {
                await SendContentAsync(message, media, cancellationToken).ConfigureAwait(false);
            }
        }

        var succeeded = true;
        switch (media.Kind)
        {
            case ParsedMediaKind.Video:
                if (Config.IsVideoDeliveryEnabled())
                {
                    succeeded = await SendVideoAsync(message, media, cancellationToken).ConfigureAwait(false);
                }
                break;
            case ParsedMediaKind.Track:
                succeeded = await SendTrackAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Article:
                await SendContentAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Gallery:
                await SendGalleryAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Collection:
                await SendCollectionAsync(message, media).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Live:
                succeeded = await SendLiveInfoAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (succeeded) await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
    }

    private async Task SendCoverAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(media.CoverUrl)) return;

        var purpose = media.Kind == ParsedMediaKind.Article ? ProviderCardPurpose.Article : ProviderCardPurpose.Main;
        var uri = await RenderCardAsync(media, purpose, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(uri)) await SendImageAsync(message, new ImageSegment(uri)).ConfigureAwait(false);
    }

    private async Task<string?> RenderCardAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken)
    {
        if (CardRenderer is not null)
        {
            try
            {
                var custom = await CardRenderer.RenderAsync(media, purpose, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser {media.ProviderName} 专属卡片渲染失败，回退统一卡片: media_id={media.MediaId}, error={ex.Message}");
            }
        }

        return await _cardRenderer.RenderAsync(media, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendVideoAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        var videos = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Video).ToArray();
        if (videos.Length == 0) return true;

        var audios = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Audio).ToArray();
        string? localPath = null;
        var registered = false;
        try
        {
            if (PrimaryProvider is IParseProviderWithParser parserProvider
                && parserProvider.ParserObject is IVideoDownloadGate downloadGate)
            {
                downloadGate.EnsureVideoDownloadAllowed();
            }

            string fileUri;
            if (audios.Length > 0)
            {
                var request = new ProviderMuxedVideoDownloadRequest(
                    media.ProviderId,
                    media.ProviderName,
                    media.MediaId,
                    videos[0].CacheKey ?? $"{media.ProviderId}:{media.MediaId}",
                    media.Title,
                    videos[0].DownloadDirectory,
                    videos.Select(ToMuxedStream).ToArray(),
                    audios.Select(ToMuxedStream).ToArray(),
                    CreateRequestFactory(videos[0]),
                    "media_id");
                (fileUri, localPath) = await HostServices.DownloadMuxedProviderVideoAsync(Config, request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var selected = videos[0];
                var request = new ProviderVideoDownloadRequest(
                    media.ProviderId,
                    media.ProviderName,
                    media.MediaId,
                    selected.CacheKey ?? $"{media.ProviderId}:{media.MediaId}",
                    videos.SelectMany(asset => string.IsNullOrWhiteSpace(asset.Url) ? asset.BackupUrls : new[] { asset.Url }.Concat(asset.BackupUrls))
                        .Distinct(StringComparer.Ordinal).ToArray(),
                    selected.DownloadDirectory,
                    selected.FileNamePrefix ?? media.ProviderId,
                    selected.FileExtension,
                    CreateRequestFactory(selected),
                    ProviderVideoValidationKind.Mp4,
                    "media_id");
                (fileUri, localPath) = await HostServices.DownloadProviderVideoAsync(Config, request, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var segment = await HostServices.BuildLocalVideoSegmentAsync(Config,
                new ProviderLocalVideoSegmentRequest(media.ProviderName, media.MediaId, localPath, fileUri, media.CoverUrl),
                cancellationToken).ConfigureAwait(false);
            registered = segment.RegisteredToHttpServer;
            var sent = await HostServices.SendSegmentsAsync(message, [segment.Segment]).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sent.MessageId))
            {
                throw new InvalidOperationException("视频发送没有返回消息 ID。");
            }

            if (Config.IsVideoFileUploadEnabled() && !Config.UploadVideoAsFileOnlyOnVideoSendFailure)
            {
                await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, media.ProviderName, media.MediaId).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is IProviderClassifiedException { FailureKind: ProviderFailureKind.AuthenticationRequired })
            {
                throw;
            }

            BotLog.Warning($"MyParser {media.ProviderName} 视频投递失败: media_id={media.MediaId}, error={ex.Message}");
            if (Config.IsVideoFileUploadEnabled() && !string.IsNullOrWhiteSpace(localPath))
            {
                try
                {
                    var upload = await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, media.ProviderName, media.MediaId).ConfigureAwait(false);
                    await ReplyAsync(message, $"视频已解析，VideoSegment 发送失败，已回退上传文件：{upload}").ConfigureAwait(false);
                    await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
                    return true;
                }
                catch (Exception uploadException)
                {
                    BotLog.Warning($"MyParser {media.ProviderName} 视频文件回退上传失败: media_id={media.MediaId}, error={uploadException.Message}");
                }
            }

            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"media_id={media.MediaId}").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (registered && Config.DeleteLocalVideoDelaySeconds <= 0)
            {
                HostServices.UnregisterLocalVideoFile(localPath);
            }

            HostServices.DeleteLocalVideoIfConfigured(Config, localPath, media.ProviderId);
        }
    }

    private async Task<bool> SendTrackAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        var audio = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio);
        if (audio is null) return true;

        string? localAudioPath = null;
        try
        {
            var request = new ProviderAudioDownloadRequest(
                media.ProviderId, media.ProviderName, media.MediaId,
                audio.CacheKey ?? $"{media.ProviderId}:{media.MediaId}", audio.Url,
                audio.DownloadDirectory, audio.FileNamePrefix ?? media.Title ?? media.MediaId,
                audio.FileExtension, CreateRequestFactory(audio), "media_id");
            var (_, localPath) = await HostServices.DownloadProviderAudioAsync(Config, request, cancellationToken).ConfigureAwait(false);
            localAudioPath = localPath;
            var variants = await HostServices.BuildSilkRecordVariantsAsync(Config,
                new ProviderRecordBuildRequest(media.ProviderId, media.ProviderName, media.MediaId, localPath,
                    audio.FileNamePrefix ?? media.Title ?? media.MediaId, Config.SendNetEaseMobileBestRecord), cancellationToken).ConfigureAwait(false);
            foreach (var variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = await HostServices.BuildRecordUriAsync(variant.Path, cancellationToken).ConfigureAwait(false);
                var sent = await HostServices.SendSegmentsAsync(message, [new RecordOutgoingSegment(uri)]).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("音频发送没有返回消息 ID。");
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 音频投递失败: media_id={media.MediaId}, error={ex.Message}");
            try
            {
                var upload = await HostServices.UploadLocalFileAsync(Config, message, localAudioPath,
                    media.ProviderName, media.MediaId, preferBase64: true).ConfigureAwait(false);
                await ReplyAsync(message, $"音频发送失败，已回退文件上传：{upload}").ConfigureAwait(false);
                await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
                return true;
            }
            catch (Exception uploadException)
            {
                BotLog.Warning($"MyParser {media.ProviderName} 音频文件回退失败: media_id={media.MediaId}, error={uploadException.Message}");
            }
            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"media_id={media.MediaId}").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
    }

    private async Task SendContentAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        foreach (var block in media.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Kind == MediaContentKind.Image)
            {
                await SendRemoteImageAsync(message, block.Url, block.Caption, media, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (block.Kind == MediaContentKind.Video)
            {
                var video = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Video
                    && string.Equals(asset.Url, block.Url, StringComparison.Ordinal));
                if (video is not null && Config.IsVideoDeliveryEnabled())
                {
                    var videoMedia = media with { Kind = ParsedMediaKind.Video, Assets = [video] };
                    if (!await SendVideoAsync(message, videoMedia, cancellationToken).ConfigureAwait(false))
                    {
                        await ReplyAsync(message, block.Url).ConfigureAwait(false);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(block.Url))
                {
                    await ReplyAsync(message, $"正文视频：{block.Url}").ConfigureAwait(false);
                }

                continue;
            }

            var text = block.Attributes.GetValueOrDefault("category") == "comment"
                ? $"{block.Caption}: {block.Text}"
                : block.Kind == MediaContentKind.Heading ? $"【{block.Text}】" : block.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                await ReplyAsync(message, text).ConfigureAwait(false);
            }
        }
    }

    private async Task SendGalleryAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        foreach (var image in media.Assets.Where(asset => asset.Kind == MediaAssetKind.Image))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendRemoteImageAsync(message, image.Url, image.Label, media, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendCollectionAsync(MessageEvent message, ParsedMedia media)
    {
        foreach (var asset in media.Assets)
        {
            if (!string.IsNullOrWhiteSpace(asset.Url))
            {
                await ReplyAsync(message, $"{asset.Label ?? "条目"}：{asset.Url}").ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> SendLiveInfoAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        var streams = media.Assets.Where(asset => asset.Kind == MediaAssetKind.LiveStream).ToArray();
        if (streams.Length == 0)
        {
            return true;
        }

        await ReplyAsync(message, $"直播信息已解析，可用线路数：{streams.Length}。").ConfigureAwait(false);
        if (!Config.IsBilibiliLiveReplayEnabled()) return true;

        string? localPath = null;
        var registered = false;
        try
        {
            var replayStreams = streams.Select(asset => new ProviderLiveReplayStream(
                asset.Protocol, asset.Format, asset.Codec, asset.QualityId, asset.CdnIndex, asset.Url)).ToArray();
            var selected = streams[0];
            var request = new ProviderLiveReplayClipDownloadRequest(
                media.ProviderId, media.ProviderName, media.MediaId, selected.DownloadDirectory, replayStreams,
                CreateRequestFactory(selected), CreateRequestFactory(selected), GetLiveStreamRank, "room_id");
            var clip = await HostServices.DownloadLiveReplayClipAsync(Config, request, cancellationToken).ConfigureAwait(false);
            localPath = clip.LocalPath;
            var video = await HostServices.BuildLocalVideoSegmentAsync(Config,
                new ProviderLocalVideoSegmentRequest(media.ProviderName, media.MediaId, clip.LocalPath, clip.FileUri, media.CoverUrl, "room_id"),
                cancellationToken).ConfigureAwait(false);
            registered = video.RegisteredToHttpServer;
            var sent = await HostServices.SendSegmentsAsync(message, [video.Segment]).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("直播回看片段发送没有返回消息 ID。");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 直播回看片段投递失败: room_id={media.MediaId}, error={ex.Message}");
            if (Config.IsVideoFileUploadEnabled() && !string.IsNullOrWhiteSpace(localPath))
            {
                try
                {
                    var upload = await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, media.ProviderName, media.MediaId).ConfigureAwait(false);
                    await ReplyAsync(message, $"直播回看片段已回退为文件上传：{upload}").ConfigureAwait(false);
                    return true;
                }
                catch (Exception uploadException)
                {
                    BotLog.Warning($"MyParser {media.ProviderName} 直播回看片段文件上传失败: room_id={media.MediaId}, error={uploadException.Message}");
                }
            }

            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"room_id={media.MediaId}; feature=live-replay").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (registered && Config.DeleteLocalVideoDelaySeconds <= 0) HostServices.UnregisterLocalVideoFile(localPath);
            HostServices.DeleteLocalVideoIfConfigured(Config, localPath, media.ProviderId);
        }
    }

    private static int GetLiveStreamRank(ProviderLiveReplayStream stream) => (stream.Protocol, stream.Format, stream.Codec) switch
    {
        ("http_hls", "fmp4", "avc") => 0,
        ("http_hls", "ts", "avc") => 1,
        ("http_stream", "flv", "avc") => 2,
        ("http_hls", "fmp4", "hevc") => 3,
        ("http_hls", "ts", "hevc") => 4,
        ("http_stream", "flv", "hevc") => 5,
        ("http_hls", "fmp4", "av1") => 6,
        _ => 99,
    };

    private async Task SendRemoteImageAsync(MessageEvent message, string? url, string? label, ParsedMedia media, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var result = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                media.ProviderName, url, media.SourceUrl, $"{media.ProviderId}_{media.MediaId}_image",
                request => ApplyHeaders(request, GetImageHeaders(media)), PersistLocalFile: false), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Uri)) await SendImageAsync(message, new ImageSegment(result.Uri)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(label)) await ReplyAsync(message, label).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 正文图片发送失败: media_id={media.MediaId}, error={ex.Message}");
        }
    }

    private static string FormatSummary(ParsedMedia media)
    {
        var lines = new List<string> { $"{media.ProviderName}解析完成" };
        if (!string.IsNullOrWhiteSpace(media.Title)) lines.Add($"标题：{media.Title}");
        if (!string.IsNullOrWhiteSpace(media.AuthorName)) lines.Add($"作者：{media.AuthorName}");
        if (!string.IsNullOrWhiteSpace(media.Description)) lines.Add($"简介：{TextPreviewFormatter.TrimLine(media.Description, 240)}");
        foreach (var (key, value) in media.Attributes)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && key is not ("lyrics" or "translated_lyrics")
                && !key.Contains("url", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{key}：{value}");
            }
        }
        if (!string.IsNullOrWhiteSpace(media.SourceUrl)) lines.Add($"链接：{media.SourceUrl}");
        return string.Join(Environment.NewLine, lines);
    }

    private static ProviderMuxedMediaStream ToMuxedStream(MediaAsset asset) =>
        new(asset.QualityId.ToString(), asset.Url, asset.BackupUrls, asset.QualityId, asset.Label ?? string.Empty,
            asset.Width, asset.Height, asset.FrameRate, asset.Codec, asset.Kind == MediaAssetKind.Audio);

    private static Func<HttpMethod, string, string?, HttpRequestMessage> CreateRequestFactory(MediaAsset asset) =>
        (method, url, range) =>
        {
            var request = new HttpRequestMessage(method, url);
            ApplyHeaders(request, asset.RequestHeaders);
            if (string.IsNullOrWhiteSpace(asset.RequestHeaders.GetValueOrDefault("Referer")) && !string.IsNullOrWhiteSpace(asset.Referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", asset.Referer);
            }
            if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);
            return request;
        };

    private static IReadOnlyDictionary<string, string> GetCoverHeaders(ParsedMedia media) =>
        media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Image)?.RequestHeaders ?? new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> GetImageHeaders(ParsedMedia media) => GetCoverHeaders(media);

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "Referer", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var referer))
            {
                request.Headers.Referrer = referer;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }
}
