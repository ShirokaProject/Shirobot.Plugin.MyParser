using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.BiliBili.Views;

public partial class BiliLiveCard : UserControl
{
    public BiliLiveCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
