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

        var match = SphUrlRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        shareUrl = NormalizeUrl(match.Value);
        return true;
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

    [GeneratedRegex(@"(?:https?://)?weixin\.qq\.com/sph/[A-Za-z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SphUrlRegex();
}
