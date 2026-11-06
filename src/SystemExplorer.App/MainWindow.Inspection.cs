using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;
using SystemExplorer.Windows;
using SystemExplorer.App.Views;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly IProcessInspectionProvider inspection = new WindowsProcessInspectionProvider();

    private async void InspectionClicked(object sender, RoutedEventArgs args)
    {
        if (detailsOpen || selectedIdentity is not { IsStable: true } identity ||
            Model.Snapshot?.Processes.Any(p => p.Identity == identity) != true || sender is not Button { Tag: string tag }) return;
        var kind = tag == "Modules" ? ProcessInspectionKind.Modules : ProcessInspectionKind.MemoryRegions;
        detailsOpen = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var view = new ProcessInspectionView
        {
            Width = Math.Min(760, Math.Max(320, Root.ActualWidth - 100)),
            Height = Math.Min(440, Math.Max(200, Root.ActualHeight - 200))
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
            Title = $"{(kind == ProcessInspectionKind.Modules ? "Modules" : "Memory regions")} · PID {identity.Pid}",
            Content = view, CloseButtonText = "Close / cancel", DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900d;
        dialog.Closing += (_, _) => cancellation.Cancel();
        var load = LoadInspectionAsync(view, identity, kind, cancellation.Token);
        try { await dialog.ShowAsync(); }
        finally { cancellation.Cancel(); await load; detailsOpen = false; }
    }

    private async Task LoadInspectionAsync(ProcessInspectionView view, ProcessIdentity identity, ProcessInspectionKind kind, CancellationToken token)
    {
        try
        {
            var result = await inspection.CaptureAsync(identity, kind, token);
            token.ThrowIfCancellationRequested();
            view.ShowResult(result);
        }
        catch (OperationCanceledException) { view.ShowError("Inspection cancelled; no inventory retained."); }
        catch (Exception ex) { view.ShowError($"Inspection unavailable: {ex.Message}"); }
    }
}
