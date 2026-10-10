using Avalonia.Media.Imaging;

namespace MyParser.Provider.X.Views;

public sealed class XCardViewModel
{
    public XCardViewModel()
    {
        // 设计时数据
        DisplayName = "卡洛琳";
        ScreenName = "@caroline_example";
        Verified = true;
        PublishTime = "2026-10-08 17:30";
        KindText = "视频";
        Description = "这是一条示例推文正文这是示例推文正文这是示例推文正文这是示例推文正文这是示例推文正文。";
        ReplyCount = "1,097";
        RetweetCount = "4,532";
        LikeCount = "25.1万";
        ViewCount = "91.6万";
        SourceUrl = "x.com/caroline_example/status/2108127977871733092";
        MediaPage = "1/1";
        HasAvatar = false;
        HasCover = false;
        ShowAvatarPlaceholder = true;
        ShowCoverPlaceholder = true;
    }

    public Bitmap? Cover { get; init; }
    public Bitmap? Avatar { get; init; }
    public bool HasCover { get; init; }
    public bool ShowCoverPlaceholder { get; init; }
    public bool HasAvatar { get; init; }
    public bool ShowAvatarPlaceholder { get; init; }
    public bool Verified { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string ScreenName { get; init; } = string.Empty;
    public string PublishTime { get; init; } = string.Empty;
    public string KindText { get; init; } = string.Empty;
    public string MediaPage { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ReplyCount { get; init; } = string.Empty;
    public string RetweetCount { get; init; } = string.Empty;
    public string LikeCount { get; init; } = string.Empty;
    public string ViewCount { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string KindBadge => string.IsNullOrWhiteSpace(MediaPage) ? KindText : $"{KindText} {MediaPage}";
}
