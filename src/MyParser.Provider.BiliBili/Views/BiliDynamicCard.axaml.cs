using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.BiliBili.Views;

public partial class BiliDynamicCard : UserControl
{
    public BiliDynamicCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
