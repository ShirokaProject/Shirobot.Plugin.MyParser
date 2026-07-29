using Avalonia.Media.Imaging;

namespace MyParser.Provider.BiliBili.Views;

public sealed class BiliCommentCardViewModel
{
    public int CanvasHeight { get; init; } = 1200;
    public Bitmap? Cover { get; init; }
    public Bitmap? AuthorAvatar { get; init; }
    public string Title { get; init; } = "Bilibili 评论区";
    public string AuthorName { get; init; } = "未知 UP";
    public string MetaText { get; init; } = string.Empty;
    public string StatsText { get; init; } = string.Empty;
    public IReadOnlyList<BiliCommentItemViewModel> Comments { get; init; } = [];
}

public sealed class BiliCommentItemViewModel
{
    public Bitmap? Avatar { get; init; }
    public Bitmap? Image { get; init; }
    public bool HasImage => Image is not null;
    public string UserName { get; init; } = "未知用户";
    public string UserIdText { get; init; } = "UID --";
    public string Message { get; init; } = string.Empty;
    public string MetaText { get; init; } = string.Empty;
    public string StatsText { get; init; } = string.Empty;
    public string IndexText { get; init; } = "01";
    public bool IsAuthor { get; init; }
}
