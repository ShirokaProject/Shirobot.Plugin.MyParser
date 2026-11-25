using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>
/// B 站 / 抖音共用的视频卡片数据。版式固定，平台差异体现在徽标与双色渐变上；
/// 卡片底色取自封面模糊，保证整体色调与封面协调。
/// </summary>
public sealed class MediaCardViewModel
{
    public const double CardWidth = 320;
    private const double ContentOverlap = 56;

    // ---- 平台 ----
    public MediaCardPlatform Platform { get; init; } = MediaCardPlatform.Bilibili;
    public string PlatformName => Platform.Name;
    public bool IsBilibili => Platform.Kind == MediaCardPlatformKind.Bilibili;
    public bool IsDouyin => Platform.Kind == MediaCardPlatformKind.Douyin;
    public IBrush PlatformBadgeBackground => new SolidColorBrush(Platform.BadgeBackground);
    public IBrush PlatformBadgeForeground => new SolidColorBrush(Platform.BadgeForeground);
    public IBrush PrimaryBrush => new SolidColorBrush(Platform.Primary);
    public IBrush SecondaryBrush => new SolidColorBrush(Platform.Secondary);
    /// <summary>平台主渐变（图标、头像描边、信息条胶囊）。</summary>
    public IBrush AccentGradient => Gradient(1, 1);
    /// <summary>浅色平台渐变（标签、信息条底）。</summary>
    public IBrush AccentSoftGradient => Gradient(0.13, 0.13);
    /// <summary>叠加在模糊封面上的平台色调。</summary>
    public IBrush AccentTintGradient => Gradient(0.22, 0.16);

    // ---- 封面 ----
    public Bitmap? Cover { get; init; } = LoadDesignBitmap("background.png");
    public bool HasCover => Cover is not null;
    public bool HasNoCover => Cover is null;

    /// <summary>封面区高度：横版裁成约 4:3 放大展示，竖版最高约 1:1。</summary>
    public double CoverHeight
    {
        get
        {
            if (Cover is not { } cover || cover.PixelSize.Width <= 0) return 150;
            var natural = CardWidth * cover.PixelSize.Height / cover.PixelSize.Width;
            return Math.Round(Math.Clamp(natural, 248, 330));
        }
    }

    public Thickness SheetMargin => new(0, Math.Max(52, CoverHeight - ContentOverlap), 0, 0);

