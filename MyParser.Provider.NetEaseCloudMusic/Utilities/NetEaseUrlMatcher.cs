using System.Text.RegularExpressions;

namespace MyParser.Provider.NetEaseCloudMusic.Utilities;

internal static partial class NetEaseUrlMatcher
{
    public static bool ContainsNetEaseSongUrl(string text)
    {
        return SongUrlRegex().IsMatch(text) || ShortUrlRegex().IsMatch(text);
    }

    public static long? ExtractSongIdFromUrl(string text)
    {
        var match = SongUrlRegex().Match(text);
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }

    public static string? ExtractSongUrl(string text)
    {
        var id = ExtractSongIdFromUrl(text);
        if (id is not null)
        {
            return NetEaseUrlParser.BuildSongUrl(id.Value);
        }

        var match = SongUrlRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    public static string? ExtractShortUrl(string text)
    {
        var match = ShortUrlRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"https?://(?:y\.)?music\.163\.com/(?:m/|#/)?song\?(?:[^\s\]\)）>&]*&)*id=(\d+)(?:&[^\s\]\)）>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex SongUrlRegex();

    [GeneratedRegex(@"https?://163cn\.tv/[0-9A-Za-z]+", RegexOptions.IgnoreCase)]
    private static partial Regex ShortUrlRegex();
}
