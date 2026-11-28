using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>
/// 按头像框（B 站装扮挂件）实际不透明的范围排版：头像连同头像框整体限制在一个固定大小的方框里。
/// 头像框的洞口和头像的比例固定（头像框是头像的 1.75 倍），装饰伸得越远，头像和头像框就一起缩得越小，
/// 让所有头像框的可见范围都不超过这个方框；装饰贴着圆圈的头像框不会放大，头像最大就是原尺寸。
/// 方框大小固定，所以名字位置和作者行高度不随头像框变化。
/// </summary>
public sealed record AvatarPendantLayout(
    double AvatarSize,
    double PendantSize,
    Thickness PendantMargin,
    Thickness ColumnMargin,
    Thickness RowMargin,
    Thickness TextMargin)
{
    /// <summary>B 站官方比例：头像框边长是头像的 1.75 倍。</summary>
    public const double PendantRatio = 1.75;

    /// <param name="pendant">头像框图片，null 时按没有头像框排版。</param>
    /// <param name="unitSize">头像连同头像框整体的可见范围上限（方框边长）。</param>
    /// <param name="maxAvatar">头像最大尺寸，即没有头像框时的头像尺寸。</param>
    /// <param name="ringSize">没有头像框时头像连描边占的尺寸，用来算作者行要多留的高度。</param>
    /// <param name="textGap">方框右边缘到名字的间距。</param>
    /// <param name="plain">没有头像框时的排版。</param>
    public static AvatarPendantLayout Create(Bitmap? pendant, double unitSize, double maxAvatar, double ringSize, double textGap,
        AvatarPendantLayout plain)
    {
        if (pendant is null || Measure(pendant) is not { } reach) return plain;

        // reach 是装饰在各方向离中心的距离，占头像框边长的比例（0 ~ 0.5）；取最远的方向来定整体大小。
        var farthest = Math.Max(Math.Max(reach.Left, reach.Right), Math.Max(reach.Top, reach.Bottom));
        var avatar = Math.Round(Math.Min(maxAvatar, unitSize / (2 * farthest * PendantRatio)), 1);
        var size = avatar * PendantRatio;
        var side = (unitSize - avatar) / 2;
        var extraHeight = Math.Max(0, (unitSize - ringSize) / 2);
        return new AvatarPendantLayout(
            avatar,
            size,
            new Thickness(-(size - avatar) / 2),
            // 头像在方框里居中，名字始终从方框右边缘之后开始。
            new Thickness(side, 0, 0, 0),
            new Thickness(0, plain.RowMargin.Top + extraHeight, 0, plain.RowMargin.Bottom + extraHeight),
            new Thickness(side + textGap, 0, 0, 0));
    }

    private readonly record struct Reach(double Left, double Right, double Top, double Bottom);

    /// <summary>找出不透明像素的包围盒，换算成相对中心的伸出比例；读不到像素时返回 null。</summary>
    private static Reach? Measure(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0 || bitmap.Format is not { } format || format.BitsPerPixel != 32) return null;
        // 32 位格式（Bgra8888 / Rgba8888）的 alpha 都在第 4 个字节。
        if (format != PixelFormat.Bgra8888 && format != PixelFormat.Rgba8888) return null;

        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }

        const byte threshold = 24;
        int minX = size.Width, minY = size.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < size.Height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < size.Width; x++)
            {
                if (pixels[row + x * 4 + 3] <= threshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return null;
        double centerX = size.Width / 2.0, centerY = size.Height / 2.0;
        return new Reach(
            (centerX - minX) / size.Width,
            (maxX + 1 - centerX) / size.Width,
            (centerY - minY) / size.Height,
            (maxY + 1 - centerY) / size.Height);
    }
}
