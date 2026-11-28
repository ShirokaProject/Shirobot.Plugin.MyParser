// 一键渲染全部卡片预览：dotnet run --project Tests/CardPreview [输出目录]
// 输出 all-light.png / all-dark.png（默认在 Tests/CardPreview/out，已被 .gitignore 忽略）。
// 只用仓库自带的演示图片（src/MyParser/Assets/Demo），数据都是手写的示例。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MyParser.Provider.NetEaseCloudMusic.Views;
using MyParser.Provider.Heybox.Views;
using Shirobot.Plugin.MyParser.CardRendering;
using Shirobot.Plugin.MyParser.Parsing;
using System.Reflection;
using MyParser.Provider.BiliBili.Views;

var repo = FindRepositoryRoot();
var assets = Path.Combine(repo, "src", "MyParser", "Assets", "Demo");
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repo, "Tests", "CardPreview", "out");
Directory.CreateDirectory(output);
AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var fontFamily = new FontFamily(OperatingSystem.IsMacOS() ? "PingFang SC" : OperatingSystem.IsWindows() ? "Microsoft YaHei" : "Noto Sans CJK SC");

var landscape = new Bitmap(Path.Combine(assets, "background.png"));
// 仓库里只有横图，竖图从横图中间裁出来。
var vertical = Portrait(landscape);
var albumArt = new Bitmap(Path.Combine(assets, "avatar.jpg"));
Bitmap Avatar(double size) => AvatarBitmapResizer.DecodeFile(Path.Combine(assets, "avatar.jpg"), size);
var now = DateTimeOffset.Now;

MediaContentBlock Comment(string user, string text, long likes, long replies, TimeSpan ago, string ip, bool author = false) => new()
{
    Kind = MediaContentKind.Text, Text = text, Caption = user,
    Attributes = new Dictionary<string, string>
    {
        ["category"] = "comment", ["likes"] = likes.ToString(), ["replies"] = replies.ToString(),
        ["created"] = (now - ago).ToUnixTimeSeconds().ToString(), ["ip"] = ip, ["is_author"] = author.ToString(),
    },
};

IReadOnlyList<CommentCardItem> Items(IEnumerable<(MediaContentBlock Block, bool Avatar, Bitmap? Image)> rows) =>
    rows.Select((row, index) => CommentCardLoader.CreateItem(row.Block, index, row.Avatar ? Avatar(30) : null, row.Image, now)).ToArray();

var biliComments = new CommentCardViewModel
{
    Platform = MediaCardPlatform.Bilibili, Cover = landscape,
    SourceTitle = "最新证据表明dream使用了新型空气动力学鞘翅", CountText = "热评 4", SourceMeta = "共 3,412 条评论",
    Comments = Items([
        (Comment("强走药大葱", "置顶一下：mod 和地图链接都在简介里，1.16.4 fabric 版本，别下错了[doge]", 2306, 87, TimeSpan.FromHours(5), "IP属地：安徽", author: true), true, null),
        (Comment("鞘翅物理学家", "我宣布这是 MCC 史上最离谱的一次飞行，速度曲线完全不符合原版的阻力模型，建议直接送去做风洞实验。", 15832, 432, TimeSpan.FromDays(2), "IP属地：广东"), true, null),
        (Comment("今天也要早睡", "附一张我复现的轨迹图", 912, 12, TimeSpan.FromDays(3), "IP属地：上海"), false, landscape),
        (Comment("路过的小透明", "前排", 3, 0, TimeSpan.FromMinutes(12), "IP属地：北京"), true, null),
    ]),
};
var douyinComments = new CommentCardViewModel
{
    Platform = MediaCardPlatform.Douyin, Cover = vertical,
    SourceTitle = "敲虾真的太好吃了！一口下去全是弹弹的虾肉 #美食 #家常菜", CountText = "热评 3", SourceMeta = "共 3,412 条评论",
    Comments = Items([
        (Comment("干饭人小王", "看饿了，明天就去菜市场买虾！请问用的是什么淀粉呀？", 12603, 236, TimeSpan.FromHours(20), "浙江"), true, null),
        (Comment("小厨娘", "用的是土豆淀粉哦～敲的时候力度要轻，不然容易破", 8840, 41, TimeSpan.FromHours(19), "浙江", author: true), true, null),
        (Comment("夜宵研究所", "做出来了！虽然有点丑但是真的很弹", 1204, 9, TimeSpan.FromDays(9), "四川"), true, vertical),
    ]),
};

