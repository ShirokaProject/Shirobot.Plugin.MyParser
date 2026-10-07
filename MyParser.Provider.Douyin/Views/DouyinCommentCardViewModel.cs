using Avalonia.Media.Imaging;

namespace MyParser.Provider.Douyin.Views;

public sealed class DouyinCommentCardViewModel
{
    public int CanvasHeight { get; init; } = 600;
    public string SourceTitle { get; init; } = string.Empty;
    public string CommentCountText { get; init; } = string.Empty;
    public IReadOnlyList<DouyinCommentItemViewModel> Comments { get; init; } = [];
}

public sealed class DouyinCommentItemViewModel
{
    public Bitmap? Avatar { get; init; }
    public Bitmap? CommentImage { get; init; }
    public bool HasImage { get; init; }
    public string UserName { get; init; } = "未知用户";
    public string TimeLocationText { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string LikeText { get; init; } = "0";
    public bool HasReplies { get; init; }
    public string ReplyCountText { get; init; } = string.Empty;
    public int EstimatedHeight { get; init; } = 100;
    public bool IsAuthor { get; init; }
}
