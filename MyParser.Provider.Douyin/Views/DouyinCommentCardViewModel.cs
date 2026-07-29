using Avalonia.Media.Imaging;

namespace MyParser.Provider.Douyin.Views;

public sealed class DouyinCommentCardViewModel
{
    public int CanvasHeight { get; init; } = 1200;
    public Bitmap? Cover { get; init; }
    public string Title { get; init; } = "抖音热门评论";
    public string MetaText { get; init; } = string.Empty;
    public string StatsText { get; init; } = string.Empty;
    public IReadOnlyList<DouyinCommentItemViewModel> Comments { get; init; } = [];
}

public sealed class DouyinCommentItemViewModel
{
    public Bitmap? Avatar { get; init; }
    public Bitmap? CommentImage { get; init; }
    public bool HasImage { get; init; }
    public string UserName { get; init; } = "未知用户";
    public string UserIdText { get; init; } = "抖音号 --";
    public string IpText { get; init; } = "IP 未知";
    public string Message { get; init; } = string.Empty;
    public string LikeText { get; init; } = "0";
    public string ReplyText { get; init; } = "0";
    public string TimeText { get; init; } = "时间未知";
    public string IndexText { get; init; } = "01";
    public bool IsAuthor { get; init; }
}
