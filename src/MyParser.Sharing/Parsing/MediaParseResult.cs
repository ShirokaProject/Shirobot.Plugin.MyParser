namespace Shirobot.Plugin.MyParser.Parsing;

public enum ParsedMediaKind
{
    Video,
    Gallery,
    Article,
    Track,
    Live,
    Collection,
    Other,
}

public enum MediaAssetKind
{
    Video,
    Audio,
    Image,
    LiveStream,
}

public enum MediaContentKind
{
    Text,
    Heading,
    Quote,
    Image,
    Video,
}

public sealed record MediaAsset
{
    public required MediaAssetKind Kind { get; init; }
    public required string Url { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string> BackupUrls { get; init; } = [];
    public string? Referer { get; init; }
    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; } = new Dictionary<string, string>();
    public string? CacheKey { get; init; }
    public string? FileNamePrefix { get; init; }
    public string DownloadDirectory { get; init; } = string.Empty;
    public string FileExtension { get; init; } = "mp4";
    public string Protocol { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public int CdnIndex { get; init; }
    public int QualityId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public string Codec { get; init; } = string.Empty;
}

public sealed record MediaContentBlock
{
    public required MediaContentKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public int Level { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

public sealed record ParsedMedia
{
    public required string ProviderId { get; init; }
    public required string ProviderName { get; init; }
    public required string MediaId { get; init; }
    public ParsedMediaKind Kind { get; init; } = ParsedMediaKind.Other;
    public string? SourceUrl { get; init; }
    public string? Title { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorId { get; init; }
    public string? CoverUrl { get; init; }
    public string? MusicUrl { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<MediaAsset> Assets { get; init; } = [];
    public IReadOnlyList<MediaContentBlock> Content { get; init; } = [];
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}
