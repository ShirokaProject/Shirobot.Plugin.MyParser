using Avalonia;
using Avalonia.Media.Imaging;

namespace MyParser.Provider.Heybox.Views;

public sealed class HeyboxArticleDocumentViewModel
{
    public int CanvasHeight { get; init; } = 1200;
    public Bitmap? Avatar { get; init; }
    public Bitmap? Cover { get; init; }
    public string KindText { get; init; } = "小黑盒文章";
    public string Title { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public string MetaText { get; init; } = string.Empty;
    public string StatsText { get; init; } = string.Empty;
    public IReadOnlyList<HeyboxArticleDocumentBlockViewModel> Blocks { get; init; } = [];
}

public sealed class HeyboxArticleDocumentBlockViewModel
{
    public string Text { get; init; } = string.Empty;
    public Bitmap? Image { get; init; }
    public string Caption { get; init; } = string.Empty;
    public int Height { get; init; } = 120;
    public bool IsImage { get; init; }
    public bool IsText => !IsImage;
    public int HeadingLevel { get; init; }
    public int Indent { get; init; }
    public Thickness Margin { get; init; } = new(0);
    public int FontSize { get; init; } = 15;
    public int LineHeight { get; init; } = 24;
    public string Foreground { get; init; } = "#F4F7FB";
    public string Background { get; init; } = "Transparent";
    public string BorderBrush { get; init; } = "Transparent";
    public string FontWeight { get; init; } = "Normal";
    public string AccentBrush { get; init; } = "Transparent";
    public int AccentWidth { get; init; }
    public bool HasAccent => AccentWidth > 0;
}
