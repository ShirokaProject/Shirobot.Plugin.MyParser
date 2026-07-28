namespace MyParser.Provider.Douyin.Models;

public sealed record DouyinParseResult
{
    public required string AwemeId { get; init; }
    public bool IsIgnored { get; init; }
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public long AuthorFollowerCount { get; init; }
    public string? AuthorRegion { get; init; }
    public long DurationMilliseconds { get; init; }
    public long CreateTimeUnixSeconds { get; init; } = 0;
    public string? CoverUrl { get; init; }
    public string? CoverSource { get; init; }
    public string? VideoUrl { get; init; }
    public string? LocalVideoPath { get; set; }
    public string? LocalVideoFileUri { get; set; }
    public bool LocalVideoRegisteredToHttpServer { get; set; }
    public string? MusicUrl { get; init; }
    public string? MusicTitle { get; init; }
    public string? MusicAuthor { get; init; }
    public long LikeCount { get; init; }
    public long CollectCount { get; init; }
    public long CommentCount { get; init; }
    public long ShareCount { get; init; }
    public long PlayCount { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<DouyinVideoQuality> Qualities { get; init; } = [];
    public List<DouyinImageInfo> Images { get; init; } = [];
    public List<DouyinCommentInfo> Comments { get; init; } = [];
    public bool IsGallery => Images.Count > 0;
    public bool IsVideo => !string.IsNullOrWhiteSpace(VideoUrl);

    public static DouyinParseResult IgnoredLive(string sourceUrl) => new()
    {
        AwemeId = "live",
        SourceUrl = sourceUrl,
        Title = "抖音直播",
        IsIgnored = true,
    };
}

public sealed record DouyinVideoQuality
{
    public required string Url { get; init; }
    public string? Uri { get; init; }
    public string Label { get; init; } = "默认";
    public string Ratio { get; init; } = string.Empty;
    public int BitRate { get; init; }
    public int Fps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Codec { get; init; } = string.Empty;
    public string GearName { get; init; } = string.Empty;
    public bool IsByteVc1 { get; init; }
}

public sealed record DouyinImageInfo
{
    public required string Url { get; init; }
    public string? LivePhotoUrl { get; init; }
}

public sealed record DouyinCommentInfo
{
    public required string CommentId { get; init; }
    public required string Text { get; init; }
    public string UserName { get; init; } = "未知用户";
    public string? UserId { get; init; }
    public string? DisplayUserId { get; init; }
    public string? UserAvatarUrl { get; init; }
    public List<string> ImageUrls { get; init; } = [];
    public string? IpLabel { get; init; }
    public long LikeCount { get; init; }
    public long ReplyCount { get; init; }
    public long CreateTimeUnixSeconds { get; init; }
    public bool IsAuthor { get; init; }
}

public sealed class DouyinParseException(string message) : Exception(message);
