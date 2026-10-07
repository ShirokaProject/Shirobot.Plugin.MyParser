using System.Text.RegularExpressions;

namespace MyParser.Provider.Douyin.Parsing;

internal static partial class DouyinUrlMatcher
{
    public static bool ContainsDouyinUrl(string text) => ExtractDouyinUrl(text) is not null;

    public static string? ExtractDouyinUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in UrlRegex().Matches(text))
        {
            var url = match.Value.Trim().TrimEnd('，', '。', '、', ',', '.', ';', '；', ')', '）', ']', '】', '>', '》', '"', '\'');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsSupportedDouyinUrl(uri))
            {
                return uri.ToString();
            }
        }

        return null;
    }

    private static bool IsSupportedDouyinUrl(Uri uri)
    {
        var host = uri.Host;
        var isDouyinHost = IsHostOrSubdomain(host, "douyin.com")
                           || IsHostOrSubdomain(host, "iesdouyin.com")
                           || string.Equals(host, "webcast.amemv.com", StringComparison.OrdinalIgnoreCase);
        if (!isDouyinHost) return false;

        var path = uri.AbsolutePath;
        if (string.Equals(host, "v.douyin.com", StringComparison.OrdinalIgnoreCase))
            return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 1;

        if (string.Equals(host, "live.douyin.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "webcast.amemv.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return VideoPathRegex().IsMatch(path)
               || NotePathRegex().IsMatch(path)
               || AwemeDetailPathRegex().IsMatch(path)
               || ShareWorkPathRegex().IsMatch(path)
               || IsShareUrlWithWorkId(uri);
    }

    private static bool IsShareUrlWithWorkId(Uri uri)
    {
        if (!uri.AbsolutePath.StartsWith("/share", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && (string.Equals(Uri.UnescapeDataString(pair[0]), "modal_id", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Uri.UnescapeDataString(pair[0]), "aweme_id", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Uri.UnescapeDataString(pair[0]), "itemId", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Uri.UnescapeDataString(pair[0]), "note_id", StringComparison.OrdinalIgnoreCase))
                && WorkIdRegex().IsMatch(Uri.UnescapeDataString(pair[1])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHostOrSubdomain(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("(?:(?:https?://)?(?<![A-Za-z0-9.-])(?:[A-Za-z0-9-]+\\.)*(?:douyin\\.com|iesdouyin\\.com|webcast\\.amemv\\.com)/[^\\s<>\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^/video/(\d{15,25})(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPathRegex();

    [GeneratedRegex(@"^/note/(\d{15,25})(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NotePathRegex();

    [GeneratedRegex(@"^/aweme/detail/(\d{15,25})(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AwemeDetailPathRegex();

    [GeneratedRegex(@"^/share/(?:video|note)/(\d{15,25})(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ShareWorkPathRegex();

    [GeneratedRegex(@"^\d{15,25}$")]
    private static partial Regex WorkIdRegex();
}
