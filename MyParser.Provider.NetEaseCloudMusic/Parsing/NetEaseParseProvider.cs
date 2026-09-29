using MyParser.Provider.NetEaseCloudMusic.Parsing;
using MyParser.Provider.NetEaseCloudMusic.Infrastructure;

namespace MyParser.Provider.NetEaseCloudMusic.Parsing;

public sealed class NetEaseParseProvider(NetEaseParser parser) : IParseProviderWithParser, IProviderPriority, IDisposable
{
    public NetEaseParser Parser { get; } = parser;
    public object ParserObject => Parser;
    public string Id => "neteasecloudmusic";
    public string Name => "网易云音乐";
    public int Priority => 30;

    public bool CanHandle(string text) => NetEaseUrlParser.ContainsNetEaseSongUrl(text);

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken).ConfigureAwait(false);
        return new ParsedMedia
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.SongId.ToString(),
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.Artists,
            CoverUrl = result.CoverUrl,
            MusicUrl = result.AudioUrl,
            Tags = [result.Quality],
            Kind = ParsedMediaKind.Track,
            Assets = [new MediaAsset { Kind = MediaAssetKind.Audio, Url = result.AudioUrl, Label = result.Quality, CacheKey = $"neteasecloudmusic:{result.SongId}:{result.Quality}:{result.FileType}", FileNamePrefix = $"{result.Artists} - {result.Title}_{result.SongId}_{result.Quality}", FileExtension = result.FileType ?? "mp3", DownloadDirectory = MyParserRuntime.DownloadDirectory, RequestHeaders = new Dictionary<string, string> { ["User-Agent"] = NetEaseHttp.UserAgent, ["Referer"] = NetEaseHttp.Referer } }],
            Description = $"{result.Album} · {result.Quality}",
            Attributes = new Dictionary<string, string>
            {
                ["album"] = result.Album, ["quality"] = result.Quality, ["file_type"] = result.FileType ?? "mp3",
                ["file_size"] = result.FileSize?.ToString() ?? string.Empty, ["bitrate"] = result.Bitrate?.ToString() ?? string.Empty,
                ["lyrics"] = result.Lyric ?? string.Empty, ["translated_lyrics"] = result.TranslatedLyric ?? string.Empty,
            },
        };
    }

    public void Dispose() => Parser.Dispose();
}
