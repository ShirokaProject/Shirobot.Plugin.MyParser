using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Shirobot.Plugin.MyParser.CardRendering;

public partial class MediaCard : UserControl
{
    public MediaCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