var lrc = "[00:00.00]作词 : 格瑞普\n[00:01.00]作曲 : 格瑞普\n[00:02.00]编曲 : 某某\n[00:03.00]混音 : 某某\n" +
          "[00:12.10]妄想のステップ刻んで、キミのハートにフルコン\n[00:18.40]響けメロディ、夜空を突き抜けて\n[00:24.00]指先が紡ぐ奇跡、誰も止められない\n" +
          "[00:30.50]夢のステージへ、もう一度手を伸ばして\n[00:36.20]光の波に乗って\n[00:41.00]キミと二人で\n[00:46.00]まだ終わらない\n[00:51.00]この歌を\n[00:56.00]もう一度\n[01:01.00]届けたい\n[01:06.00]最後の一行";
var tlrc = "[by:译者]\n[00:12.10]踏着妄想的舞步，向你的心打出全连\n[00:18.40]响彻吧旋律，穿透夜空\n[00:24.00]指尖编织的奇迹，谁也无法阻止\n[00:30.50]向着梦想的舞台，再一次伸出手\n[00:36.20]乘着光之波浪\n[00:41.00]和你两个人";
var (lines, credits) = NetEaseMusicCardViewModel.ParseLyrics(lrc, tlrc, 10);
var music = new NetEaseMusicCardViewModel
{
    Cover = albumArt, Avatar = Avatar(92), Title = "电波妄想美少女Q", Artists = "格瑞普喵 / 示例歌手", Album = "MuseDash 特别集",
    DurationText = "3:45", Credits = credits, Lyrics = lines,
};
var (noLines, noCredits) = NetEaseMusicCardViewModel.ParseLyrics("", "", 10);
var musicEmpty = new NetEaseMusicCardViewModel
{
    Cover = landscape, Avatar = null, Title = "没有封面也没有歌词的纯音乐", Artists = "未知歌手", Album = "",
    DurationText = "", Credits = noCredits, Lyrics = noLines,
};


