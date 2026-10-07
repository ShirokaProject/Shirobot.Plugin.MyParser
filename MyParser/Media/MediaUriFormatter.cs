namespace Shirobot.Plugin.MyParser.Media;

internal static class MediaUriFormatter
{
    public static string GetUriMode(string uri)
    {
        if (uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase)) return "base64";
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return "file";
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "http";
        return "unknown";
    }

    public static string PreviewUri(string? uri, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
        if (uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase)) return "base64://...";
        return uri.Length <= maxLength ? uri : uri[..maxLength] + "...";
    }
}
