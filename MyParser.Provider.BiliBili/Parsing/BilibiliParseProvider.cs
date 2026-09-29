using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Parsing;
using ShiroBot.SDK.Models;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliParseProvider(BilibiliParser parser) : IIncomingMessageParseProvider, IProviderParseTextMatcher, IParseProviderWithParser, IProviderPriority, IDisposable
{
    public BilibiliParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public string Id => "bilibili";
    public string Name => "Bilibili";
    public int Priority => 50;

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ClassifyLink(text) == BilibiliLinkKind.Video;
    }

    public string? TryNormalizeParseText(string text, ProviderParseTextContext context)
    {
        if (context.IsUrlLike)
        {
            return BilibiliUrlParser.ExtractStrictBilibiliUrl(text);
        }

        return context.IsAutoParse
            ? BilibiliUrlParser.NormalizeStandaloneBvid(text)
            : BilibiliUrlParser.NormalizeStandaloneVideoId(text);
    }

    public string? ExtractParseText(IncomingMessage message)
    {
        var text = BilibiliLightAppUrlExtractor.ExtractParseText(message);
        return BilibiliUrlParser.ExtractStrictBilibiliUrl(text ?? string.Empty);
    }

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseMediaAsync(text, cancellationToken);
        return result switch
        {
            BilibiliMultiPageParseResult multi => new ParsedMedia
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = multi.Bvid,
                SourceUrl = multi.SourceUrl,
                Title = multi.Title,
                AuthorName = multi.AuthorName,
                AuthorId = multi.AuthorId,
                CoverUrl = multi.CoverUrl,
                Description = multi.Description,
                Tags = [],
                Kind = ParsedMediaKind.Collection,
                Assets = multi.Pages.Select(page => new MediaAsset
                {
                    Kind = MediaAssetKind.Video,
                    Url = page.SourceUrl,
                    Label = page.PartTitle,
                    CacheKey = $"bilibili:{multi.Bvid}:p{page.Page}:cid{page.Cid}",
                }).ToArray(),
                Attributes = new Dictionary<string, string>
                {
                    ["aid"] = multi.Aid.ToString(), ["pages"] = multi.PageCount.ToString(),
                    ["requested_page"] = multi.RequestedPage.ToString(),
                },
                Content = [new MediaContentBlock { Kind = MediaContentKind.Text, Text = multi.Description ?? string.Empty }],
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
                    {
                        Kind = MediaAssetKind.Video, Url = stream.Url, Label = stream.QualityName, BackupUrls = stream.BackupUrls,
                        Referer = video.SourceUrl, CacheKey = $"bilibili:{video.Bvid}:p{video.Page}:cid{video.Cid}",
                        FileNamePrefix = "bilibili", DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory,
                        RequestHeaders = CreateMediaHeaders(video.SourceUrl),
                        QualityId = stream.QualityId, Width = stream.Width, Height = stream.Height, FrameRate = stream.Fps, Codec = stream.CodecName,
                    })
                    .Concat(video.AudioStreams.Select(stream => new MediaAsset
                    {
                        Kind = MediaAssetKind.Audio, Url = stream.Url, Label = stream.QualityName, BackupUrls = stream.BackupUrls,
                        Referer = video.SourceUrl, CacheKey = $"bilibili:{video.Bvid}:p{video.Page}:cid{video.Cid}",
                        FileNamePrefix = "bilibili", DownloadDirectory = MyParserRuntime.BilibiliDownloadDirectory,
                        RequestHeaders = CreateMediaHeaders(video.SourceUrl),
                        QualityId = stream.QualityId, Codec = stream.CodecName,
                    }))
                    .ToArray(),
                Attributes = new Dictionary<string, string>
                {
                    ["duration_seconds"] = video.DurationSeconds.ToString(), ["views"] = video.ViewCount.ToString(),
                    ["likes"] = video.LikeCount.ToString(), ["coins"] = video.CoinCount.ToString(),
                    ["favorites"] = video.FavoriteCount.ToString(), ["shares"] = video.ShareCount.ToString(),
                    ["replies"] = video.ReplyCount.ToString(), ["part_title"] = video.PartTitle ?? string.Empty,
                },
                Content = [new MediaContentBlock { Kind = MediaContentKind.Text, Text = video.Description ?? string.Empty }],
            },
            _ => throw new BilibiliParseException("Bilibili 视频解析返回了未知结果类型。"),
        };
    }

    public void Dispose() => Parser.Dispose();

    private static IReadOnlyDictionary<string, string> CreateMediaHeaders(string? referer)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = BilibiliConstants.UserAgent,
            ["Referer"] = referer ?? BilibiliConstants.Origin + "/",
            ["Origin"] = BilibiliConstants.Origin,
            ["Accept"] = "*/*",
        };
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            headers["Cookie"] = MyParserRuntime.BilibiliCookie;
        }

        return headers;
    }
}
