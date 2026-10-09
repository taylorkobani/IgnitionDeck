using IgnitionDeck.UI;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace IgnitionDeck;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "IgnitionDeck";
        AppWindow.Resize(new SizeInt32(1200, 800));
        var layout = new MainLayout(this);
        Root.Children.Add(layout);
        Closed += (_, _) => layout.Stop();
    }
}