MediaCardViewModel Wx(Bitmap cover, string title, string desc, string[] tags, bool avatar) => new()
{
    Platform = MediaCardPlatform.WeixinChannels, Cover = cover, Avatar = avatar ? Avatar(32) : null,
    BadgeText = "1:24", BadgeIcon = MediaCardIcons.Play, Title = title, Meta = "2026-11-25 20:14 发布 · 视频",
    Description = desc, DescriptionMaxLines = 6, Tags = tags,
    Metrics = [new(MediaCardIcons.Heart, "点赞", "1.2万"), new(MediaCardIcons.Comment, "评论", "368"),
               new(MediaCardIcons.Star, "收藏", "2,301"), new(MediaCardIcons.Share, "转发", "856")],
    ExtraText = "", AuthorName = "山野日记", AuthorMeta = "",
};
var wxLand = Wx(landscape, "周末去山里露营，拍到了一整晚的星空，银河真的肉眼可见！", "", ["#露营", "#星空", "#治愈"], true);
var wxTall = Wx(vertical, "敲虾真的太好吃了", "土豆淀粉 + 轻轻敲，弹到飞起", [], false);
var hbFull = new HeyboxCardViewModel
{
    Cover = landscape, Avatar = Avatar(72), AuthorName = "盒友小黑", MetaText = "3 小时前 · 图文",
    Title = "《艾尔登法环》DLC 全 Boss 无伤攻略合集，附配装思路和每个阶段的安全输出窗口",
    Description = "整理了一周，所有 Boss 的出招前摇和安全窗口都标出来了，新手照着打也能过。评论区可以交流配装。",
    Topics = ["艾尔登法环", "攻略", "黄金树之影"], ImageCountText = "12", VideoCountText = "1",
    Stats = HeyboxCardViewModel.CreateStats("34210", "1208", "876", "213"),
};
var hbBare = new HeyboxCardViewModel
{
    Cover = null, Avatar = null, AuthorName = "路过的盒友", MetaText = "帖子",
    Title = "有没有人一起开黑？", Description = "", Topics = [], ImageCountText = "", VideoCountText = "",
    Stats = HeyboxCardViewModel.CreateStats("88", "3", "", "5"),
};
var hbArticle = new HeyboxArticleDocumentViewModel
{
    Cover = landscape, Avatar = Avatar(72), AuthorName = "盒友小黑", MetaText = "2026-11-25 20:14 发布",
    Title = "《黑神话：悟空》全收集路线整理：每一章的隐藏 Boss 与珍玩位置",
    Topics = ["黑神话悟空", "攻略", "全收集"],
    Stats = HeyboxCardViewModel.CreateStats("34210", "1208", "876", "213"),
    Blocks =
    [
        HeyboxArticleBlockViewModel.Paragraph("整理了一周，按章节顺序把隐藏 Boss 和珍玩都标出来了。照着路线走可以一次拿全，不用回头跑图。文末附了每一章的装备推荐。"),
        HeyboxArticleBlockViewModel.Heading("第一回 · 火照黑云", 2),
        HeyboxArticleBlockViewModel.Paragraph("开局先别急着推主线，土地庙右侧的小路可以绕到广智的背后，打完以后能直接拿到第一件珍玩。"),
        HeyboxArticleBlockViewModel.Picture(vertical, "土地庙右侧小路入口"),
        HeyboxArticleBlockViewModel.Quote("提示：隐藏 Boss 只在白天出现，夜里过去会扑空。"),
        HeyboxArticleBlockViewModel.Heading("第二回 · 风起黄昏", 2),
        HeyboxArticleBlockViewModel.Paragraph("沙国的地图很大，建议先把三个土地庙都点亮再开始收集。"),
        HeyboxArticleBlockViewModel.Video("https://video.xiaoheihe.cn/example/route-chapter2.mp4"),
    ],
};
var hbArticleBare = new HeyboxArticleDocumentViewModel
{
    Cover = null, Avatar = null, AuthorName = "路过的盒友", MetaText = "小黑盒文章", Topics = [],
    Title = "一段没有封面也没有图的短文", Stats = HeyboxCardViewModel.CreateStats("120", "4", "", ""),
    Blocks = [HeyboxArticleBlockViewModel.Paragraph("就一句话。")],
};

var renderer = typeof(BiliArticleDocumentViewModel).Assembly.GetType("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", true)!;
MediaCardViewModel Invoke(string method, params object?[] a) =>
    (MediaCardViewModel)renderer.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, a)!;
ParsedMedia Media(string provider, string title, string? author, string? desc, string[] tags, Dictionary<string, string> attrs, ParsedMediaKind kind = ParsedMediaKind.Video) => new()
{
    ProviderId = provider, ProviderName = "Bilibili", MediaId = "BV1xx", SourceUrl = "https://www.bilibili.com", Title = title,
    AuthorName = author, Description = desc, Tags = tags.ToList(), Kind = kind, Attributes = attrs,
};

