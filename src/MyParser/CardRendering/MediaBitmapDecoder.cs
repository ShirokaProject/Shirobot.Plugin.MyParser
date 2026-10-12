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
        return Decode(stream);
    }

    public static Bitmap? DecodeImageFileForRender(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Decode(stream);
    }
    // Cards are rendered at 192 DPI. Cap decoded covers at 1600 physical pixels,
    // retaining aspect ratio and avoiding a full-resolution camera bitmap.
    private static Bitmap Decode(Stream stream)
    {
        using var codec = SkiaSharp.SKCodec.Create(stream)
            ?? throw new InvalidDataException("无法解码卡片图片。");
        var info = codec.Info;
        var scale = Math.Min(1f, 1600f / Math.Max(info.Width, info.Height));
        var size = codec.GetScaledDimensions(scale);
        using var decoded = new SkiaSharp.SKBitmap(size.Width, size.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result != SkiaSharp.SKCodecResult.Success)
            throw new InvalidDataException($"卡片图片解码失败: {result}");
        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
        using var resized = size.Width == width && size.Height == height ? null : decoded.Resize(new SkiaSharp.SKImageInfo(width, height),
            new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.CatmullRom));
        using var image = SkiaSharp.SKImage.FromBitmap(resized ?? decoded);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var output = png.AsStream();
        return new Bitmap(output);
    }
}
