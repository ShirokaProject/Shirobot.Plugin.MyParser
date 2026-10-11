using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.BiliBili.Views;

public partial class BiliBangumiCard : UserControl
{
    public BiliBangumiCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
