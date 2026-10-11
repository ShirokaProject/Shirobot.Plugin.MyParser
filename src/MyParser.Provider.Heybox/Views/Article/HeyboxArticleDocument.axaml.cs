using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.Heybox.Views;

public partial class HeyboxArticleDocument : UserControl
{
    public HeyboxArticleDocument()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
