using System.IO;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.App.Views;
using SystemExplorer.Core;
using SystemExplorer.Windows;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly IRamBreakdownProvider ramBreakdown = new WindowsRamBreakdownProvider();
    private readonly IProcessResidentProvider ramResidents = new WindowsProcessResidentProvider();
    private readonly IPoolTagDriverProvider ramDriverLeads = new WindowsPoolTagDriverProvider();
    private readonly IPhysicalMemoryProvider ramPhysical = new WindowsPhysicalMemoryProvider();

    private async void ShowRamAttribution()
    {
        if (detailsOpen || Model.Snapshot is not { } snapshot) return;
        detailsOpen = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var view = new RamAttributionView(snapshot, ramBreakdown, ramResidents, poolTags, ramDriverLeads, ramPhysical,
            administrator, ExportRamAsync, cancellation.Token);
        void Resize()
        {
            view.Width = Math.Min(1100, Math.Max(0, Root.ActualWidth - 100));
            view.Height = Math.Min(760, Math.Max(0, Root.ActualHeight - 170));
        }
        void Resized(object sender, SizeChangedEventArgs args) => Resize();
        void ScaleChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
        Resize();
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "What's eating RAM",
            Content = view, CloseButtonText = "Close / cancel", DefaultButton = ContentDialogButton.Close };
        dialog.Resources["ContentDialogMaxWidth"] = 1200d;
        dialog.Closing += (_, _) => cancellation.Cancel();
        Root.SizeChanged += Resized; Root.XamlRoot.Changed += ScaleChanged;
        var load = view.InitializeAsync();
        try { await dialog.ShowAsync(); }
        finally
        {
            cancellation.Cancel();
            Root.SizeChanged -= Resized; Root.XamlRoot.Changed -= ScaleChanged;
            try { await load; await view.StopAsync(); } finally { detailsOpen = false; }
        }
    }

    private async Task<string?> ExportRamAsync(RamExportDocument document, string format, CancellationToken token)
    {
        var path = NativeFileDialogs.ChooseSave(WinRT.Interop.WindowNative.GetWindowHandle(this), format,
            "Export RAM investigation", "SystemExplorer-RAM-" + document.Tab.Replace(' ', '-'));
        if (path is null) return null;
        var text = format == "csv" ? RamAttributionPresentation.Csv(document) : RamAttributionPresentation.Json(document);
        await File.WriteAllTextAsync(path, text, token);
        return path;
    }
}
