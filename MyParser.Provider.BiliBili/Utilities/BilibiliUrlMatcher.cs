using System.Text.RegularExpressions;

namespace MyParser.Provider.BiliBili.Utilities;

internal static partial class BilibiliUrlMatcher
{
    public static bool ContainsStrictBilibiliUrl(string text) => ExtractStrictBilibiliUrl(text) is not null;

    public static string? ExtractStrictBilibiliUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = StrictBilibiliUrlRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var url = match.Value.TrimEnd('.', '。', ',', '，', ')', '）', ']', '】', '>', '》');
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }

    public static string? ExtractB23Url(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = B23UrlRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var url = match.Value.TrimEnd('.', '。', ',', '，', ')', '）', ']', '】');
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
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

    [GeneratedRegex(@"(?:https?://)?(?:(?:www|m|live|t|space)\.)?bilibili\.com/[^\s<>\""'，。]+|(?:https?://)?b23\.tv/[0-9A-Za-z]+|(?:https?://)?bili2233\.cn/[0-9A-Za-z]+", RegexOptions.IgnoreCase)]
    private static partial Regex StrictBilibiliUrlRegex();

    [GeneratedRegex(@"(?:https?://)?b23\.tv/[0-9A-Za-z]+", RegexOptions.IgnoreCase)]
    private static partial Regex B23UrlRegex();

    [GeneratedRegex("https?://[^\\s\\\"'<>，。)）\\]}]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
}