    /// <summary>右上角角标，例如 "5:39" 或 "12 张"。</summary>
    public string BadgeText { get; init; } = "5:39";
    public string BadgeIcon { get; init; } = MediaCardIcons.Play;
    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);

    // ---- 正文 ----
    public string Title { get; init; } = "这是视频标题这是视频标题这是视频标题";
    public string Meta { get; init; } = "2026-11-24 发布 · 日常 · BV1xx411c7xx";
    public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);
    public string Description { get; init; } = "这是视频简介这是视频简介这是视频简介这是视频简介这是视频简介。";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    /// <summary>0 表示不限制行数。</summary>
    public int DescriptionMaxLines { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = ["#标签一", "#标签二", "#标签三"];
    public bool HasTags => Tags.Count > 0;

    // ---- 数据 ----
    public IReadOnlyList<MediaCardMetric> Metrics { get; init; } =
    [
        new(MediaCardIcons.Play, "播放", "4.3万"),
        new(MediaCardIcons.ThumbUp, "点赞", "1,506"),
        new(MediaCardIcons.Coin, "投币", "243"),
        new(MediaCardIcons.Star, "收藏", "436"),
    ];
    public bool HasMetrics => Metrics.Count > 0;

    // ---- 附加条（多 P / 背景音乐等） ----
    public string ExtraIcon { get; init; } = MediaCardIcons.Playlist;
    public string ExtraText { get; init; } = string.Empty;
    public string ExtraHint { get; init; } = string.Empty;
    public bool HasExtra => !string.IsNullOrWhiteSpace(ExtraText);
    public bool HasExtraHint => !string.IsNullOrWhiteSpace(ExtraHint);

    // ---- 作者 ----
    public Bitmap? Avatar { get; init; } = LoadDesignBitmap("avatar.jpg");
    public bool HasAvatar => Avatar is not null;
    public string AuthorName { get; init; } = "作者名字";
    public string AuthorMeta { get; init; } = "IP属地：广东";
    public bool HasAuthorMeta => !string.IsNullOrWhiteSpace(AuthorMeta);
    public string BrandText { get; } = $"By ShirokaProject && MyParser v{PluginVersion}";

    private static string PluginVersion { get; } =
        typeof(MediaCardViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? typeof(MediaCardViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    private LinearGradientBrush Gradient(double primaryOpacity, double secondaryOpacity) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(WithOpacity(Platform.Primary, primaryOpacity), 0),
            new GradientStop(WithOpacity(Platform.Secondary, secondaryOpacity), 1),
        },
    };

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);

    public static string FormatCount(string? value)
    {
        if (!long.TryParse(value, out var count) || count < 0) return string.IsNullOrWhiteSpace(value) ? "--" : value;
        return count switch
        {
            >= 100_000_000 => (count / 100_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿",
            >= 10_000 => (count / 10_000d).ToString("0.#", CultureInfo.InvariantCulture) + "万",
            _ => count.ToString("N0", CultureInfo.InvariantCulture),
        };
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.ToString(duration.TotalHours >= 1 ? "h\\:mm\\:ss" : "m\\:ss", CultureInfo.InvariantCulture);

    private static Bitmap? LoadDesignBitmap(string fileName, [CallerFilePath] string sourceFilePath = "")
    {
        if (!Design.IsDesignMode) return null;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? "."); directory is not null; directory = directory.Parent)
        {
            foreach (var path in new[]
                     {
                         Path.Combine(directory.FullName, "Assets", "Demo", fileName),
                         Path.Combine(directory.FullName, "MyParser", "Assets", "Demo", fileName),
                     })
            {
                if (File.Exists(path))
                    return fileName == "avatar.jpg" ? AvatarBitmapResizer.DecodeFile(path, 34) : new Bitmap(path);
            }
        }

        return null;
    }
}

public sealed record MediaCardMetric(string Icon, string Label, string Value);

public enum MediaCardPlatformKind
{
    Bilibili,
    Douyin,
}

/// <summary>平台视觉：名称、双色渐变与徽标配色。</summary>
public sealed record MediaCardPlatform(
    MediaCardPlatformKind Kind,
    string Name,
    Color Primary,
    Color Secondary,
    Color BadgeBackground,
    Color BadgeForeground)
{
    /// <summary>B 站：樱花粉 → 天蓝，白底徽标。</summary>
    public static readonly MediaCardPlatform Bilibili = new(MediaCardPlatformKind.Bilibili, "哔哩哔哩",
        Color.Parse("#FB7299"), Color.Parse("#00AEEC"), Color.Parse("#F2FFFFFF"), Color.Parse("#FB7299"));

    /// <summary>抖音：青 → 品红，墨黑徽标。</summary>
    public static readonly MediaCardPlatform Douyin = new(MediaCardPlatformKind.Douyin, "抖音",
        Color.Parse("#25F4EE"), Color.Parse("#FE2C55"), Color.Parse("#E6161823"), Color.Parse("#FFFFFF"));
}

