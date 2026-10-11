namespace MyParser.Provider.NetEaseCloudMusic.Models;

public sealed record NetEaseSearchSong(
    long Id,
    string Name,
    string Artists,
    string Album,
    string? CoverUrl);

public sealed record NetEaseParseResult
{
    public required long SongId { get; init; }
    public required string Title { get; init; }
    public required string Artists { get; init; }
    public required string Album { get; init; }
    public string? CoverUrl { get; init; }
    public string? SourceUrl { get; init; }
    public required string AudioUrl { get; init; }
    public string Quality { get; init; } = "standard";
    public string? FileType { get; init; }
    public long? FileSize { get; init; }
    public int? Bitrate { get; init; }
    public string? Lyric { get; init; }
    public string? TranslatedLyric { get; init; }
}
