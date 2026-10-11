using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.BiliBili.Views;

public partial class BiliCard : UserControl
{
    public BiliCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
