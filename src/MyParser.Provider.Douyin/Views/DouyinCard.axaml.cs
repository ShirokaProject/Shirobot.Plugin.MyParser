using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.Douyin.Views;

public partial class DouyinCard : UserControl
{
    public DouyinCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
