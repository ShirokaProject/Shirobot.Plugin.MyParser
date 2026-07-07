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

        var match = HeyboxUrlRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        return match.Value.TrimEnd(')', ']', '}', '。', '，', ',', '.', ';');
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

    [GeneratedRegex("""https?://[^\s\u3000<>\"']*(?:xiaoheihe\.cn|heybox\.cn|maxjia\.com)[^\s\u3000<>\"']*""", RegexOptions.IgnoreCase)]
    private static partial Regex HeyboxUrlRegex();

    [GeneratedRegex("https?://[^\\s\\\"'<>，。)）\\]}]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
}
