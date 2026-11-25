using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.CardRendering;

namespace MyParser.Provider.Heybox.Views;

/// <summary>
/// 小黑盒帖子卡片：游戏社区信息流排版。作者头像与发布时间、标题正文、封面（右下角图片/视频数）、
/// 话题标签和一行带图标的互动计数。
/// </summary>
public sealed class HeyboxCardViewModel
{
    /// <summary>封面区域宽度（卡片 360 − 左右各 16 边距）。</summary>
    public const double CoverWidth = 328;

    public Bitmap? Cover { get; init; } = LoadDesignBitmap("background.png");
    public bool HasCover => Cover is not null;
    public Bitmap? Avatar { get; init; } = LoadDesignBitmap("avatar.jpg");
    public bool HasAvatar => Avatar is not null;
    public bool HasNoAvatar => Avatar is null;
    public string AvatarInitial => string.IsNullOrWhiteSpace(AuthorName) ? "盒" : AuthorName.Trim()[..1];

    public string AuthorName { get; init; } = "盒友小黑";
    public string MetaText { get; init; } = "3 小时前 · 帖子";
    public string Title { get; init; } = "《艾尔登法环》DLC 全 Boss 无伤攻略合集，附配装思路";
    public string Description { get; init; } = "整理了一周，所有 Boss 的出招前摇和安全窗口都标出来了，新手照着打也能过。";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description) && Description.Trim() != Title.Trim();

    public IReadOnlyList<string> Topics { get; init; } = ["艾尔登法环", "攻略"];
    public bool HasTopics => Topics.Count > 0;

    /// <summary>封面右下角：图片数与视频数。</summary>
    public string ImageCountText { get; init; } = "12";
    public bool HasImageCount => !string.IsNullOrWhiteSpace(ImageCountText);
    public string VideoCountText { get; init; } = "1";
    public bool HasVideoCount => !string.IsNullOrWhiteSpace(VideoCountText);
    public bool HasMediaBadge => HasCover && (HasImageCount || HasVideoCount);

    public IReadOnlyList<HeyboxStat> Stats { get; init; } =
    [
        new(HeyboxIcons.View, "3.4万"),
        new(HeyboxIcons.Like, "1,208"),
        new(HeyboxIcons.Favorite, "876"),
        new(HeyboxIcons.Comment, "213"),
    ];
    public bool HasStats => Stats.Count > 0;

    /// <summary>按封面比例取高度，限制在 16:9 到 1:1 之间。</summary>
    public double CoverHeight
    {
        get
        {
            if (Cover is not { PixelSize: { Width: > 0, Height: > 0 } size }) return CoverWidth * 9 / 16;
            var ratio = Math.Clamp((double)size.Height / size.Width, 9d / 16, 1d);
            return Math.Round(CoverWidth * ratio);
        }
    }

    public string BrandText { get; } = "By ShirokaProject && MyParser v" +
        (typeof(HeyboxCardViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown");

    /// <summary>计数为空时不显示该项；0 显示为 0。</summary>
    public static IReadOnlyList<HeyboxStat> CreateStats(string? views, string? likes, string? favorites, string? comments) =>
        new (string Icon, string? Value)[]
        {
            (HeyboxIcons.View, views), (HeyboxIcons.Like, likes), (HeyboxIcons.Favorite, favorites), (HeyboxIcons.Comment, comments),
        }
        .Where(item => !string.IsNullOrWhiteSpace(item.Value))
        .Select(item => new HeyboxStat(item.Icon, MediaCardViewModel.FormatCount(item.Value)))
        .ToArray();

    private static Bitmap? LoadDesignBitmap(string fileName, [CallerFilePath] string sourceFilePath = "")
    {
        if (!Design.IsDesignMode) return null;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? "."); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "MyParser", "Assets", "Demo", fileName);
            if (File.Exists(path)) return fileName == "avatar.jpg" ? AvatarBitmapResizer.DecodeFile(path, 72) : new Bitmap(path);
        }

        return null;
    }
}

public sealed record HeyboxStat(string Icon, string Text);

/// <summary>小黑盒卡片图标（Material Design Icons，24x24）。</summary>
public static class HeyboxIcons
{
    public const string View = "M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9M12,4.5C17,4.5 21.27,7.61 23,12C21.27,16.39 17,19.5 12,19.5C7,19.5 2.73,16.39 1,12C2.73,7.61 7,4.5 12,4.5M3.18,12C4.83,15.36 8.24,17.5 12,17.5C15.76,17.5 19.17,15.36 20.82,12C19.17,8.64 15.76,6.5 12,6.5C8.24,6.5 4.83,8.64 3.18,12Z";
    public const string Like = "M5,9V21H1V9H5M9,21A2,2 0 0,1 7,19V9C7,8.45 7.22,7.95 7.59,7.59L14.17,1L15.23,2.06C15.5,2.33 15.67,2.7 15.67,3.11L15.64,3.43L14.69,8H21C22.11,8 23,8.9 23,10V12C23,12.26 22.95,12.5 22.86,12.73L19.84,19.78C19.54,20.5 18.83,21 18,21H9M9,19H18.03L21,12V10H12.21L13.34,4.68L9,9.03V19Z";
    public const string Favorite = "M12,15.39L8.24,17.66L9.23,13.38L5.91,10.5L10.29,10.13L12,6.09L13.71,10.13L18.09,10.5L14.77,13.38L15.76,17.66M22,9.24L14.81,8.63L12,2L9.19,8.63L2,9.24L7.45,13.97L5.82,21L12,17.27L18.18,21L16.54,13.97L22,9.24Z";
    public const string Comment = "M9,22A1,1 0 0,1 8,21V18H4A2,2 0 0,1 2,16V4C2,2.89 2.9,2 4,2H20A2,2 0 0,1 22,4V16A2,2 0 0,1 20,18H13.9L10.2,21.71C10,21.9 9.75,22 9.5,22V22H9M10,16V19.08L13.08,16H20V4H4V16H10Z";
    public const string Images = "M21,17H7V3H21M21,1H7A2,2 0 0,0 5,3V17A2,2 0 0,0 7,19H21A2,2 0 0,0 23,17V3A2,2 0 0,0 21,1M3,5H1V21A2,2 0 0,0 3,23H19V21H3M15.96,10.29L13.21,13.83L11.25,11.47L8.5,15H19.5L15.96,10.29Z";
    public const string Videos = "M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M10,16.5L16,12L10,7.5V16.5Z";
    public const string Brand = "M7,6H17A6,6 0 0,1 23,12A6,6 0 0,1 17,18C15.22,18 13.63,17.23 12.53,16H11.47C10.37,17.23 8.78,18 7,18A6,6 0 0,1 1,12A6,6 0 0,1 7,6M6,9V11H4V13H6V15H8V13H10V11H8V9H6M15.5,12A1.5,1.5 0 0,0 14,13.5A1.5,1.5 0 0,0 15.5,15A1.5,1.5 0 0,0 17,13.5A1.5,1.5 0 0,0 15.5,12M18.5,9A1.5,1.5 0 0,0 17,10.5A1.5,1.5 0 0,0 18.5,12A1.5,1.5 0 0,0 20,10.5A1.5,1.5 0 0,0 18.5,9Z";
    public const string Share = "M14,5V9C7,10 4,15 3,20C5.5,16.5 9,14.9 14,14.9V19L21,12L14,5M16,9.83L18.17,12L16,14.17V12.9H14C11.93,12.9 10.07,13.28 8.34,13.85C9.74,12.46 11.54,11.37 14.28,11L16,10.73V9.83Z";
}
