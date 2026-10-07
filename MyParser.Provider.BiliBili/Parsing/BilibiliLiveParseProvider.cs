using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Parsing;
using MyParser.Provider.BiliBili.Infrastructure;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliLiveParseProvider(BilibiliLiveParser parser) : IProviderParseTextMatcher, IParseProvider, IProviderPriority
{
    public BilibiliLiveParser Parser { get; } = parser;

    public string Id => "bilibili-live";
    public string Name => "Bilibili 直播";
    public int Priority => 40;

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ClassifyLink(text) == BilibiliLinkKind.Live;
    }

    public string? TryNormalizeParseText(string text, ProviderParseTextContext context)
    {
        return context.IsUrlLike ? BilibiliUrlParser.ExtractStrictBilibiliUrl(text) : null;
    }

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        return new ParsedMedia
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.RealRoomId,
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.AnchorName,
            AuthorId = null,
            CoverUrl = result.CoverUrl,
            Description = result.RoomAudienceText,
            Tags = [],
            Kind = ParsedMediaKind.Live,
            Assets = result.Streams.Select(stream => new MediaAsset
            {
                Kind = MediaAssetKind.LiveStream, Url = stream.Url, Label = $"{stream.Protocol}/{stream.Format}/{stream.Codec}",
                Referer = result.SourceUrl, CacheKey = $"bilibili-live:{result.RealRoomId}",
                Protocol = stream.Protocol, Format = stream.Format, Codec = stream.Codec,
                QualityId = stream.CurrentQn, CdnIndex = stream.CdnIndex,
                DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory,
                RequestHeaders = CreateLiveHeaders(result.SourceUrl),
            }).ToArray(),
            Attributes = new Dictionary<string, string>
            {
                ["room_id"] = result.RealRoomId, ["live_status"] = result.LiveStatus.ToString(),
                ["online"] = result.OnlineCount.ToString(), ["watched"] = result.WatchedText ?? result.WatchedCount.ToString(),
                ["duration"] = result.LiveDuration?.ToString() ?? string.Empty,
                ["anchor_avatar_url"] = result.AnchorAvatarUrl ?? string.Empty,
                ["live_start"] = result.LiveStartTime?.ToString("O") ?? string.Empty,
                ["audience_text"] = result.RoomAudienceText ?? string.Empty,
            },
        };
    }

    private static IReadOnlyDictionary<string, string> CreateLiveHeaders(string? referer)
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
