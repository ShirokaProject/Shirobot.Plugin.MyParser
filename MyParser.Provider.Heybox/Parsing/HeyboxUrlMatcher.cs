using System.Text.RegularExpressions;

namespace MyParser.Provider.Heybox.Parsing;

internal static partial class HeyboxUrlMatcher
{
    public static bool ContainsHeyboxUrl(string text) => ExtractHeyboxUrl(text) is not null;

    public static string? ExtractHeyboxUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in HttpUrlRegex().Matches(text))
        {
            var candidate = match.Value.TrimEnd(')', ']', '}', '。', '，', ',', '.', ';');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsSupportedHeyboxUri(uri))
                continue;
            return uri.ToString();
        }

        return null;
    }

    public static bool IsSupportedHeyboxUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) return false;
        var host = uri.Host;
        if (!IsHostOrSubdomain(host, "xiaoheihe.cn")
            && !IsHostOrSubdomain(host, "heybox.cn")
            && !IsHostOrSubdomain(host, "maxjia.com")) return false;

        if (LinkIdPathRegex().IsMatch(uri.AbsolutePath)) return true;
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(pair => pair.Length == 2
                         && (string.Equals(Uri.UnescapeDataString(pair[0]), "link_id", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Uri.UnescapeDataString(pair[0]), "linkid", StringComparison.OrdinalIgnoreCase))
                         && LinkIdValueRegex().IsMatch(Uri.UnescapeDataString(pair[1])));
    }

    private static bool IsHostOrSubdomain(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex("https?://[^\\s\\\"'<>，。)）\\]}]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"/bbs/link/([A-Za-z0-9]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkIdPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]+$")]
    private static partial Regex LinkIdValueRegex();
}
