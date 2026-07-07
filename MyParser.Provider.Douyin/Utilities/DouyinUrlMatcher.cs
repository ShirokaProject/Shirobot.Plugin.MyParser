using System.Text.RegularExpressions;

namespace MyParser.Provider.Douyin.Utilities;

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

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsDouyinHost(uri.Host))
            {
                return uri.ToString();
            }
        }

        return null;
    }

    private static bool IsDouyinHost(string host) => host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("(?:(?:https?://)?(?:v\\.)?douyin\\.com/[^\\s<>\"']+|(?:https?://)?(?:www\\.)?iesdouyin\\.com/[^\\s<>\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
