using MyParser.Provider.Heybox.Models;
using MyParser.Provider.Heybox.Parsing;
using ShiroBot.SDK.Models;

namespace MyParser.Provider.Heybox.Parsing;

public sealed class HeyboxParseProvider(HeyboxParser parser) : IIncomingMessageParseProvider, IParseProviderWithParser, IProviderPriority, IDisposable
{
    public HeyboxParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public string Id => "heybox";
    public string Name => "小黑盒";
    public int Priority => 60;

    public bool CanHandle(string text) => HeyboxParser.ContainsHeyboxUrl(text);

    public string? ExtractParseText(IncomingMessage message)
    {
        return HeyboxLightAppUrlExtractor.ExtractParseText(message);
    }

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        return new ParsedMedia
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.LinkId,
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = result.AuthorId,
            CoverUrl = result.CoverUrl,
            Description = result.Description,
            Tags = [],
            Kind = result.IsArticle ? ParsedMediaKind.Article : result.VideoUrls.Count > 0 ? ParsedMediaKind.Video
                : result.ImageUrls.Count > 0 ? ParsedMediaKind.Gallery : ParsedMediaKind.Other,
            Assets = result.VideoUrls.Select(url => new MediaAsset { Kind = MediaAssetKind.Video, Url = url, Referer = result.SourceUrl })
                .Concat(result.ImageUrls.Select(url => new MediaAsset { Kind = MediaAssetKind.Image, Url = url, Referer = result.SourceUrl }))
                .ToArray(),
            Content = result.Blocks.Select(block => new MediaContentBlock
            {
                Kind = block.Type switch
                {
                    HeyboxArticleBlockType.Image => MediaContentKind.Image,
                    HeyboxArticleBlockType.Video => MediaContentKind.Video,
                    _ => block.TextStyle == HeyboxArticleTextStyle.Heading ? MediaContentKind.Heading
                        : block.TextStyle == HeyboxArticleTextStyle.Quote ? MediaContentKind.Quote : MediaContentKind.Text,
                },
                Text = block.Text, Url = block.Url, Caption = block.Caption, Level = block.HeadingLevel,
            }).ToArray(),
            Attributes = new Dictionary<string, string>
            {
                ["views"] = result.ViewCount?.ToString() ?? string.Empty, ["likes"] = result.LikeCount?.ToString() ?? string.Empty,
                ["comments"] = result.CommentCount?.ToString() ?? string.Empty, ["source_kind"] = result.SourceKind ?? string.Empty,
                ["favorites"] = result.FavoriteCount?.ToString() ?? string.Empty, ["shares"] = result.ShareCount?.ToString() ?? string.Empty,
                ["video_count"] = result.VideoUrls.Count.ToString(), ["image_count"] = result.ImageUrls.Count.ToString(),
            },
        };
    }

    public void Dispose() => Parser.Dispose();
}
