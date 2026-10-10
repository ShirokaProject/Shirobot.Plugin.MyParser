using System.Text.RegularExpressions;

namespace MyParser.Provider.X.Parsing;

public static partial class XUrlExtractor
{
    private const string HostPattern = "(?:[a-z0-9-]+\\.)*(?:x|twitter|fxtwitter|vxtwitter|fixupx)\\.com";

    [GeneratedRegex(@"(?<![A-Za-z0-9.-])(?:(?:https?://)?" + HostPattern + @"/[^\s<>""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"status/(\d{1,25})(?:/[^/\s<>""']*)?", RegexOptions.IgnoreCase)]
    private static partial Regex StatusRegex();

    public static bool IsSupportedHost(string host)
    {
        var value = (host ?? string.Empty).ToLowerInvariant();
        return value is "x.com" or "twitter.com" or "fxtwitter.com" or "vxtwitter.com" or "fixupx.com"
               || value.EndsWith(".x.com", StringComparison.Ordinal)
               || value.EndsWith(".twitter.com", StringComparison.Ordinal)
               || value.EndsWith(".fxtwitter.com", StringComparison.Ordinal)
               || value.EndsWith(".vxtwitter.com", StringComparison.Ordinal)
               || value.EndsWith(".fixupx.com", StringComparison.Ordinal);
    }

    public static bool TryExtractStatusId(string text, out string statusId)
    {
        statusId = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in UrlRegex().Matches(text))
        {
            var candidate = match.Value.Trim().TrimEnd('。', '，', ',', '.', ')', '）', ']', '】', '!', '！', '?', '？');
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsSupportedHost(uri.Host))
            {
                continue;
            }

            var matchStatus = StatusRegex().Match(uri.AbsolutePath);
            if (!matchStatus.Success)
            {
                continue;
            }

            statusId = matchStatus.Groups[1].Value;
            return true;
        }

        return false;
    }
}
