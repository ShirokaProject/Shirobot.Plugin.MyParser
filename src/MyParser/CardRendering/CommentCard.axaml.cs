using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Shirobot.Plugin.MyParser.CardRendering;

public partial class CommentCard : UserControl
{
    public CommentCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
