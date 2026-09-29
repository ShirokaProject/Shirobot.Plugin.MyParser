using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Utilities;
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

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseMediaAsync(text, cancellationToken);
        return result switch
        {
            BilibiliMultiPageParseResult multi => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = multi.Bvid,
                SourceUrl = multi.SourceUrl,
                Title = multi.Title,
                AuthorName = multi.AuthorName,
                AuthorId = multi.AuthorId,
                CoverUrl = multi.CoverUrl,
                MusicUrl = null,
                Tags = [],
                IsGallery = true,
                IsVideo = false,
                ProviderPayload = multi,
            },
            BilibiliParseResult video => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = video.Bvid,
                SourceUrl = video.SourceUrl,
                Title = video.Title,
                AuthorName = video.AuthorName,
                AuthorId = video.AuthorId,
                CoverUrl = video.CoverUrl,
                MusicUrl = null,
                Tags = [],
                IsGallery = false,
                IsVideo = video.IsVideo,
                ProviderPayload = video,
            },
            _ => throw new BilibiliParseException("Bilibili 视频解析返回了未知结果类型。"),
        };
    }

    public void Dispose() => Parser.Dispose();
}
