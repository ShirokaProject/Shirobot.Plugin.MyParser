using MyParser.Provider.WeixinChannels.Infrastructure;

namespace MyParser.Provider.WeixinChannels.Parsing;

internal static partial class WeixinChannelsUrlParser
{
    public static bool ContainsWeixinChannelsUrl(string text)
    {
        return WeixinChannelsUrlMatcher.ContainsWeixinChannelsUrl(text);
    }

    public static bool TryExtractShareUrl(string text, out string shareUrl)
    {
        return WeixinChannelsUrlMatcher.TryExtractShareUrl(text, out shareUrl);
    }

    public static string ExtractSphId(string shareUrl)
    {
        if (!Uri.TryCreate(shareUrl, UriKind.Absolute, out var uri))
        {
            return SanitizeFileName(shareUrl, 32);
        }

        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(segment) ? SanitizeFileName(shareUrl, 32) : segment;
    }

    private static string SanitizeFileName(string value, int maxLength)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return value.Length > maxLength ? value[..maxLength] : value;
    }

}
