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
    public bool IsWeixinChannels => Platform.Kind == MediaCardPlatformKind.WeixinChannels;
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
    /// <summary>官方账号头像（如番剧）小一号、不加渐变描边，和普通用户区分。</summary>
    public bool CompactAvatar { get; init; }
    public double AvatarImageSize => AvatarLayout.AvatarSize;
    /// <summary>是否画平台渐变描边。B 站设为 false：有头像框就显示头像框，没有就只显示头像。</summary>
    public bool AvatarRing { get; init; } = true;
    /// <summary>B 站头像框（装扮挂件），透明 PNG，按头像的 1.75 倍显示。</summary>
    public Bitmap? AvatarPendant { get; init; }
    public bool HasAvatarPendant => AvatarPendant is not null;
    public bool ShowAvatarRing => AvatarRing && !CompactAvatar && !HasAvatarPendant;
    // 头像框大小和四周间距按头像框实际装饰范围动态计算，见 AvatarPendantLayout。
    public double PendantSize => AvatarLayout.PendantSize;
    public Thickness PendantMargin => AvatarLayout.PendantMargin;
    public Thickness AuthorTextMargin => AvatarLayout.TextMargin;
    public Thickness AuthorRowMargin => AvatarLayout.RowMargin;
    public Thickness AvatarColumnMargin => AvatarLayout.ColumnMargin;
    private AvatarPendantLayout AvatarLayout => _avatarLayout ??= AvatarPendantLayout.Create(AvatarPendant, unitSize: 44, maxAvatar: 32, ringSize: 38, textGap: 5, new AvatarPendantLayout(CompactAvatar ? 26 : 32, 0, default, default, new Thickness(0, 2, 0, 0),
            new Thickness(CompactAvatar ? 7 : 9, 0, 0, 0)));
    private AvatarPendantLayout? _avatarLayout;

    /// <summary>「哔哩哔哩番剧」官方账号头像（内嵌资源）。</summary>
    public static Bitmap? BilibiliBangumiAvatar => EmbeddedBitmaps.Load(ref _bangumiAvatar, "MyParser.Bilibili.BangumiAvatar.jpg", 26);
    private static Bitmap? _bangumiAvatar;
    public string AuthorName { get; init; } = "作者名字";
    public string AvatarInitial => string.IsNullOrWhiteSpace(AuthorName) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(AuthorName.Trim(), 0));
    public string AuthorMeta { get; init; } = "IP属地：广东";
    public bool HasAuthorMeta => !string.IsNullOrWhiteSpace(AuthorMeta);
    private LinearGradientBrush Gradient(double primaryOpacity, double secondaryOpacity) =>
        Platform.CreateGradient(primaryOpacity, secondaryOpacity);

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
    NetEase,
    WeixinChannels,
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

    /// <summary>网易云音乐：朱红 → 暖橘，红底徽标。</summary>
    public static readonly MediaCardPlatform NetEase = new(MediaCardPlatformKind.NetEase, "网易云音乐",
        Color.Parse("#FF3A5C"), Color.Parse("#FF9548"), Color.Parse("#FFE60026"), Color.Parse("#FFFFFF"));

    /// <summary>微信视频号：微信绿 → 视频号橙，白底徽标。</summary>
    public static readonly MediaCardPlatform WeixinChannels = new(MediaCardPlatformKind.WeixinChannels, "微信视频号",
        Color.Parse("#07C160"), Color.Parse("#FA9D3B"), Color.Parse("#F2FFFFFF"), Color.Parse("#07C160"));

    public bool IsBilibili => Kind == MediaCardPlatformKind.Bilibili;
    public bool IsDouyin => Kind == MediaCardPlatformKind.Douyin;
    public bool IsNetEase => Kind == MediaCardPlatformKind.NetEase;

    /// <summary>平台双色斜向渐变，透明度分别作用于两端。</summary>
    public LinearGradientBrush CreateGradient(double primaryOpacity, double secondaryOpacity) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(WithOpacity(Primary, primaryOpacity), 0),
            new GradientStop(WithOpacity(Secondary, secondaryOpacity), 1),
        },
    };

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);
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
    /// <summary>B 站官方投币图标（视频页工具栏，28×28 视图）。</summary>
    public const string Coin = "M14.045 25.5454C7.69377 25.5454 2.54504 20.3967 2.54504 14.0454C2.54504 7.69413 7.69377 2.54541 14.045 2.54541C20.3963 2.54541 25.545 7.69413 25.545 14.0454C25.545 17.0954 24.3334 20.0205 22.1768 22.1771C20.0201 24.3338 17.095 25.5454 14.045 25.5454ZM9.66202 6.81624H18.2761C18.825 6.81624 19.27 7.22183 19.27 7.72216C19.27 8.22248 18.825 8.62807 18.2761 8.62807H14.95V10.2903C17.989 10.4444 20.3766 12.9487 20.3855 15.9916V17.1995C20.3854 17.6997 19.9799 18.1052 19.4796 18.1052C18.9793 18.1052 18.5738 17.6997 18.5737 17.1995V15.9916C18.5667 13.9478 16.9882 12.2535 14.95 12.1022V20.5574C14.95 21.0577 14.5444 21.4633 14.0441 21.4633C13.5437 21.4633 13.1382 21.0577 13.1382 20.5574V12.1022C11.1 12.2535 9.52148 13.9478 9.51448 15.9916V17.1995C9.5144 17.6997 9.10883 18.1052 8.60856 18.1052C8.1083 18.1052 7.70273 17.6997 7.70265 17.1995V15.9916C7.71158 12.9487 10.0992 10.4444 13.1382 10.2903V8.62807H9.66202C9.11309 8.62807 8.66809 8.22248 8.66809 7.72216C8.66809 7.22183 9.11309 6.81624 9.66202 6.81624Z";
    public const string Star = "M12 2.6l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 16.9l-5.6 2.9 1.1-6.2L3 9.2l6.2-.9z";
    public const string Comment = "M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v9a2.5 2.5 0 0 1-2.5 2.5H10l-4.3 3.4c-.7.5-1.7.1-1.7-.8V5.5zM8 9a1.2 1.2 0 1 0 0 2.4A1.2 1.2 0 0 0 8 9zm4 0a1.2 1.2 0 1 0 0 2.4A1.2 1.2 0 0 0 12 9zm4 0a1.2 1.2 0 1 0 0 2.4A1.2 1.2 0 0 0 16 9z";
    public const string Share = "M13 4.6c0-.9 1-1.3 1.7-.7l7 6.4c.4.4.4 1.1 0 1.5l-7 6.4c-.7.6-1.7.2-1.7-.7V14c-4.3 0-7.3 1.4-9.4 4.6-.3.5-1.1.2-1-.4C3.4 12.6 6.8 9.4 13 8.9V4.6z";
    /// <summary>下载：向下箭头 + 托盘。</summary>
    public const string Download = "M12 3a1 1 0 0 1 1 1v8.59l2.3-2.3a1 1 0 1 1 1.4 1.42l-4 4a1 1 0 0 1-1.4 0l-4-4a1 1 0 1 1 1.4-1.42l2.3 2.3V4a1 1 0 0 1 1-1zM5 15a1 1 0 0 1 1 1v2a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1v-2a1 1 0 1 1 2 0v2a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3v-2a1 1 0 0 1 1-1z";
    public const string Music = "M9 18.5a3.5 3.5 0 1 1-2-3.16V6.2c0-.7.5-1.3 1.2-1.5l9.5-2.1c.9-.2 1.8.5 1.8 1.5v11.4a3.5 3.5 0 1 1-2-3.16V7.6L9 9.4v9.1z";
    public const string Image = "M5.5 3h13A2.5 2.5 0 0 1 21 5.5v13a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 18.5v-13A2.5 2.5 0 0 1 5.5 3zm3 3.5a2 2 0 1 0 0 4 2 2 0 0 0 0-4zM5 17.6V18.5c0 .3.2.5.5.5h13c.3 0 .5-.2.5-.5v-3.1l-3.3-3.3a1 1 0 0 0-1.4 0L9.5 17l-1.8-1.8a1 1 0 0 0-1.4 0L5 17.6z";
    public const string Reply = "M10 8.2V5.6c0-.9-1-1.3-1.7-.7l-5.6 5.2c-.4.4-.4 1.1 0 1.5l5.6 5.2c.7.6 1.7.2 1.7-.7v-2.6c4.4 0 7.5 1.3 9.6 4.4.4.5 1.2.2 1.1-.4C20 11.6 16.4 8.6 10 8.2z";
    public const string Album = "M12 2.5a9.5 9.5 0 1 1 0 19 9.5 9.5 0 0 1 0-19zm0 6a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zm0 2.3a1.2 1.2 0 1 1 0 2.4 1.2 1.2 0 0 1 0-2.4z";
    public const string Clock = "M12 2.5a9.5 9.5 0 1 1 0 19 9.5 9.5 0 0 1 0-19zm0 2a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15zM12 6.5a1 1 0 0 1 1 1v4.1l2.8 2.1a1 1 0 1 1-1.2 1.6l-3.2-2.4a1 1 0 0 1-.4-.8V7.5a1 1 0 0 1 1-1z";
    public const string Waveform = "M4 10a1 1 0 0 1 1 1v2a1 1 0 0 1-2 0v-2a1 1 0 0 1 1-1zm4-4a1 1 0 0 1 1 1v10a1 1 0 0 1-2 0V7a1 1 0 0 1 1-1zm4-3a1 1 0 0 1 1 1v16a1 1 0 0 1-2 0V4a1 1 0 0 1 1-1zm4 4a1 1 0 0 1 1 1v8a1 1 0 0 1-2 0V8a1 1 0 0 1 1-1zm4 3a1 1 0 0 1 1 1v2a1 1 0 0 1-2 0v-2a1 1 0 0 1 1-1z";
    public const string File = "M7 2.5h6.6c.5 0 1 .2 1.4.6l4.4 4.4c.4.4.6.9.6 1.4V19a2.5 2.5 0 0 1-2.5 2.5H7A2.5 2.5 0 0 1 4.5 19V5A2.5 2.5 0 0 1 7 2.5zm6 1.8V8a1 1 0 0 0 1 1h3.7L13 4.3z";
    public const string Fire = "M12.6 2.2c.3-.4 1-.3 1.1.2.6 2.3 2 3.9 3.5 5.4 1.6 1.7 3.3 3.5 3.3 6.6 0 4.3-3.6 7.6-8.5 7.6S3.5 18.7 3.5 14.4c0-2.6 1.2-4.6 2.8-6 .4-.3 1-.1 1.1.4.2 1.2.7 2.2 1.6 2.9.2-3.8 1.7-7 3.6-9.5z";
    /// <summary>B 站小电视（24x24）。</summary>
    public const string BilibiliTv = "M17.813 4.653h.854c1.51.054 2.769.578 3.773 1.574 1.004.995 1.524 2.249 1.56 3.76v7.36c-.036 1.51-.556 2.769-1.56 3.773s-2.262 1.524-3.773 1.56H5.333c-1.51-.036-2.769-.556-3.773-1.56S.036 18.858 0 17.347v-7.36c.036-1.511.556-2.765 1.56-3.76 1.004-.996 2.262-1.52 3.773-1.574h.774l-1.174-1.12a1.234 1.234 0 0 1-.373-.906c0-.356.124-.658.373-.907l.027-.027c.267-.249.573-.373.92-.373.347 0 .653.124.92.373L9.653 4.44c.071.071.134.142.187.213h4.267a.836.836 0 0 1 .16-.213l2.853-2.747c.267-.249.573-.373.92-.373.347 0 .662.151.929.4.267.249.391.551.391.907 0 .355-.124.657-.373.906zM5.333 7.24c-.746.018-1.373.276-1.88.773-.506.498-.769 1.13-.786 1.894v7.52c.017.764.28 1.395.786 1.893.507.498 1.134.756 1.88.773h13.334c.746-.017 1.373-.275 1.88-.773.506-.498.769-1.129.786-1.893v-7.52c-.017-.765-.28-1.396-.786-1.894-.507-.497-1.134-.755-1.88-.773zM8 11.107c.373 0 .684.124.933.373.25.249.383.569.4.96v1.173c-.017.391-.15.711-.4.96-.249.25-.56.374-.933.374s-.684-.125-.933-.374c-.25-.249-.383-.569-.4-.96V12.44c0-.373.129-.689.386-.947.258-.257.574-.386.947-.386zm8 0c.373 0 .684.124.933.373.25.249.383.569.4.96v1.173c-.017.391-.15.711-.4.96-.249.25-.56.374-.933.374s-.684-.125-.933-.374c-.25-.249-.383-.569-.4-.96V12.44c.017-.391.15-.711.4-.96.249-.249.56-.373.933-.373z";
    /// <summary>抖音音符（24x24）。</summary>
    public const string DouyinNote = "M16.4 2.5h-3.5v13.3a2.9 2.9 0 1 1-2.9-2.9c.3 0 .6.1.9.2V9.5a6.4 6.4 0 1 0 5.5 6.3V9.1a7.9 7.9 0 0 0 4.6 1.5V7.1a4.6 4.6 0 0 1-4.6-4.6z";
    /// <summary>网易云音乐（Remix Icon netease-cloud-music-line）。</summary>
    public const string WeChat = "M8.691 2.188C3.891 2.188 0 5.476 0 9.53c0 2.212 1.17 4.203 3.002 5.55a.59.59 0 0 1 .213.665l-.39 1.48c-.019.07-.048.141-.048.213 0 .163.13.295.29.295a.326.326 0 0 0 .167-.054l1.903-1.114a.864.864 0 0 1 .717-.098 10.16 10.16 0 0 0 2.837.403c.276 0 .543-.027.811-.05-.857-2.578.157-4.972 1.932-6.446 1.703-1.415 3.882-1.98 5.853-1.838-.576-3.583-4.196-6.348-8.596-6.348zM5.785 5.991c.642 0 1.162.529 1.162 1.18a1.17 1.17 0 0 1-1.162 1.178A1.17 1.17 0 0 1 4.623 7.17c0-.651.52-1.18 1.162-1.18zm5.813 0c.642 0 1.162.529 1.162 1.18a1.17 1.17 0 0 1-1.162 1.178 1.17 1.17 0 0 1-1.162-1.178c0-.651.52-1.18 1.162-1.18zm5.34 2.867c-1.797-.052-3.746.512-5.28 1.786-1.72 1.428-2.687 3.72-1.78 6.22.942 2.453 3.666 4.229 6.884 4.229.826 0 1.622-.12 2.361-.336a.722.722 0 0 1 .598.082l1.584.926a.272.272 0 0 0 .14.047c.134 0 .24-.111.24-.247 0-.06-.023-.12-.038-.177l-.327-1.233a.582.582 0 0 1-.023-.156.49.49 0 0 1 .201-.398C23.024 18.48 24 16.82 24 14.98c0-3.21-2.931-5.837-6.656-6.088V8.89c-.135-.01-.27-.027-.407-.03zm-2.53 3.274c.535 0 .969.44.969.982a.976.976 0 0 1-.969.983.976.976 0 0 1-.969-.983c0-.542.434-.982.97-.982zm4.844 0c.535 0 .969.44.969.982a.976.976 0 0 1-.969.983.976.976 0 0 1-.969-.983c0-.542.434-.982.969-.982z";
    public const string NetEase = "M10.4222 11.375C10.1278 12.4026 10.4341 13.4395 11.2058 14.0282C12.267 14.8376 13.7712 14.3289 14.0796 13.0331C14.1599 12.6958 14.1833 12.311 14.1067 11.9767C13.8775 10.9756 13.586 9.98862 13.3147 8.98094C11.9843 9.13543 10.7722 10.1533 10.4222 11.375ZM15.9698 11.0879C16.2427 12.1002 16.2553 13.1053 15.8435 14.0875C14.7148 16.7784 11.1215 17.2286 9.26951 14.9136C7.96829 13.2869 7.99065 10.953 9.32982 9.18031C10.1096 8.14796 11.1339 7.47322 12.3776 7.12595C12.5007 7.09159 12.6241 7.058 12.7566 7.02157C12.6731 6.60736 12.569 6.20612 12.5143 5.79828C12.3375 4.48137 13.026 3.29477 14.2582 2.7574C15.4836 2.22294 16.9661 2.54204 17.7889 3.51738C18.1936 3.99703 18.183 4.59854 17.7631 4.98218C17.3507 5.359 16.7665 5.32761 16.3276 4.89118C16.0809 4.64585 15.8185 4.45112 15.451 4.45569C14.9264 4.46223 14.4642 4.87382 14.5058 5.39329C14.5432 5.86105 14.6785 6.32376 14.8058 6.77892C14.8276 6.85679 15.0218 6.91415 15.1436 6.9321C16.4775 7.12862 17.6476 7.66332 18.6165 8.60769C21.1739 11.1006 21.4772 15.1394 19.2882 18.0482C17.7593 20.0797 15.6785 21.2165 13.1609 21.4567C8.53953 21.8977 4.49683 18.9278 3.46188 14.3992C2.5147 10.2551 4.8397 5.83074 8.79509 4.25032C9.38067 4.01635 9.93787 4.21869 10.1664 4.74827C10.3982 5.28546 10.147 5.83389 9.55552 6.09847C7.18759 7.15787 5.73935 8.9527 5.34076 11.5215C4.80806 14.9546 6.99662 18.2982 10.3416 19.2428C13.0644 20.0117 15.9994 19.0758 17.6494 16.9123C19.2354 14.8328 19.0484 11.8131 17.2221 10.0389C16.7172 9.54838 16.1246 9.21455 15.3988 9.02564C15.5974 9.74151 15.7879 10.4136 15.9698 11.0879Z";
    public const string Playlist = "M4 6h11a1 1 0 0 1 0 2H4a1 1 0 0 1 0-2zm0 5h11a1 1 0 0 1 0 2H4a1 1 0 0 1 0-2zm0 5h6a1 1 0 0 1 0 2H4a1 1 0 0 1 0-2zm11.5-1.9v5.8c0 .8.9 1.3 1.5.8l4.2-2.9c.6-.4.6-1.2 0-1.6L17 14.1c-.6-.5-1.5 0-1.5.8z";
}
