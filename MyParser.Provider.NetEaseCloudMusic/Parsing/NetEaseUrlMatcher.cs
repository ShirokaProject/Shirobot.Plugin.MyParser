using System.Text.RegularExpressions;

namespace MyParser.Provider.NetEaseCloudMusic.Parsing;

internal static partial class NetEaseUrlMatcher
{
    public static bool ContainsNetEaseSongUrl(string text)
    {
        return ExtractSongIdFromUrl(text) is not null || ExtractShortUrl(text) is not null;
    }

    public static long? ExtractSongIdFromUrl(string text)
    {
        foreach (Match match in SongUrlRegex().Matches(text))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || !IsSongRoute(uri)) continue;
            var id = GetSongQueryValue(uri);
            if (long.TryParse(id, out var songId) && songId > 0) return songId;
        }

        return null;
    }

    public static string? ExtractSongUrl(string text)
    {
        var id = ExtractSongIdFromUrl(text);
        if (id is not null)
        {
            return NetEaseUrlParser.BuildSongUrl(id.Value);
        }

        foreach (Match match in SongUrlRegex().Matches(text))
        {
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) && IsSongRoute(uri))
                return match.Value;
        }

        return null;
    }

    public static string? ExtractShortUrl(string text)
    {
        foreach (Match match in ShortUrlRegex().Matches(text))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, "163cn.tv", StringComparison.OrdinalIgnoreCase)) continue;
            var token = uri.AbsolutePath.Trim('/');
            if (ShortCodeRegex().IsMatch(token)) return uri.ToString();
        }

        return null;
    }

    private static bool IsSongRoute(Uri uri)
    {
        if (uri.Host is not ("music.163.com" or "y.music.163.com")) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return path is "/song" or "/m/song"
               || uri.Fragment.StartsWith("#/song", StringComparison.OrdinalIgnoreCase)
               || uri.Fragment.StartsWith("#/m/song", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSongQueryValue(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) && uri.Fragment.Contains('?', StringComparison.Ordinal))
            query = uri.Fragment[(uri.Fragment.IndexOf('?') + 1)..];

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), "id", StringComparison.OrdinalIgnoreCase))
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : null;
        }

        return null;
    }

    [GeneratedRegex(@"https?://(?:y\.)?music\.163\.com/(?:m/|#/)?song\?(?:[^\s\]\)）>&]*&)*id=(\d+)(?:&[^\s\]\)）>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex SongUrlRegex();

    [GeneratedRegex(@"https?://163cn\.tv/[0-9A-Za-z]+", RegexOptions.IgnoreCase)]
    private static partial Regex ShortUrlRegex();

    [GeneratedRegex(@"^[0-9A-Za-z]+$")]
    private static partial Regex ShortCodeRegex();
}
