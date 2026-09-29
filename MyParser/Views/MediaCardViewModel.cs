using Avalonia.Media.Imaging;

namespace Shirobot.Plugin.MyParser.Views;

internal sealed record MediaCardViewModel
{
    public Bitmap? Cover { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string KindName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Metadata { get; init; } = string.Empty;
}
