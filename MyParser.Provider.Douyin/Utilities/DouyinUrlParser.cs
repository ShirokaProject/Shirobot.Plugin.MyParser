using System.Text.RegularExpressions;

namespace MyParser.Provider.Douyin.Utilities;

internal static partial class DouyinUrlParser
{
    public static bool ContainsDouyinUrl(string text) => ExtractDouyinUrl(text) is not null;

    public static string? ExtractDouyinUrl(string text)
    {
        return DouyinUrlMatcher.ExtractDouyinUrl(text);
    }

    public static string? ExtractAwemeId(string input)
    {
        if (Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https") || !IsDouyinHost(uri.Host)) return null;

            foreach (var pattern in AwemeIdPatterns())
            {
                var match = Regex.Match(uri.AbsolutePath, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success && match.Groups[1].Value.Length >= 15) return match.Groups[1].Value;
            }

            foreach (var key in new[] { "modal_id", "aweme_id", "itemId", "note_id" })
            {
                var value = GetQueryValue(uri.Query, key);
                if (value is not null && Regex.IsMatch(value, @"^\d{15,25}$")) return value;
            }

            return null;
        }

        foreach (var pattern in AwemeIdPatterns())
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success && match.Groups[1].Value.Length >= 15)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static bool IsDouyinHost(string host) =>
        string.Equals(host, "douyin.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "iesdouyin.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : null;
        }

        return null;
    }

    public static Uri MakeAbsolute(Uri location, Uri baseUri) => location.IsAbsoluteUri ? location : new Uri(baseUri, location);

    private static string[] AwemeIdPatterns() =>
    [
        @"/video/(\d{15,25})",
        @"/note/(\d{15,25})",
        @"/aweme/detail/(\d{15,25})",
        @"/share/video/(\d{15,25})",
        @"modal_id=(\d{15,25})",
        "aweme_id[=\\\"':]+(\\d{15,25})",
        "itemId[\\\"':]+(\\d{15,25})",
        "note_id[=\\\"':]+(\\d{15,25})",
    ];
}
