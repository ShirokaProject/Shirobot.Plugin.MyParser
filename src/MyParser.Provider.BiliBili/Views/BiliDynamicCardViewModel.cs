using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.CardRendering;

namespace MyParser.Provider.BiliBili.Views;

/// <summary>
/// B 站动态（opus 里没有标题的图文动态）：帖子样式，头像（含头像框）+ 名字 + 认证 / 发布时间，
/// 正文，按 B 站规则排的图片宫格，转发 / 评论 / 点赞。配色与视频卡片一致。
/// </summary>
public sealed class BiliDynamicCardViewModel
{
    public const double CardWidth = 520;
    public const double ContentWidth = CardWidth - 2 * Padding;
    private const double Padding = 24;
    private const double Gap = 4;
    public const int MaxTiles = 9;

    private static readonly MediaCardPlatform Platform = MediaCardPlatform.Bilibili;

    public IBrush AccentGradient => Platform.CreateGradient(1, 1);
    public IBrush AccentSoftGradient => Platform.CreateGradient(0.13, 0.13);
    public IBrush PrimaryBrush => new SolidColorBrush(Platform.Primary);

    public Bitmap? Avatar { get; init; } = LoadDesignBitmap("avatar.jpg", 40);
    public bool HasAvatar => Avatar is not null;
    public string AvatarInitial => string.IsNullOrWhiteSpace(AuthorName) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(AuthorName.Trim(), 0));
    public Bitmap? AvatarPendant { get; init; }
    public bool HasAvatarPendant => AvatarPendant is not null;

    // 头像框按装饰实际范围排版，整体限制在 50 的方框里（与视频卡片同一套规则，基准大一号）。
    public double AvatarSize => AvatarLayout.AvatarSize;
    public double PendantSize => AvatarLayout.PendantSize;
    public Thickness PendantMargin => AvatarLayout.PendantMargin;
    public Thickness AvatarColumnMargin => AvatarLayout.ColumnMargin;
    public Thickness AuthorTextMargin => AvatarLayout.TextMargin;
    private AvatarPendantLayout AvatarLayout => _avatarLayout ??= AvatarPendantLayout.Create(AvatarPendant, unitSize: 50, maxAvatar: 40,
        ringSize: 40, textGap: 6, new AvatarPendantLayout(40, 0, default, default, default, new Thickness(10, 0, 0, 0)));
    private AvatarPendantLayout? _avatarLayout;

    public string AuthorName { get; init; } = "哔哩哔哩漫画";
    /// <summary>名字下面一行：发布时间，有认证时接在后面（「2022-10-20 14:28 · 哔哩哔哩漫画官方账号」）。</summary>
    public string AuthorMeta { get; init; } = "2022-10-20 14:28 · 哔哩哔哩漫画官方账号";
    public bool HasAuthorMeta => !string.IsNullOrWhiteSpace(AuthorMeta);

    public string Text { get; init; } = "【#哔哩哔哩漫画#作品推荐】《#勿言推理#》：男主这张脸画得也太犯规了！冬天，一个适合吃咖喱的日子里，大学生久能正在公寓里切着洋葱，警察突然上门……";
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>原图列表，宫格按数量排：1 张整宽；2、4 张两列；其余三列，最多 9 格，多出的在最后一格显示 +N。</summary>
    public IReadOnlyList<Bitmap> Images { get; init; } = [];
    /// <summary>动态的图片总数；只解码前 9 张，多出的数量显示在最后一格。</summary>
    public int ImageCount { get; init; }
    private int TotalImages => Math.Max(ImageCount, Images.Count);

    public IReadOnlyList<BiliDynamicTileViewModel> Tiles => _tiles ??= BuildTiles();
    private IReadOnlyList<BiliDynamicTileViewModel>? _tiles;
    public bool HasTiles => Tiles.Count > 0;
    public int GridColumns => GridColumnsFor(TotalImages);
    public static int GridColumnsFor(int count) => count switch { 1 => 1, 2 or 4 => 2, _ => 3 };
    /// <summary>宫格里每格的边长（单图时是整宽），渲染前按这个尺寸解码图片。</summary>
    public static double TileSizeFor(int count) => count <= 1
        ? ContentWidth
        : Math.Floor((ContentWidth - Gap * (GridColumnsFor(count) - 1)) / GridColumnsFor(count));
    public double GridWidth => ContentWidth;

    public IReadOnlyList<MediaCardMetric> Metrics { get; init; } =
    [
        new(MediaCardIcons.Share, "转发", "88"),
        new(MediaCardIcons.Comment, "评论", "58"),
        new(MediaCardIcons.ThumbUp, "点赞", "3,144"),
    ];
    public bool HasMetrics => Metrics.Count > 0;

    private IReadOnlyList<BiliDynamicTileViewModel> BuildTiles()
    {
        if (Images.Count == 0) return [];
        if (TotalImages == 1)
        {
            // 单图整宽：横图按比例；竖图最高为宽度的 1.1 倍，超出时原图居中、两侧模糊铺底。
            var image = Images[0];
            var natural = image.PixelSize.Width > 0 ? ContentWidth * image.PixelSize.Height / image.PixelSize.Width : ContentWidth;
            var max = ContentWidth * 1.1;
            return [new BiliDynamicTileViewModel(image, ContentWidth, Math.Round(Math.Min(natural, max)), natural > max + 1, string.Empty)];
        }

        var size = TileSizeFor(TotalImages);
        var shown = Images.Take(MaxTiles).ToArray();
        var extra = TotalImages - shown.Length;
        return shown.Select((image, index) => new BiliDynamicTileViewModel(image, size, size, false,
            index == shown.Length - 1 && extra > 0 ? "+" + extra : string.Empty)).ToArray();
    }

    private static Bitmap? LoadDesignBitmap(string fileName, double avatarSize, [CallerFilePath] string sourceFilePath = "")
    {
        if (!Design.IsDesignMode) return null;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? "."); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "MyParser", "Assets", "Demo", fileName);
            if (File.Exists(path)) return AvatarBitmapResizer.DecodeFile(path, avatarSize);
        }

        return null;
    }
}

/// <summary>宫格里的一格。</summary>
public sealed record BiliDynamicTileViewModel(Bitmap Image, double Width, double Height, bool HasBackdrop, string MoreText)
{
    public bool HasMore => !string.IsNullOrEmpty(MoreText);
}
