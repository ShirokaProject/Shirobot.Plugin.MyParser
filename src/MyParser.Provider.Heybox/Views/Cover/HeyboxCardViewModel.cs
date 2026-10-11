using Avalonia.Media.Imaging;

namespace MyParser.Provider.Heybox.Views;

public sealed class HeyboxCardViewModel
{
    public Bitmap? Cover { get; init; }
    public string Title { get; init; } = "小黑盒帖子";
    public string Description { get; init; } = string.Empty;
    public string AuthorName { get; init; } = "小黑盒用户";
    public string StatsText { get; init; } = string.Empty;
    public string MediaText { get; init; } = string.Empty;
    public string LinkId { get; init; } = string.Empty;
    public string SourceText { get; init; } = "小黑盒";
}
