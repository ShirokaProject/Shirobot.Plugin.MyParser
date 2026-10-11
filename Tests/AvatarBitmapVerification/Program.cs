using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Shirobot.Plugin.MyParser.CardRendering;

Avalonia.Skia.SkiaPlatform.Initialize();
using var input = new SKBitmap(400, 200);
using (var canvas = new SKCanvas(input))
{
    canvas.Clear(SKColors.Red);
    using var paint = new SKPaint { Color = new SKColor(0, 0, 255, 128), BlendMode = SKBlendMode.Src };
    canvas.DrawRect(100, 0, 200, 200, paint);
    paint.Color = SKColors.Green;
    canvas.DrawRect(300, 0, 100, 200, paint);
}
using var image = SKImage.FromBitmap(input);
using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
var uri = "base64://" + Convert.ToBase64String(encoded.ToArray());
foreach (var size in new[] { 32, 40, 44, 46, 52 })
{
    using var avatar = AvatarBitmapResizer.DecodeBase64(uri, size)!;
    Check(avatar.PixelSize == new PixelSize(size * 2, size * 2), "Physical avatar size does not match 192 DPI.");
    using var pixels = ReadPixels(avatar);
    foreach (var x in new[] { 2, pixels.Width / 2, pixels.Width - 3 })
    {
        var color = pixels.GetPixel(x, pixels.Height / 2);
        Check(color.Blue > 245 && color.Red < 10 && color.Green < 10, "Avatar was stretched instead of center cropped.");
        Check(Math.Abs(color.Alpha - 128) <= 2, "Transparent avatar lost alpha during resampling.");
    }
}
using (var avatar = AvatarBitmapResizer.DecodeBase64(uri, 52, dpi: 288)!)
    Check(avatar.PixelSize == new PixelSize(156, 156), "Custom DPI was ignored.");
using (var checker = new SKBitmap(1024, 1024))
{
    for (var y = 0; y < checker.Height; y++)
        for (var x = 0; x < checker.Width; x++)
            checker.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.White : SKColors.Black);
    using var checkerImage = SKImage.FromBitmap(checker);
    using var data = checkerImage.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = data.AsStream();
    using var avatar = AvatarBitmapResizer.Decode(stream, 32);
    using var result = ReadPixels(avatar);
    var color = result.GetPixel(32, 32);
    Check(color.Red is >= 115 and <= 140, "Large reduction aliases fine alternating details.");
}
var tempPath = Path.Combine(Path.GetTempPath(), $"myparser-avatar-verification-{Guid.NewGuid():N}.png");
try
{
    File.WriteAllBytes(tempPath, encoded.ToArray());
    using var fromFile = AvatarBitmapResizer.DecodeFile(tempPath, 52)!;
    Check(fromFile.PixelSize == new PixelSize(104, 104), "File decoder differs from base64 decoder.");
}
finally { File.Delete(tempPath); }
Check(AvatarBitmapResizer.DecodeFile(tempPath, 52) is null, "Missing image should remain absent.");
Check(AvatarBitmapResizer.DecodeBase64("https://example.com/avatar", 52) is null, "Unexpected URI was decoded.");
try
{
    using var invalid = new MemoryStream([1, 2, 3]);
    using var avatar = AvatarBitmapResizer.Decode(invalid, 52);
    throw new Exception("Invalid encoded image was accepted.");
}
catch (InvalidDataException) { }
Console.WriteLine("PASS: all avatar sizes, DPI, center crop, alpha, large reduction, file/base64 and invalid images.");

static SKBitmap ReadPixels(Bitmap bitmap)
{
    using var stream = new MemoryStream();
    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    stream.Position = 0;
    return SKBitmap.Decode(stream);
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