/// <summary>把图标 path 字符串转换为 Geometry；在渲染线程上解析，避免模型构造依赖 Avalonia 平台。</summary>
public sealed class MediaCardGeometryConverter : IValueConverter
{
    public static readonly MediaCardGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string data && !string.IsNullOrWhiteSpace(data) ? StreamGeometry.Parse(data) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>卡片使用的通用图标（圆角线性风格，24x24 坐标系，填充绘制）。</summary>
public static class MediaCardIcons
{
    public const string Play = "M8 5.14v13.72a1 1 0 0 0 1.5.86l10.6-6.86a1 1 0 0 0 0-1.72L9.5 4.28A1 1 0 0 0 8 5.14z";
    public const string ThumbUp = "M2 10.5A1.5 1.5 0 0 1 3.5 9h1A1.5 1.5 0 0 1 6 10.5v9A1.5 1.5 0 0 1 4.5 21h-1A1.5 1.5 0 0 1 2 19.5v-9zM8 10.3c0-.5.2-1 .6-1.4l5.1-5.6c.8-.9 2.3-.5 2.6.7.1.4.1.8 0 1.2L15.4 9h4.1c1.6 0 2.8 1.5 2.4 3.1l-1.6 6.6A3 3 0 0 1 17.4 21H10a2 2 0 0 1-2-2v-8.7z";
    public const string Heart = "M12 20.6l-1.3-1.2C6 15.2 3 12.4 3 8.9 3 6.1 5.2 4 7.9 4c1.6 0 3.1.7 4.1 1.9C13 4.7 14.5 4 16.1 4 18.8 4 21 6.1 21 8.9c0 3.5-3 6.3-7.7 10.5L12 20.6z";
    public const string Coin = "M12 2.5a9.5 9.5 0 1 1 0 19 9.5 9.5 0 0 1 0-19zm0 2a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15zM8.5 8h7a1 1 0 0 1 0 2H13v6a1 1 0 0 1-2 0v-6H8.5a1 1 0 0 1 0-2z";
    public const string Star = "M12 2.6l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 16.9l-5.6 2.9 1.1-6.2L3 9.2l6.2-.9z";
    public const string Comment = "M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v9a2.5 2.5 0 0 1-2.5 2.5H10l-4.3 3.4c-.7.5-1.7.1-1.7-.8V5.5zM8 9a1.2 1.2 0 1 0 0 2.4A1.2 1.2 0 0 0 8 9zm4 0a1.2 1.2 0 1 0 0 2.4A1.2 1.2 0 0 0 12 9zm4 0a1.2 1.2 0 1 0 0 2.4A1.2 1.2 0 0 0 16 9z";
    public const string Share = "M13 4.6c0-.9 1-1.3 1.7-.7l7 6.4c.4.4.4 1.1 0 1.5l-7 6.4c-.7.6-1.7.2-1.7-.7V14c-4.3 0-7.3 1.4-9.4 4.6-.3.5-1.1.2-1-.4C3.4 12.6 6.8 9.4 13 8.9V4.6z";
    public const string Music = "M9 18.5a3.5 3.5 0 1 1-2-3.16V6.2c0-.7.5-1.3 1.2-1.5l9.5-2.1c.9-.2 1.8.5 1.8 1.5v11.4a3.5 3.5 0 1 1-2-3.16V7.6L9 9.4v9.1z";
    public const string Image = "M5.5 3h13A2.5 2.5 0 0 1 21 5.5v13a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 18.5v-13A2.5 2.5 0 0 1 5.5 3zm3 3.5a2 2 0 1 0 0 4 2 2 0 0 0 0-4zM5 17.6V18.5c0 .3.2.5.5.5h13c.3 0 .5-.2.5-.5v-3.1l-3.3-3.3a1 1 0 0 0-1.4 0L9.5 17l-1.8-1.8a1 1 0 0 0-1.4 0L5 17.6z";
    public const string Playlist = "M4 6h11a1 1 0 0 1 0 2H4a1 1 0 0 1 0-2zm0 5h11a1 1 0 0 1 0 2H4a1 1 0 0 1 0-2zm0 5h6a1 1 0 0 1 0 2H4a1 1 0 0 1 0-2zm11.5-1.9v5.8c0 .8.9 1.3 1.5.8l4.2-2.9c.6-.4.6-1.2 0-1.6L17 14.1c-.6-.5-1.5 0-1.5.8z";
}
