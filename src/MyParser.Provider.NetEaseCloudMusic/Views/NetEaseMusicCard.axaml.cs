using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyParser.Provider.NetEaseCloudMusic.Views;

public partial class NetEaseMusicCard : UserControl
{
    public NetEaseMusicCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
