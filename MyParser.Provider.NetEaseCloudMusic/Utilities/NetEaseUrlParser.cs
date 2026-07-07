using System.Text.RegularExpressions;

namespace MyParser.Provider.NetEaseCloudMusic.Utilities;

internal static partial class NetEaseUrlParser
{
    public static bool ContainsNetEaseSongUrl(string text)
    {
        return TryParseInternalSongUri(text, out _)
               || TryParseInternalPickUri(text, out _, out _)
               || NetEaseUrlMatcher.ContainsNetEaseSongUrl(text);
    }

    public static string? NormalizeParseText(string text)
    {
        text = text.Trim();
        var id = ExtractSongId(text);
        return id is null ? ExtractSongUrl(text) ?? ExtractShortUrl(text) : BuildSongUrl(id.Value);
    }

    public static string BuildSongUrl(long songId) => $"https://music.163.com/song?id={songId}";

    public static string BuildInternalSongUri(long songId) => $"netease://song?id={songId}";

    public static string BuildInternalPickUri(IReadOnlyList<long> songIds, int startIndex)
    {
        return $"netease://pick?index={startIndex}&ids={string.Join(',', songIds.Where(i => i > 0))}";
    }

    public static bool TryParseInternalSongUri(string text, out long songId)
    {
        var match = InternalSongRegex().Match(text.Trim());
        return long.TryParse(match.Success ? match.Groups[1].Value : string.Empty, out songId) && songId > 0;
    }

    public static bool TryParseInternalPickUri(string text, out int startIndex, out IReadOnlyList<long> songIds)
    {
        startIndex = 0;
        songIds = [];
        var match = InternalPickRegex().Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out startIndex))
        {
            return false;
        }

        var ids = match.Groups[2].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(i => long.TryParse(i, out var id) ? id : 0)
            .Where(i => i > 0)
            .ToArray();
        if (ids.Length == 0 || startIndex < 0 || startIndex >= ids.Length)
        {
            return false;
        }

        songIds = ids;
        return true;
    }

    public static long? ExtractSongId(string text)
    {
        text = text.Trim();
        if (TryParseInternalPickUri(text, out var startIndex, out var songIds))
        {
            return songIds[startIndex];
        }

        if (TryParseInternalSongUri(text, out var internalId))
        {
            return internalId;
        }

        var urlId = NetEaseUrlMatcher.ExtractSongIdFromUrl(text);
        if (urlId is not null)
        {
            return urlId.Value;
        }

        var match = SongIdRegex().Match(text);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0)
        {
            return id;
        }

        return long.TryParse(text, out var numericId) && numericId > 0 ? numericId : null;
    }

    public static string? ExtractSongUrl(string text)
    {
        return NetEaseUrlMatcher.ExtractSongUrl(text);
    }

    public static string? ExtractShortUrl(string text)
    {
        return NetEaseUrlMatcher.ExtractShortUrl(text);
    }

    [GeneratedRegex(@"netease://song\?id=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InternalSongRegex();

    [GeneratedRegex(@"netease://pick\?index=(\d+)&ids=([\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InternalPickRegex();

    [GeneratedRegex(@"(?:^|[?&#])id=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SongIdRegex();
}
