using Avalonia.Media.Imaging;

namespace MyParser.Provider.BiliBili.Views;

public sealed class BiliLiveCardViewModel
{
    public Bitmap? Cover { get; init; }
    public Bitmap? Avatar { get; init; }
    public string KindText { get; init; } = "Bilibili 直播";
    public string StatusText { get; init; } = "直播中";
    public string RoomIdText { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string AnchorName { get; init; } = string.Empty;
    public string AudienceText { get; init; } = string.Empty;
    public string WatchedText { get; init; } = string.Empty;
    public string LiveStartTimeText { get; init; } = string.Empty;
    public string LiveDurationText { get; init; } = string.Empty;
}
