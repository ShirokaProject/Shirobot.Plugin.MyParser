using System.Text.RegularExpressions;

namespace MyParser.Provider.Heybox.Parsing;

internal static partial class HeyboxUrlParser
{
    [GeneratedRegex(@"/bbs/link/([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkIdPathRegex();

    [GeneratedRegex("""(?:link_id|linkid)[\"'=:/\\\s]+([A-Za-z0-9]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkIdTextRegex();

    public static bool ContainsHeyboxUrl(string text) => HeyboxUrlMatcher.ContainsHeyboxUrl(text);

    public static string? ExtractHeyboxUrl(string text)
    {
        return HeyboxUrlMatcher.ExtractHeyboxUrl(text);
    }

    public static string? ExtractLinkId(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                var linkId = TryGetQueryValue(uri.Query, "link_id") ?? TryGetQueryValue(uri.Query, "linkid");
                if (!string.IsNullOrWhiteSpace(linkId))
                {
                    return linkId;
                }

                var pathMatch = LinkIdPathRegex().Match(uri.AbsolutePath);
                if (pathMatch.Success)
                {
                    return pathMatch.Groups[1].Value;
                }
            }

            var decoded = Uri.UnescapeDataString(value);
            var textMatch = LinkIdTextRegex().Match(decoded);
            if (textMatch.Success)
            {
                return textMatch.Groups[1].Value;
            }
        }

        return null;
    }

    private static string? TryGetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            var name = index < 0 ? part : part[..index];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index < 0 ? string.Empty : Uri.UnescapeDataString(part[(index + 1)..]);
        }

        return null;
    }
}
