using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>Creates a center-cropped avatar at the card renderer's physical pixel size.</summary>
public static class AvatarBitmapResizer
{
    // Keep in sync with ControlRenderOptions' default 192 DPI.
    public const double DefaultDpi = 192;
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.CatmullRom);

    public static Bitmap? DecodeFile(string path, double displaySize, double dpi = DefaultDpi)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Decode(stream, displaySize, dpi);
    }

    public static Bitmap? DecodeBase64(string uri, double displaySize, double dpi = DefaultDpi)
    {
        if (!uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase)) return null;
        using var stream = new MemoryStream(Convert.FromBase64String(uri[9..]));
        return Decode(stream, displaySize, dpi);
    }

    public static Bitmap Decode(Stream stream, double displaySize, double dpi = DefaultDpi)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!double.IsFinite(displaySize) || displaySize <= 0) throw new ArgumentOutOfRangeException(nameof(displaySize));
        if (!double.IsFinite(dpi) || dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        var pixels = checked((int)Math.Ceiling(displaySize * dpi / 96));
        using var decoded = SKBitmap.Decode(stream) ?? throw new InvalidDataException("无法解码头像图片。");
        var current = SKImage.FromBitmap(decoded);
        try
        {
            var side = Math.Min(current.Width, current.Height);
            var source = SKRect.Create((current.Width - side) / 2f, (current.Height - side) / 2f, side, side);
            // Halve large images progressively so fine details do not alias during a large reduction.
            while (true)
            {
                var nextSize = side > pixels * 2L ? Math.Max(pixels, side / 2) : pixels;
                using var surface = SKSurface.Create(new SKImageInfo(nextSize, nextSize, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException("无法创建头像缩放画布。");
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawImage(current, source, SKRect.Create(nextSize, nextSize), Sampling);
                var next = surface.Snapshot();
                current.Dispose();
                current = next;
                if (nextSize == pixels) break;
                side = nextSize;
                source = SKRect.Create(side, side);
            }
            using var png = current.Encode(SKEncodedImageFormat.Png, 100);
            using var output = png.AsStream();
            return new Bitmap(output);
        }
        finally
        {
            current.Dispose();
        }
    }
}