var biliVideo = Invoke("CreateVideoViewModel", Media("bilibili", "【4K】用 Avalonia 渲染一张会呼吸的卡片", "某某UP主", "毛玻璃、渐进模糊、平台配色，一次讲清楚。", ["科技", "教程"],
    new() { ["duration_seconds"] = "754", ["views"] = "1480000", ["likes"] = "26000", ["coins"] = "7305", ["favorites"] = "9142",
        ["publish_timestamp"] = DateTimeOffset.Now.AddDays(-2).ToUnixTimeSeconds().ToString(), ["category"] = "科技", ["author_location"] = "云南" }),
    landscape, Avatar(32), null);
var bangumi = Invoke("CreateBangumiViewModel", Media("bilibili-bangumi", "葬送的芙莉莲", "哔哩哔哩番剧",
    "勇者一行人打倒了魔王，迎来了和平。作为勇者队伍一员的精灵魔法使芙莉莲，在与同伴们告别后，开始了一段了解人类的旅程。", ["奇幻", "冒险"],
    new() { ["pages"] = "28", ["requested_page"] = "3", ["publish"] = "已完结, 全28话", ["play_count"] = "523000000",
        ["follow_count"] = "11200000", ["rating_score"] = "9.9", ["rating_count"] = "1830000", ["episode_title"] = "第3话 杀人魔法" }),
    landscape, MediaCardViewModel.BilibiliBangumiAvatar);
var live = Invoke("CreateLiveViewModel", Media("bilibili-live", "【歌回】深夜杂谈 + 点歌，来聊聊天吧～", "这是一个名字特别特别长的主播用来测试截断效果", null, ["唱见", "杂谈"],
    new() { ["live_status"] = "1", ["room_id"] = "21452505", ["parent_area"] = "虚拟主播", ["area"] = "虚拟日常",
        ["live_start"] = DateTimeOffset.Now.AddHours(-2.3).ToString("O"), ["duration"] = TimeSpan.FromMinutes(138.4).ToString(),
        ["watched_count"] = "48211", ["audience_count"] = "3120" }), vertical, Avatar(32), null);
var douyin = new MediaCardViewModel
{
    Platform = MediaCardPlatform.Douyin, Cover = vertical, Avatar = Avatar(32), BadgeText = "0:42", BadgeIcon = MediaCardIcons.Play,
    Title = "敲虾真的太好吃了！一口下去全是弹弹的虾肉", Meta = "2026-11-26 发布", Description = "", Tags = ["#美食", "#家常菜"],
    Metrics = [new(MediaCardIcons.Heart, "点赞", "12.6万"), new(MediaCardIcons.Comment, "评论", "3,412"), new(MediaCardIcons.Star, "收藏", "8,840"), new(MediaCardIcons.Share, "分享", "1,204")],
    AuthorName = "小厨娘", AuthorMeta = "IP属地：浙江",
};
var biliArticle = new BiliArticleDocumentViewModel
{
    Cover = landscape, Avatar = Avatar(40), AuthorName = "技术宅小明", AuthorMeta = "12.3万粉丝", MetaText = "2026-11-27 发布 · 3,560 字",
    KindText = "专栏", Title = "从零开始的 Avalonia 卡片渲染：毛玻璃、渐进模糊与平台配色", Tags = ["#科技", "#教程"],
    Blocks =
    [
        BiliArticleBlockViewModel.Paragraph("这篇专栏整理了做解析卡片时踩过的坑。卡片的底色取自封面模糊，再叠一层平台双色调。"),
        BiliArticleBlockViewModel.Heading("一、渐进模糊", 2),
        BiliArticleBlockViewModel.Paragraph("清晰层和模糊层叠在一起，模糊层只在下半部分逐渐出现。"),
        BiliArticleBlockViewModel.Quote("提示：BoxShadow 和 BackdropEffect 叠用在 2x 渲染下会错位。"),
    ],
};

