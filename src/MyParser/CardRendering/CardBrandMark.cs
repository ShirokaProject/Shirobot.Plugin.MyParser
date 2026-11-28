using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>
/// 卡片右下角署名，所有平台共用：上行「MyParser v版本」，下行「Powered By [ShiroBot 头像] ShiroBot」，右对齐。
/// 文字颜色继承 TextElement.Foreground，字号用 <see cref="TextSize"/> 调（版本行小 1 号，头像比字大 3）。
/// </summary>
public sealed class CardBrandMark : StackPanel
{
    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<CardBrandMark, double>(nameof(TextSize), 8);

    private readonly TextBlock _version;
    private readonly TextBlock _powered;
    private readonly TextBlock _name;
    private readonly Ellipse _icon;

    public CardBrandMark()
    {
        Spacing = 2;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        _version = new TextBlock { Text = VersionText, HorizontalAlignment = HorizontalAlignment.Right };
        _powered = new TextBlock { Text = "Powered By", VerticalAlignment = VerticalAlignment.Center };
        _name = new TextBlock { Text = "ShiroBot", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _icon = new Ellipse { VerticalAlignment = VerticalAlignment.Center };
        if (Icon is { } icon) _icon.Fill = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
        else _icon.IsVisible = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(_powered);
        row.Children.Add(_icon);
        row.Children.Add(_name);
        Children.Add(_version);
        Children.Add(row);
        ApplySize();
    }

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    /// <summary>「MyParser v0.6.21」。</summary>
    public static string VersionText { get; } = "MyParser v" +
        (typeof(CardBrandMark).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
         ?? typeof(CardBrandMark).Assembly.GetName().Version?.ToString(3)
         ?? "unknown");

    /// <summary>ShiroBot 头像（内嵌资源），成功解码后缓存，所有卡片共用。</summary>
    public static Bitmap? Icon => EmbeddedBitmaps.Load(ref _icon_, "MyParser.ShiroBot.png", 16);
    private static Bitmap? _icon_;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextSizeProperty) ApplySize();
    }

    private void ApplySize()
    {
        var size = TextSize;
        _version.FontSize = size - 1;
        _powered.FontSize = size;
        _name.FontSize = size;
        // 英文没有下伸部的字形偏上，头像按字形视觉中心上移约 1/8 字号。
        _icon.Width = _icon.Height = size + 3;
        var lift = Math.Round(size / 8, 1);
        _icon.Margin = new Thickness(0, -lift, 0, lift);
    }
}

/// <summary>内嵌图片解码：成功后缓存共用（只读，不随卡片释放）；渲染平台未就绪等原因失败时返回 null，下次再试。</summary>
public static class EmbeddedBitmaps
{
    public static Bitmap? Load(ref Bitmap? cache, string name, double displaySize)
    {
        if (Volatile.Read(ref cache) is { } cached) return cached;
        try
        {
            using var stream = typeof(EmbeddedBitmaps).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            var bitmap = AvatarBitmapResizer.Decode(stream, displaySize);
            var existing = Interlocked.CompareExchange(ref cache, bitmap, null);
            if (existing is null) return bitmap;
            bitmap.Dispose();
            return existing;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }
}
