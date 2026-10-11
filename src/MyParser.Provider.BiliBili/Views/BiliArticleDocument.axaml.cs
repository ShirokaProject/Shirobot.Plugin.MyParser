using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.BiliBili.Views;

public partial class BiliArticleDocument : UserControl
{
    public BiliArticleDocument()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
