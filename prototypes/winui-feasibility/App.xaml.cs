using Microsoft.UI.Xaml;

namespace SystemExplorer.WinUI.Feasibility;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        UnhandledException += (_, args) =>
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "startup-error.log"), args.Message + Environment.NewLine + args.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
