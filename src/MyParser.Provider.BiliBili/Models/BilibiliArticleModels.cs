namespace MyParser.Provider.BiliBili.Models;

public sealed record BilibiliArticleParseResult
{
    public required long Cvid { get; init; }
    public string? OpusId { get; init; }
    public bool IsOpus { get; init; }
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? ContentHtml { get; init; }
    public string? PlainText { get; init; }
    public string? BannerUrl { get; init; }
    public List<string> ImageUrls { get; init; } = [];
    public List<BilibiliArticleBlock> Blocks { get; init; } = [];
    public string? AuthorName { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public long AuthorFans { get; init; }
    public long ViewCount { get; init; }
    public long LikeCount { get; init; }
    public long CoinCount { get; init; }
    public long FavoriteCount { get; init; }
    public long ShareCount { get; init; }
    public long ReplyCount { get; init; }
    public long Words { get; init; }
    public DateTimeOffset? PublishTime { get; init; }
    public List<string> Categories { get; init; } = [];
}

public sealed record BilibiliArticleBlock
{
    public required BilibiliArticleBlockType Type { get; init; }
    public BilibiliArticleTextStyle TextStyle { get; init; } = BilibiliArticleTextStyle.Normal;
    public int HeadingLevel { get; init; }
    public int IndentLevel { get; init; }
    public bool IsBold { get; init; }
    public string? TextColor { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
}

public enum BilibiliArticleBlockType
{
    Text,
    Image,
}

public enum BilibiliArticleTextStyle
{
    Normal,
    Heading,
    Quote,
    Code,
    ListItem,
}
