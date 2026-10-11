using Avalonia.Media.Imaging;

namespace Shirobot.Plugin.MyParser.CardRendering;

internal static class MediaBitmapDecoder
{
    public static Bitmap? DecodeBase64ImageForRender(string uri)
    {
        if (!uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bytes = Convert.FromBase64String(uri[9..]);
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    public static Bitmap? DecodeImageFileForRender(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new Bitmap(path);
    }
}
