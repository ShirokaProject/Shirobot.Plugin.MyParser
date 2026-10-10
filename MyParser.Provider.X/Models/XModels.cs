namespace MyParser.Provider.X.Models;

public sealed record XTweetData(
    string Id,
    string Text,
    string Lang,
    string AuthorName,
    string ScreenName,
    string? AvatarUrl,
    bool IsBlueVerified,
    long? Likes,
    long? Replies,
    long? Retweets,
    long? Views,
    DateTimeOffset? CreatedAt,
    bool PossiblySensitive,
    IReadOnlyList<XMediaItem> Media);

public sealed record XMediaItem(
    string Kind,
    bool IsMp4,
    int Width,
    int Height,
    string? Url,
    string? ThumbnailUrl,
    IReadOnlyList<XMediaVariant> Variants);

public sealed record XMediaVariant(string Url, int Bitrate, string ContentType);
