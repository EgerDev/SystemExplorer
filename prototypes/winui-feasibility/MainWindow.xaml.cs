using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace SystemExplorer.WinUI.Feasibility;

public sealed partial class MainWindow : Window
{
    private int interactionCount;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1180, 740));
    }

    private void VerifyInteraction(object sender, RoutedEventArgs e)
    {
        interactionCount++;
        InteractionResult.Text = $"Native WinUI event received ({interactionCount}). No monitoring data collected.";
    }
}
