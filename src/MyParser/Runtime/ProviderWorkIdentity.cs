using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser;

internal static class ProviderWorkIdentity
{
    public static string Normalize(string providerId, string parseText)
    {
        var value = parseText.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return TextPreviewFormatter.TrimLine(value.ReplaceLineEndings(" "), 160);

        var builder = new UriBuilder(uri)
        {
            Host = uri.Host.ToLowerInvariant(),
            Query = string.Equals(providerId, "neteasecloudmusic", StringComparison.OrdinalIgnoreCase)
                ? uri.Query.TrimStart('?')
                : string.Empty,
            Fragment = string.Empty,
        };
        builder.Path = builder.Path.TrimEnd('/');
        if (string.Equals(providerId, "bilibili", StringComparison.OrdinalIgnoreCase))
        {
            var page = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part.Length == 2 && part[0].Equals("p", StringComparison.OrdinalIgnoreCase));
            if (page is not null && int.TryParse(page[1], out var number) && number > 0)
                builder.Query = "p=" + number;
        }
        return builder.Uri.AbsoluteUri;
    }
}
