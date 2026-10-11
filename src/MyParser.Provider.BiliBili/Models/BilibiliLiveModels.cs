namespace MyParser.Provider.BiliBili.Models;

public sealed class BilibiliLiveParseResult
{
    public string RoomId { get; init; } = string.Empty;

    public string RealRoomId { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public int LiveStatus { get; init; }

    public string? Title { get; init; }

    public string? AnchorName { get; init; }

    public string? AnchorAvatarUrl { get; init; }

    public string? CoverUrl { get; init; }

    public long OnlineCount { get; init; }

    public long RoomAudienceCount { get; init; }

    public string? RoomAudienceText { get; init; }

    public long WatchedCount { get; init; }

    public string? WatchedText { get; init; }

    public DateTimeOffset? LiveStartTime { get; init; }

    public TimeSpan? LiveDuration { get; init; }

    public string? LocalClipPath { get; set; }

    public string? LocalClipFileUri { get; set; }

    public bool LocalClipRegisteredToHttpServer { get; set; }

    public IReadOnlyList<BilibiliLiveStream> Streams { get; init; } = [];
}

public sealed class BilibiliLiveStream
{
    public string Protocol { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string Codec { get; init; } = string.Empty;

    public int CurrentQn { get; init; }

    public IReadOnlyList<int> AcceptQn { get; init; } = [];

    public int CdnIndex { get; init; }

    public string Url { get; init; } = string.Empty;
}
