using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.CardRendering;

namespace MyParser.Provider.BiliBili.Views;

/// <summary>
/// B 站专栏长图：与 B 站视频卡片同一套视觉（樱花粉 → 天蓝渐变、小电视角标、渐变描边头像、毛玻璃数据栏）。
/// 顶部头图，下面是标题、作者（不加描边，有头像框就叠上）、分类，正文按段落 / 小标题 / 引用 / 图片逐块排版，高度随内容撑开。
/// </summary>
public sealed class BiliArticleDocumentViewModel
{
    public const double ContentWidth = 560;

    private static readonly MediaCardPlatform Platform = MediaCardPlatform.Bilibili;

    public IBrush AccentGradient => Platform.CreateGradient(1, 1);
    public IBrush AccentSoftGradient => Platform.CreateGradient(0.13, 0.13);
    public IBrush AccentLineGradient => Platform.CreateGradient(0.35, 0.35);
    public IBrush PrimaryBrush => new SolidColorBrush(Platform.Primary);

    public Bitmap? Cover { get; init; } = LoadDesignBitmap("background.png");
    public bool HasCover => Cover is not null;
    public Bitmap? Avatar { get; init; } = LoadDesignBitmap("avatar.jpg");
    public bool HasAvatar => Avatar is not null;
    /// <summary>头像框（装扮挂件），按头像（40）的 1.75 倍显示。</summary>
    public Bitmap? AvatarPendant { get; init; }
    public bool HasAvatarPendant => AvatarPendant is not null;
    // 头像框大小和四周间距按头像框实际装饰范围动态计算，见 AvatarPendantLayout。
    public double AvatarSize => AvatarLayout.AvatarSize;
    public double PendantSize => AvatarLayout.PendantSize;
    public Thickness PendantMargin => AvatarLayout.PendantMargin;
    public Thickness AuthorTextMargin => AvatarLayout.TextMargin;
    public Thickness AuthorRowMargin => new(AvatarLayout.ColumnMargin.Left, AvatarLayout.RowMargin.Top, 0, AvatarLayout.RowMargin.Bottom);
    private AvatarPendantLayout AvatarLayout => _avatarLayout ??= AvatarPendantLayout.Create(AvatarPendant, unitSize: 54, maxAvatar: 40, ringSize: 44, textGap: 7, new AvatarPendantLayout(40, 0, default, default, default, new Thickness(11, 0, 0, 0)));
    private AvatarPendantLayout? _avatarLayout;
    public string AvatarInitial => string.IsNullOrWhiteSpace(AuthorName) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(AuthorName.Trim(), 0));

    public string KindText { get; init; } = "专栏";
    public string Title { get; init; } = "从零开始的 Avalonia 卡片渲染：毛玻璃、渐进模糊与平台配色";
    public string AuthorName { get; init; } = "作者名字";
    public string AuthorMeta { get; init; } = "2.3万粉丝";
    public string MetaText { get; init; } = "2026-11-27 发布 · 3,560 字";
    public IReadOnlyList<string> Tags { get; init; } = ["#科技", "#教程"];
    public bool HasTags => Tags.Count > 0;

    public IReadOnlyList<MediaCardMetric> Metrics { get; init; } =
    [
        new(MediaCardIcons.Play, "阅读", "3.4万"),
        new(MediaCardIcons.ThumbUp, "点赞", "1,208"),
        new(MediaCardIcons.Coin, "投币", "243"),
        new(MediaCardIcons.Star, "收藏", "876"),
        new(MediaCardIcons.Comment, "评论", "213"),
    ];
    public bool HasMetrics => Metrics.Count > 0;

    public IReadOnlyList<BiliArticleBlockViewModel> Blocks { get; init; } =
    [
        BiliArticleBlockViewModel.Paragraph("这是一段正文。卡片的底色取自封面模糊，再叠一层平台双色调，整体色调就会跟着封面走。"),
        BiliArticleBlockViewModel.Heading("一、渐进模糊", 2),
        BiliArticleBlockViewModel.Paragraph("清晰层和模糊层叠在一起，模糊层只在下半部分逐渐出现。"),
        BiliArticleBlockViewModel.Quote("提示：BoxShadow 和 BackdropEffect 叠用在 2x 渲染下会错位。"),
    ];


    private static Bitmap? LoadDesignBitmap(string fileName, [CallerFilePath] string sourceFilePath = "")
    {
        if (!Design.IsDesignMode) return null;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? "."); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "MyParser", "Assets", "Demo", fileName);
            if (File.Exists(path)) return fileName == "avatar.jpg" ? AvatarBitmapResizer.DecodeFile(path, 40) : new Bitmap(path);
        }

        return null;
    }
}

public enum BiliArticleBlockKind { Paragraph, Heading, Quote, Image }

/// <summary>正文中的一块。模板按 Is* 标志切换样式，不需要预估高度。</summary>
public sealed class BiliArticleBlockViewModel
{
    public BiliArticleBlockKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public Bitmap? Image { get; init; }
    public string Caption { get; init; } = string.Empty;
    public int Level { get; init; } = 2;

    public bool IsParagraph => Kind == BiliArticleBlockKind.Paragraph;
    public bool IsHeading => Kind == BiliArticleBlockKind.Heading;
    public bool IsQuote => Kind == BiliArticleBlockKind.Quote;
    public bool IsImage => Kind == BiliArticleBlockKind.Image && Image is not null;
    public bool HasCaption => IsImage && !string.IsNullOrWhiteSpace(Caption);

    /// <summary>一级标题 22，二级 19，更低 17。</summary>
    public double HeadingSize => Level <= 1 ? 22 : Level == 2 ? 19 : 17;

    /// <summary>
    /// 图片区域总是撑满正文宽度。横图按比例算高度；竖图最高为正文宽度的 1.25 倍，
    /// 超出时原图完整地居中显示，两侧用同一张图模糊放大铺底（<see cref="HasBackdrop"/>），不裁内容。
    /// </summary>
    public double ImageWidth => BiliArticleDocumentViewModel.ContentWidth;
    public double ImageHeight => Math.Min(NaturalHeight, MaxImageHeight);
    public bool HasBackdrop => IsImage && NaturalHeight > MaxImageHeight + 1;

    private const double MaxImageHeight = BiliArticleDocumentViewModel.ContentWidth * 1.25;
    private double NaturalHeight => Image is { PixelSize: { Width: > 0, Height: > 0 } size }
        ? Math.Round(BiliArticleDocumentViewModel.ContentWidth * size.Height / size.Width)
        : 0;

    public static BiliArticleBlockViewModel Paragraph(string text) => new() { Kind = BiliArticleBlockKind.Paragraph, Text = text };
    public static BiliArticleBlockViewModel Heading(string text, int level) => new() { Kind = BiliArticleBlockKind.Heading, Text = text, Level = level };
    public static BiliArticleBlockViewModel Quote(string text) => new() { Kind = BiliArticleBlockKind.Quote, Text = text };
    public static BiliArticleBlockViewModel Picture(Bitmap image, string caption) => new() { Kind = BiliArticleBlockKind.Image, Image = image, Caption = caption };
}
