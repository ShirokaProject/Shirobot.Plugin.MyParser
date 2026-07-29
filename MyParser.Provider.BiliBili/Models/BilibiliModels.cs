namespace MyParser.Provider.BiliBili.Models;

public sealed record BilibiliParseResult
{
    public required string Bvid { get; init; }
    public long Aid { get; init; }
    public long Cid { get; init; }
    public int Page { get; init; } = 1;
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? PartTitle { get; init; }
    public string? Description { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public string? CoverUrl { get; init; }
    public long DurationSeconds { get; init; }
    public long ViewCount { get; init; }
    public long LikeCount { get; init; }
    public long CoinCount { get; init; }
    public long FavoriteCount { get; init; }
    public long ShareCount { get; init; }
    public long ReplyCount { get; init; }
    public List<BilibiliCommentInfo> Comments { get; init; } = [];
    public List<BilibiliMediaStream> VideoStreams { get; init; } = [];
    public List<BilibiliMediaStream> AudioStreams { get; init; } = [];
    public BilibiliMediaStream? SelectedVideo => VideoStreams.FirstOrDefault();
    public BilibiliMediaStream? SelectedAudio => AudioStreams.FirstOrDefault();
    public string? LocalVideoPath { get; set; }
    public string? LocalVideoFileUri { get; set; }
    public bool LocalVideoRegisteredToHttpServer { get; set; }
    public bool IsVideo => SelectedVideo is not null && SelectedAudio is not null;
}

public sealed record BilibiliCommentInfo
{
    public required string CommentId { get; init; }
    public required string Message { get; init; }
    public string UserName { get; init; } = "未知用户";
    public string? UserId { get; init; }
    public string? UserAvatarUrl { get; init; }
    public string? ImageUrl { get; init; }
    public string? IpLocation { get; init; }
    public string? TimeDescription { get; init; }
    public long LikeCount { get; init; }
    public long ReplyCount { get; init; }
    public long CreateTimeUnixSeconds { get; init; }
    public bool IsAuthor { get; init; }
}

public sealed record BilibiliMultiPageParseResult
{
    public required string Bvid { get; init; }
    public long Aid { get; init; }
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public string? CoverUrl { get; init; }
    public int RequestedPage { get; init; } = 1;
    public IReadOnlyList<BilibiliVideoPageInfo> Pages { get; init; } = [];
    public int PageCount => Pages.Count;
}

public sealed record BilibiliVideoPageInfo
{
    public int Page { get; init; }
    public long Cid { get; init; }
    public string? PartTitle { get; init; }
    public long DurationSeconds { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string? CoverUrl { get; init; }
}

public sealed record BilibiliMediaStream
{
    public required string StreamId { get; init; }
    public required string Url { get; init; }
    public List<string> BackupUrls { get; init; } = [];
    public int QualityId { get; init; }
    public string QualityName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public int Bandwidth { get; init; }
    public int CodecId { get; init; }
    public string CodecName { get; init; } = string.Empty;
    public string Codecs { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public bool IsAudio { get; init; }
    public IEnumerable<string> UrlCandidates => string.IsNullOrWhiteSpace(Url) ? BackupUrls : new[] { Url }.Concat(BackupUrls);
}

public sealed record BilibiliLoginStatus(bool IsLogin, string? UserName, long Mid, int VipStatus, string Message);

public sealed record BilibiliQrLoginSession(string QrcodeKey, string Url, DateTimeOffset CreatedAt);

public sealed record BilibiliQrPollResult(int Code, string Message, bool IsLogin, string? UserName);

public class BilibiliParseException(string message) : Exception(message);

public sealed class BilibiliLoginRequiredException(string message) : BilibiliParseException(message);
