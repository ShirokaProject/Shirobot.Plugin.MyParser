using System.Net;
using System.Text.RegularExpressions;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Models;
using VideoLibrary;

namespace MyParser.Provider.YouTube;

[MyParserProvider("youtube")]
public sealed class YouTubeProviderModule : MyParserProviderModuleBase, IProviderAutoParsePolicy, IProviderResultMessageClassifier
{
    public override string Id => "youtube";
    public override string DisplayName => "YouTube";
    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config) => [new YouTubeParseProvider(config)];
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

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var id = TryExtractVideoId(text) ?? throw new InvalidOperationException("未找到 YouTube 视频链接。");
        var url = $"https://www.youtube.com/watch?v={id}";
        var youtube = string.IsNullOrWhiteSpace(config.HttpProxy)
            ? VideoLibrary.YouTube.Default
            : new ProxiedYouTube(config.HttpProxy);
        var streams = (await youtube.GetAllVideosAsync(url)
            .WaitAsync(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 300)), cancellationToken)).ToList();
        var videos = streams.Where(v => v is { Format: VideoFormat.Mp4, AdaptiveKind: AdaptiveKind.Video, Resolution: > 0, FormatCode: >= 394 and <= 402 })
            .OrderByDescending(v => v.Resolution).ToArray();
        var audios = streams.Where(v => v is { AudioFormat: AudioFormat.Aac, FormatCode: 139 or 140 or 141 or 599 })
            .OrderByDescending(v => v.AudioBitrate).ToArray();
        if (videos.Length == 0) throw new InvalidOperationException("未找到可用的 AV1 MP4 视频流。");
        if (audios.Length == 0) throw new InvalidOperationException("未找到可用的 AAC 音频流。");
        var result = new YouTubeResult(id, videos[0].Title, url,
            videos.Select(v => ToStream(v, false)).ToArray(), audios.Select(v => ToStream(v, true)).ToArray());
        return new ParsedMedia
        {
            ProviderId = Id, ProviderName = Name, MediaId = id, SourceUrl = url,
            Title = result.Title, CoverUrl = result.CoverUrl, Kind = ParsedMediaKind.Video,
            Assets = result.Videos.Select(stream => new MediaAsset
                { Kind = MediaAssetKind.Video, Url = stream.Url, Label = stream.QualityName, CacheKey = $"youtube:{id}", FileNamePrefix = "youtube", DownloadDirectory = MyParserRuntime.YouTubeDownloadDirectory, QualityId = stream.QualityId, Height = stream.Height, Codec = stream.CodecName, RequestHeaders = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0" } })
                .Concat(result.Audios.Select(stream => new MediaAsset
                { Kind = MediaAssetKind.Audio, Url = stream.Url, Label = stream.QualityName, CacheKey = $"youtube:{id}", FileNamePrefix = "youtube", DownloadDirectory = MyParserRuntime.YouTubeDownloadDirectory, QualityId = stream.QualityId, Codec = stream.CodecName, RequestHeaders = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0" } })).ToArray(),
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

internal sealed class ProxiedYouTube(string proxyAddress) : VideoLibrary.YouTube
{
    protected override HttpMessageHandler MakeHandler()
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri(VideoLibrary.YouTube.YoutubeUrl), new Cookie("CONSENT", "YES+cb", "/", ".youtube.com"));
        var proxy = HttpProxySettings.Create(proxyAddress);
        return new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = proxy is not null,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
    }
}
