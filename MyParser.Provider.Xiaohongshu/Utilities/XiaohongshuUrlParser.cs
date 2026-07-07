using System.Text.RegularExpressions;

namespace MyParser.Provider.Xiaohongshu.Utilities;

internal static partial class XiaohongshuUrlParser
{
    public static bool ContainsXiaohongshuUrl(string text)
    {
        return XiaohongshuUrlMatcher.ContainsXiaohongshuUrl(text);
    }

    public static string? ExtractXiaohongshuUrl(string text)
    {
        return XiaohongshuUrlMatcher.ExtractXiaohongshuUrl(text);
    }

    public static string? ExtractNoteId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = NoteRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = ItemRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string ExtractXsecToken(string url)
    {
        return GetQueryValue(url, "xsec_token") ?? GetQueryValue(url, "xsecToken") ?? string.Empty;
    }

    public static string ExtractXsecSource(string url)
    {
        return GetQueryValue(url, "xsec_source") ?? GetQueryValue(url, "xsecSource") ?? "pc_feed";
    }

    public static string ExtractUserIdFromUrl(string url)
    {
        var match = ExploreWithUserRegex().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = UserProfileRegex().Match(url);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    public static bool IsShortUrl(string url)
    {
        return url.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("xhs.cn", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetQueryValue(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && string.Equals(Uri.UnescapeDataString(pieces[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }

        return null;
    }

    [GeneratedRegex("xiaohongshu\\.com/(?:explore|discovery/item)/(?:[0-9a-fA-F]+/)?([0-9a-fA-F]{12,40})", RegexOptions.IgnoreCase)]
    private static partial Regex NoteRegex();

    [GeneratedRegex("/item/([0-9a-fA-F]{12,40})", RegexOptions.IgnoreCase)]
    private static partial Regex ItemRegex();

    [GeneratedRegex("xiaohongshu\\.com/explore/([0-9a-fA-F]{20,32})/[0-9a-fA-F]{12,40}", RegexOptions.IgnoreCase)]
    private static partial Regex ExploreWithUserRegex();

    [GeneratedRegex("xiaohongshu\\.com/user/profile/([0-9a-fA-F]{20,32})", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfileRegex();
}
