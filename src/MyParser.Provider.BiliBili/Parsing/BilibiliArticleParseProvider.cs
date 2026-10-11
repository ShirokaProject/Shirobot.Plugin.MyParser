using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Models;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliArticleParseProvider(BilibiliParser parser) : IProviderParseTextMatcher, IParseProvider, IProviderPriority
{
    public BilibiliParser Parser { get; } = parser;

    public string Id => "bilibili-article";
    public string Name => "Bilibili 专栏";
    public int Priority => 20;

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ClassifyLink(text) == BilibiliLinkKind.Article;
    }

    public string? TryNormalizeParseText(string text, ProviderParseTextContext context)
    {
        if (context.IsUrlLike)
        {
            return BilibiliUrlParser.ExtractStrictBilibiliUrl(text);
        }

        return context.IsAutoParse ? null : BilibiliUrlParser.NormalizeStandaloneArticleId(text);
    }

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseArticleAsync(text, cancellationToken);
        return new ParsedMedia
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.IsOpus ? "opus" + result.OpusId : "cv" + result.Cvid,
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = result.AuthorId,
            CoverUrl = result.BannerUrl,
            Description = result.Summary,
            Tags = result.Categories,
            Kind = ParsedMediaKind.Article,
            Assets = result.ImageUrls.Select(url => new MediaAsset
            {
                Kind = MediaAssetKind.Image, Url = url, Referer = result.SourceUrl,
            }).ToArray(),
            Content = result.Blocks.Select(block => new MediaContentBlock
            {
                Kind = block.Type == BilibiliArticleBlockType.Image ? MediaContentKind.Image
                    : block.TextStyle == BilibiliArticleTextStyle.Heading ? MediaContentKind.Heading
                    : block.TextStyle == BilibiliArticleTextStyle.Quote ? MediaContentKind.Quote
                    : MediaContentKind.Text,
                Text = block.Text, Url = block.Url, Caption = block.Caption, Level = block.HeadingLevel,
            }).ToArray(),
            Attributes = new Dictionary<string, string>
            {
                ["cvid"] = result.Cvid.ToString(), ["opus_id"] = result.OpusId ?? string.Empty,
                ["views"] = result.ViewCount.ToString(), ["likes"] = result.LikeCount.ToString(),
                ["words"] = result.Words.ToString(), ["publish_time"] = result.PublishTime?.ToString("O") ?? string.Empty,
                ["author_avatar_url"] = result.AuthorAvatarUrl ?? string.Empty, ["author_fans"] = result.AuthorFans.ToString(),
            },
        };
    }
}
