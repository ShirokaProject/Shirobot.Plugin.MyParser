using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Parsing;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliBangumiParseProvider(BilibiliBangumiParser parser, BilibiliParser videoParser) : IProviderParseTextMatcher, IParseProviderWithParser, IProviderPriority
{
    public BilibiliBangumiParser Parser { get; } = parser;
    public object ParserObject => videoParser;

    public string Id => "bilibili-bangumi";
    public string Name => "Bilibili 番剧";
    public int Priority => 30;

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ClassifyLink(text) == BilibiliLinkKind.Bangumi;
    }

    public string? TryNormalizeParseText(string text, ProviderParseTextContext context)
    {
        if (context.IsUrlLike)
        {
            return BilibiliUrlParser.ExtractStrictBilibiliUrl(text);
        }

        return context.IsAutoParse ? null : BilibiliUrlParser.NormalizeStandaloneBangumiId(text);
    }

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        return result switch
        {
            BilibiliBangumiEpisodeVideoParseResult episode => new ParsedMedia
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = episode.Video.Bvid,
                SourceUrl = episode.Video.SourceUrl,
                Title = episode.Video.Title,
                AuthorName = episode.Video.AuthorName,
                AuthorId = episode.Video.AuthorId,
                CoverUrl = episode.Video.CoverUrl,
                Description = episode.Bangumi.Evaluate,
                Tags = episode.Bangumi.Styles.ToList(),
                Kind = ParsedMediaKind.Video,
                Assets = episode.Video.VideoStreams.Select(stream => new MediaAsset
                    { Kind = MediaAssetKind.Video, Url = stream.Url, Label = stream.QualityName, BackupUrls = stream.BackupUrls, Referer = episode.Video.SourceUrl, QualityId = stream.QualityId, Width = stream.Width, Height = stream.Height, FrameRate = stream.Fps, Codec = stream.CodecName, FileNamePrefix = "bilibili", DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory, RequestHeaders = CreateMediaHeaders(episode.Video.SourceUrl) })
                    .Concat(episode.Video.AudioStreams.Select(stream => new MediaAsset
                    { Kind = MediaAssetKind.Audio, Url = stream.Url, Label = stream.QualityName, BackupUrls = stream.BackupUrls, Referer = episode.Video.SourceUrl, QualityId = stream.QualityId, Codec = stream.CodecName, FileNamePrefix = "bilibili", DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory, RequestHeaders = CreateMediaHeaders(episode.Video.SourceUrl) }))
                    .ToArray(),
                Attributes = new Dictionary<string, string> { ["episode_title"] = episode.Bangumi.Title ?? string.Empty, ["duration_seconds"] = episode.Video.DurationSeconds.ToString() },
            },
            BilibiliParseResult video => new ParsedMedia
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = video.Bvid,
                SourceUrl = video.SourceUrl,
                Title = video.Title,
                AuthorName = video.AuthorName,
                AuthorId = video.AuthorId,
                CoverUrl = video.CoverUrl,
                Description = video.Description,
                Tags = [],
                Kind = ParsedMediaKind.Video,
                Assets = video.VideoStreams.Select(stream => new MediaAsset
                    { Kind = MediaAssetKind.Video, Url = stream.Url, Label = stream.QualityName, BackupUrls = stream.BackupUrls, Referer = video.SourceUrl, QualityId = stream.QualityId, Width = stream.Width, Height = stream.Height, FrameRate = stream.Fps, Codec = stream.CodecName, FileNamePrefix = "bilibili", DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory, RequestHeaders = CreateMediaHeaders(video.SourceUrl) })
                    .Concat(video.AudioStreams.Select(stream => new MediaAsset
                    { Kind = MediaAssetKind.Audio, Url = stream.Url, Label = stream.QualityName, BackupUrls = stream.BackupUrls, Referer = video.SourceUrl, QualityId = stream.QualityId, Codec = stream.CodecName, FileNamePrefix = "bilibili", DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory, RequestHeaders = CreateMediaHeaders(video.SourceUrl) }))
                    .ToArray(),
                Attributes = new Dictionary<string, string> { ["duration_seconds"] = video.DurationSeconds.ToString(), ["views"] = video.ViewCount.ToString(), ["likes"] = video.LikeCount.ToString() },
            },
            BilibiliBangumiParseResult bangumi => new ParsedMedia
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = bangumi.MediaId?.ToString() ?? bangumi.SeasonId?.ToString() ?? bangumi.RequestedEpId?.ToString() ?? string.Empty,
                SourceUrl = bangumi.MediaUrl ?? bangumi.SeasonUrl,
                Title = bangumi.Title,
                AuthorName = "Bilibili 番剧",
                AuthorId = null,
                CoverUrl = bangumi.CoverUrl,
                Description = bangumi.Evaluate,
                Tags = bangumi.Styles.ToList(),
                Kind = ParsedMediaKind.Collection,
                Assets = bangumi.Episodes.Select(episode => new MediaAsset
                    { Kind = MediaAssetKind.Video, Url = episode.Url, Label = episode.Title })
                    .Where(asset => !string.IsNullOrWhiteSpace(asset.Url)).ToArray(),
                Attributes = new Dictionary<string, string> { ["rating"] = bangumi.RatingText ?? string.Empty, ["publish"] = bangumi.PublishText ?? string.Empty },
            },
            _ => throw new BilibiliParseException("Bilibili 番剧解析返回了未知结果类型。"),
        };
    }

    private static IReadOnlyDictionary<string, string> CreateMediaHeaders(string? referer)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = BilibiliConstants.UserAgent,
            ["Referer"] = referer ?? BilibiliConstants.Origin + "/",
            ["Origin"] = BilibiliConstants.Origin,
            ["Accept"] = "*/*",
        };
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie)) headers["Cookie"] = MyParserRuntime.BilibiliCookie;
        return headers;
    }
}
