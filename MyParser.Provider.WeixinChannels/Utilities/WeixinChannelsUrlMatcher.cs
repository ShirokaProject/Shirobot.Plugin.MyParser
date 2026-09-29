using System.Text.RegularExpressions;

namespace MyParser.Provider.WeixinChannels.Utilities;

internal static partial class WeixinChannelsUrlMatcher
{
    public static bool ContainsWeixinChannelsUrl(string text) => TryExtractShareUrl(text, out _);

    public static bool TryExtractShareUrl(string text, out string shareUrl)
    {
        shareUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in SphUrlRegex().Matches(text))
        {
            var candidate = NormalizeUrl(match.Value);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, "weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                || !SphPathRegex().IsMatch(uri.AbsolutePath))
            {
                continue;
            }

            shareUrl = uri.ToString();
            return true;
        }

        return false;
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('，', '。', ',', '.', ')', '）', ']', '】', '>', '》');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    [GeneratedRegex(@"(?:https?://)?(?<![A-Za-z0-9.-])weixin\.qq\.com/sph/[A-Za-z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SphUrlRegex();

    [GeneratedRegex(@"^/sph/[A-Za-z0-9_-]+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SphPathRegex();
}
