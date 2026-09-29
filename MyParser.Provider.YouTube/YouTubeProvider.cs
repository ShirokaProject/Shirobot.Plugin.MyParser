using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Models;
using VideoLibrary;

namespace MyParser.Provider.YouTube;

[MyParserProvider("youtube")]
public sealed class YouTubeProviderModule : MyParserProviderModuleBase, IProviderMessageHandlerFactory, IProviderAutoParsePolicy, IProviderResultMessageClassifier
{
    public override string Id => "youtube";
    public override string DisplayName => "YouTube";
    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config) => [new YouTubeParseProvider(config)];
    public IProviderMessageHandler CreateMessageHandler(ProviderMessageHandlerContext context) => new YouTubeMessageHandler(context);
    public bool IsAutoParseEnabled(PluginConfig config) => config.AutoParseYouTubeLinks;
    public bool IsPluginResultMessage(string text) => text.StartsWith("YouTube 解析", StringComparison.OrdinalIgnoreCase);
}

internal sealed record YouTubeResult(string Id, string Title, string Url, IReadOnlyList<ProviderMuxedMediaStream> Videos, IReadOnlyList<ProviderMuxedMediaStream> Audios)
{
    public string CoverUrl => $"https://i.ytimg.com/vi/{Id}/hqdefault.jpg";
}

internal sealed partial class YouTubeParseProvider(PluginConfig config) : IParseProvider
{
    public string Id => "youtube";
    public string Name => "YouTube";

    public bool CanHandle(string text) => TryExtractVideoId(text) is not null;

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var id = TryExtractVideoId(text) ?? throw new InvalidOperationException("未找到 YouTube 视频链接。");
        var url = $"https://www.youtube.com/watch?v={id}";
        var streams = (await VideoLibrary.YouTube.Default.GetAllVideosAsync(url)
            .WaitAsync(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 300)), cancellationToken)).ToList();
        var videos = streams.Where(v => v is { Format: VideoFormat.Mp4, AdaptiveKind: AdaptiveKind.Video, Resolution: > 0, FormatCode: >= 394 and <= 402 })
            .OrderByDescending(v => v.Resolution).ToArray();
        var audios = streams.Where(v => v is { AudioFormat: AudioFormat.Aac, FormatCode: 139 or 140 or 141 or 599 })
            .OrderByDescending(v => v.AudioBitrate).ToArray();
        if (videos.Length == 0) throw new InvalidOperationException("未找到可用的 AV1 MP4 视频流。");
        if (audios.Length == 0) throw new InvalidOperationException("未找到可用的 AAC 音频流。");
        var result = new YouTubeResult(id, videos[0].Title, url,
            videos.Select(v => ToStream(v, false)).ToArray(), audios.Select(v => ToStream(v, true)).ToArray());
        return new MediaParseResult
        {
            ProviderId = Id, ProviderName = Name, MediaId = id, SourceUrl = url,
            Title = result.Title, CoverUrl = result.CoverUrl, IsVideo = true, ProviderPayload = result,
        };
    }

    private static ProviderMuxedMediaStream ToStream(YouTubeVideo video, bool audio) =>
        new(video.FormatCode.ToString(), video.Uri, [], audio ? video.AudioBitrate : video.Resolution,
            audio ? $"{video.AudioBitrate}kbps AAC" : $"{video.Resolution}p AV1",
            0, audio ? 0 : video.Resolution, 0, audio ? "AAC" : "AV1", audio);

    private static string? TryExtractVideoId(string text)
    {
        foreach (Match match in LinkRegex().Matches(text))
        {
            var candidate = match.Value.Trim().TrimEnd('，', '。', ',', '.', ')', '）', ']', '】');
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !IsSupportedYouTubeHost(uri.Host)) continue;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? id = null;
            if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                id = segments.FirstOrDefault();
            }
            else if (segments.Length >= 2 && segments[0] is "shorts" or "embed" or "live")
            {
                id = segments[1];
            }
            else if (segments.Length == 1 && string.Equals(segments[0], "watch", StringComparison.OrdinalIgnoreCase))
            {
                id = GetQueryValue(uri.Query, "v");
            }

            if (id is not null && VideoIdRegex().IsMatch(id)) return id;
        }

        return null;
    }

    private static bool IsSupportedYouTubeHost(string host) => host.ToLowerInvariant() is
        "youtu.be" or "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com";

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }

        return null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9.-])(?:(?:https?://)?(?:www\.|m\.|music\.)?youtube\.com/[^\s<>\""']+|(?:https?://)?youtu\.be/[^\s<>\""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();
}

internal sealed class YouTubeMessageHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    public override string ProviderId => "youtube";
    public override async Task ParseAndReplyAsync(MessageEvent message, string text, bool silentProviderMismatch = false, CancellationToken cancellationToken = default)
    {
        string? localPath = null;
        var registered = false;
        try
        {
            await ReactAsync(message, "351", "YouTube");
            var media = await ProviderRegistry.ParseAsync(text, cancellationToken);
            if (media.ProviderPayload is not YouTubeResult result)
            {
                await ReportFailureAsync(message, ProviderFailureKind.Unexpected,
                    diagnosticContext: "provider-result-type-mismatch");
                return;
            }
            await ReplyAsync(message, $"YouTube 解析：{result.Title}\n{result.Url}");
            if (Config.IsCoverEnabled("youtube"))
            {
                try
                {
                    var cover = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest("YouTube",
                        result.CoverUrl, result.Url, $"youtube_cover_{result.Id}"), cancellationToken);
                    if (!string.IsNullOrWhiteSpace(cover.Uri)) await SendImageAsync(message, new ImageSegment(cover.Uri));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ShiroBot.SDK.Abstractions.BotLog.Warning($"YouTube 封面发送失败：{ex.Message}");
                }
            }
            if (!Config.IsVideoDeliveryEnabled())
            {
                await ReactAsync(message, "426", "YouTube");
                return;
            }
            var directory = MyParserRuntime.YouTubeDownloadDirectory;
            var download = await HostServices.DownloadMuxedProviderVideoAsync(Config,
                new ProviderMuxedVideoDownloadRequest("youtube", "YouTube", result.Id, $"youtube:{result.Id}", result.Title,
                    directory, result.Videos, result.Audios, CreateRequest), cancellationToken);
            localPath = download.LocalPath;
            var segment = await HostServices.BuildLocalVideoSegmentAsync(Config,
                new ProviderLocalVideoSegmentRequest("YouTube", result.Id, localPath, download.FileUri), cancellationToken);
            registered = segment.RegisteredToHttpServer;
            try
            {
                var sent = await HostServices.SendSegmentsAsync(message, [segment.Segment]);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("视频发送未返回消息 ID。");
                if (Config.IsVideoFileUploadEnabled() && !Config.UploadVideoAsFileOnlyOnVideoSendFailure)
                    await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, "YouTube", result.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && Config.IsVideoFileUploadEnabled())
            {
                await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, "YouTube", result.Id);
            }
            await ReactAsync(message, "426", "YouTube");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ReportFailureAsync(message, ProviderFailureKind.Unexpected, ex);
            await ReactAsync(message, "9", "YouTube");
        }
        finally
        {
            if (registered && Config.DeleteLocalVideoDelaySeconds <= 0) HostServices.UnregisterLocalVideoFile(localPath);
            HostServices.DeleteLocalVideoIfConfigured(Config, localPath, "youtube");
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? range)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        if (!string.IsNullOrWhiteSpace(range)) request.Headers.Range = RangeHeaderValue.Parse(range);
        return request;
    }
}
