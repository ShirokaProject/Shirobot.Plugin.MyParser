using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.X.Views;

public partial class XCard : UserControl
{
    public XCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
