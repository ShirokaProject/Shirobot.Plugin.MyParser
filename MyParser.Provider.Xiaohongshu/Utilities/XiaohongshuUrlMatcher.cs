using System.Net;
using System.Text.RegularExpressions;

namespace MyParser.Provider.Xiaohongshu.Utilities;

internal static partial class XiaohongshuUrlMatcher
{
    public static bool ContainsXiaohongshuUrl(string text) => ExtractXiaohongshuUrl(text) is not null;

    public static string? ExtractXiaohongshuUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim().Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        var match = UrlRegex().Match(normalized);
        if (!match.Success)
        {
            return null;
        }

        var url = WebUtility.UrlDecode(match.Value.Trim().Trim('"', '\''));
        return IsXiaohongshuHost(url) ? url : null;
    }

    public static IEnumerable<string> ExtractHttpUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in HttpUrlRegex().Matches(text))
        {
            yield return Uri.UnescapeDataString(match.Value);
        }
    }

    private static bool IsXiaohongshuHost(string url)
    {
        return url.Contains("xiaohongshu.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("xhs.cn", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("https?://[^\\s，。)）>\\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("https?://[^\\s\\\"'<>，。)）\\]}]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
}
