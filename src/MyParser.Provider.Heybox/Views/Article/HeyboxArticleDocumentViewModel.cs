using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.CardRendering;

namespace MyParser.Provider.Heybox.Views;

/// <summary>
/// 小黑盒长文章：阅读排版。顶部封面横幅，下面是平台标识、标题、作者、话题，
/// 正文按段落 / 小标题 / 引用 / 图片 / 视频逐块排版，末尾一行互动计数。高度随内容撑开。
/// </summary>
public sealed class HeyboxArticleDocumentViewModel
{
    public const double ContentWidth = 560;

    public Bitmap? Cover { get; init; } = LoadDesignBitmap("background.png");
    public bool HasCover => Cover is not null;
    public Bitmap? Avatar { get; init; } = LoadDesignBitmap("avatar.jpg");
    public bool HasAvatar => Avatar is not null;
    public bool HasNoAvatar => Avatar is null;
    public string AvatarInitial => string.IsNullOrWhiteSpace(AuthorName) ? "盒" : AuthorName.Trim()[..1];

    public string KindText { get; init; } = "文章";
    public string Title { get; init; } = "《黑神话：悟空》全收集路线整理：每一章的隐藏 Boss 与珍玩位置";
    public string AuthorName { get; init; } = "盒友小黑";
    public string MetaText { get; init; } = "2026-11-25 发布";
    public IReadOnlyList<string> Topics { get; init; } = ["黑神话悟空", "攻略"];
    public bool HasTopics => Topics.Count > 0;

    public IReadOnlyList<HeyboxStat> Stats { get; init; } =
    [
        new(HeyboxIcons.View, "3.4万"), new(HeyboxIcons.Like, "1,208"),
        new(HeyboxIcons.Favorite, "876"), new(HeyboxIcons.Comment, "213"),
    ];
    public bool HasStats => Stats.Count > 0;

    public IReadOnlyList<HeyboxArticleBlockViewModel> Blocks { get; init; } =
    [
        HeyboxArticleBlockViewModel.Paragraph("整理了一周，按章节顺序把隐藏 Boss 和珍玩都标出来了，照着路线走可以一次拿全。"),
        HeyboxArticleBlockViewModel.Heading("第一回 · 火照黑云", 2),
        HeyboxArticleBlockViewModel.Paragraph("开局先别急着推主线，土地庙右侧的小路可以绕到广智的背后。"),
        HeyboxArticleBlockViewModel.Quote("提示：隐藏 Boss 只在白天出现，夜里过去会扑空。"),
        HeyboxArticleBlockViewModel.Video("https://example.com/video.mp4"),
    ];

    public string BrandText { get; } = "By ShirokaProject && MyParser v" +
        (typeof(HeyboxArticleDocumentViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown");

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

public enum HeyboxArticleBlockKind { Paragraph, Heading, Quote, Image, Video }

/// <summary>正文中的一块。模板按 Is* 标志切换样式，不需要预估高度。</summary>
public sealed class HeyboxArticleBlockViewModel
{
    public HeyboxArticleBlockKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public Bitmap? Image { get; init; }
    public string Caption { get; init; } = string.Empty;
    public int Level { get; init; } = 2;

    public bool IsParagraph => Kind == HeyboxArticleBlockKind.Paragraph;
    public bool IsHeading => Kind == HeyboxArticleBlockKind.Heading;
    public bool IsQuote => Kind == HeyboxArticleBlockKind.Quote;
    public bool IsImage => Kind == HeyboxArticleBlockKind.Image && Image is not null;
    public bool IsVideo => Kind == HeyboxArticleBlockKind.Video;
    public bool HasCaption => IsImage && !string.IsNullOrWhiteSpace(Caption);

    /// <summary>一级标题 22，二级 19，更低 17。</summary>
    public double HeadingSize => Level <= 1 ? 22 : Level == 2 ? 19 : 17;

    /// <summary>横图占满正文宽度；竖图最高为正文宽度的 1.25 倍，宽度按比例缩小后居中。</summary>
    public double ImageHeight => ImageSize.Height;
    public double ImageWidth => ImageSize.Width;

    private (double Width, double Height) ImageSize
    {
        get
        {
            if (Image is not { PixelSize: { Width: > 0, Height: > 0 } size }) return (0, 0);
            const double width = HeyboxArticleDocumentViewModel.ContentWidth;
            const double maxHeight = width * 1.25;
            var natural = width * size.Height / size.Width;
            return natural <= maxHeight
                ? (width, Math.Round(natural))
                : (Math.Round(maxHeight * size.Width / size.Height), maxHeight);
        }
    }

    public static HeyboxArticleBlockViewModel Paragraph(string text) => new() { Kind = HeyboxArticleBlockKind.Paragraph, Text = text };
    public static HeyboxArticleBlockViewModel Heading(string text, int level) => new() { Kind = HeyboxArticleBlockKind.Heading, Text = text, Level = level };
    public static HeyboxArticleBlockViewModel Quote(string text) => new() { Kind = HeyboxArticleBlockKind.Quote, Text = text };
    public static HeyboxArticleBlockViewModel Video(string url) => new() { Kind = HeyboxArticleBlockKind.Video, Text = url };
    public static HeyboxArticleBlockViewModel Picture(Bitmap image, string caption) => new() { Kind = HeyboxArticleBlockKind.Image, Image = image, Caption = caption };
}
