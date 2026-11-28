using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>
/// 聊天记录的长图兜底：平台既不支持合并转发、也不支持一条消息带多张图时，把全部节点（文字 + 图片）拼成一张长图。
/// 宽 640，正文宽 560；图片撑满正文宽度，过高的竖图原图居中、两侧模糊铺底。视频节点不画，只在副标题里计数。
/// </summary>
public sealed class ForwardLongImageViewModel
{
    public const double ContentWidth = 560;

    public MediaCardPlatform? Platform { get; init; }
    public IBrush AccentGradient => Platform?.CreateGradient(1, 1) ?? Neutral(1);
    public IBrush AccentSoftGradient => Platform?.CreateGradient(0.13, 0.13) ?? Neutral(0.1);

    public string Title { get; init; } = "聊天记录";
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    public string Subtitle { get; init; } = string.Empty;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public IReadOnlyList<ForwardLongImageItem> Items { get; init; } = [];

    private static IBrush Neutral(double opacity) => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb((byte)(opacity * 255), 0x8A, 0x8F, 0x9C), 0), new GradientStop(Color.FromArgb((byte)(opacity * 255), 0x5C, 0x61, 0x6E), 1) },
    };
}

/// <summary>长图里的一项：一段文字或一张图片。</summary>
public sealed class ForwardLongImageItem
{
    private const double MaxImageHeight = ForwardLongImageViewModel.ContentWidth * 1.5;

    public string Text { get; init; } = string.Empty;
    public Bitmap? Image { get; init; }

    public bool IsText => Image is null && !string.IsNullOrWhiteSpace(Text);
    public bool IsImage => Image is not null;
    public double ImageWidth => ForwardLongImageViewModel.ContentWidth;
    public double ImageHeight => Math.Min(NaturalHeight, MaxImageHeight);
    public bool HasBackdrop => IsImage && NaturalHeight > MaxImageHeight + 1;

    private double NaturalHeight => Image is { PixelSize: { Width: > 0, Height: > 0 } size }
        ? Math.Round(ForwardLongImageViewModel.ContentWidth * size.Height / size.Width)
        : 0;
}