var dynamic = new BiliDynamicCardViewModel
{
    Avatar = Avatar(40), AuthorName = "某个UP主", AuthorMeta = "2026-11-26 14:28 · 示例认证账号",
    Text = "【作品推荐】今天整理了一组新画的插图，配色参考了冬天傍晚的天空。最后一张是线稿，欢迎在评论区说说你最喜欢哪一张～",
    Images = [Tile(landscape), Tile(vertical), Tile(albumArt), Tile(landscape)], ImageCount = 4,
    Metrics = [new(MediaCardIcons.Share, "转发", "88"), new(MediaCardIcons.Comment, "评论", "58"), new(MediaCardIcons.ThumbUp, "点赞", "3,144")],
};
var forwardLong = new ForwardLongImageViewModel
{
    Platform = MediaCardPlatform.Douyin, Title = "抖音图文：敲虾真的太好吃了", Subtitle = "共 2 张图片 · 另有 1 个视频",
    Items = [new ForwardLongImageItem { Text = "土豆淀粉 + 轻轻敲，弹到飞起" }, new ForwardLongImageItem { Image = vertical }, new ForwardLongImageItem { Image = landscape }],
};
var columns = new Func<Control>[][]
{
    [() => new MediaCard { DataContext = biliVideo }, () => new MediaCard { DataContext = live }],
    [() => new MediaCard { DataContext = bangumi }, () => new MediaCard { DataContext = douyin }],
    [() => new MediaCard { DataContext = wxLand }, () => new HeyboxCard { DataContext = hbFull }],
    [() => new CommentCard { DataContext = biliComments }, () => new NetEaseMusicCard { DataContext = music }],
    [() => new CommentCard { DataContext = douyinComments }, () => new BiliDynamicCard { DataContext = dynamic }],
    [() => new BiliArticleDocument { DataContext = biliArticle }],
    [() => new HeyboxArticleDocument { DataContext = hbArticle }],
    [() => new ForwardLongImage { DataContext = forwardLong }],
};
foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
{
    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(24) };
    foreach (var column in columns)
    {
        var stack = new StackPanel { Spacing = 24, VerticalAlignment = VerticalAlignment.Top };
        foreach (var f in column) stack.Children.Add(f());
        row.Children.Add(new ThemeVariantScope { RequestedThemeVariant = theme, Child = stack, VerticalAlignment = VerticalAlignment.Top });
    }
    var root = new Border { Background = new SolidColorBrush(Color.Parse(theme == ThemeVariant.Light ? "#D9DCE1" : "#15171C")), Child = row };
    var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight, FontFamily = fontFamily };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    root.Measure(Size.Infinity);
    root.Arrange(new Rect(root.DesiredSize));
    var size = root.Bounds.Size;
    using var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * 1.5), (int)Math.Ceiling(size.Height * 1.5)), new Vector(144, 144));
    rtb.Render(root);
    var file = Path.Combine(output, $"all-{(theme == ThemeVariant.Light ? "light" : "dark")}.png");
    rtb.Save(file);
    Console.WriteLine($"{file} {size}");
    window.Close();
}

Bitmap Tile(Bitmap source) => Resize(source, BiliDynamicCardViewModel.TileSizeFor(4), BiliDynamicCardViewModel.TileSizeFor(4));
// 演示横图的人物在偏右的位置，竖图往右裁才能框住人物。
Bitmap Portrait(Bitmap source) => Resize(source, 600, 900, AlignmentX.Right);

// 用 UniformToFill 把图片画进目标尺寸并裁切。
static Bitmap Resize(Bitmap source, double width, double height, AlignmentX alignment = AlignmentX.Center)
{
    var border = new Border
    {
        Width = width, Height = height,
        Background = new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentX = alignment },
    };
    border.Measure(new Size(width, height));
    border.Arrange(new Rect(0, 0, width, height));
    var bitmap = new RenderTargetBitmap(new PixelSize((int)width * 2, (int)height * 2), new Vector(192, 192));
    bitmap.Render(border);
    return bitmap;
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "MyParser.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("找不到仓库根目录（MyParser.slnx）。");
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
