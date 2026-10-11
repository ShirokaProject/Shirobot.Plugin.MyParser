namespace MyParser.Provider.Heybox.Models;

public sealed class HeyboxParseResult
{
    public required string LinkId { get; init; }
    public required string SourceUrl { get; init; }
    public string? ResolvedUrl { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public string? CoverUrl { get; init; }
    public string? PlainText { get; init; }
    public IReadOnlyList<HeyboxArticleBlock> Blocks { get; init; } = [];
    public IReadOnlyList<string> ImageUrls { get; init; } = [];
    public IReadOnlyList<string> VideoUrls { get; init; } = [];
    public long? ViewCount { get; init; }
    public long? LikeCount { get; init; }
    public long? CommentCount { get; init; }
    public string? ShareUrl { get; init; }
    public string? SourceKind { get; init; }
    public bool IsArticle { get; init; }
    public long? FavoriteCount { get; init; }
    public long? ShareCount { get; init; }
    public DateTimeOffset? PublishTime { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = [];
}

public sealed record HeyboxArticleBlock
{
    public required HeyboxArticleBlockType Type { get; init; }
    public HeyboxArticleTextStyle TextStyle { get; init; } = HeyboxArticleTextStyle.Normal;
    public int HeadingLevel { get; init; }
    public bool IsBold { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
}

public enum HeyboxArticleBlockType
{
    Text,
    Image,
    Video,
}

public enum HeyboxArticleTextStyle
{
    Normal,
    Heading,
    Quote,
    ListItem,
}
