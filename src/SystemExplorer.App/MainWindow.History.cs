using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.App.Views;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly SelectedProcessHistory selectedHistory = new();

    private async void HistoryClicked(object sender, RoutedEventArgs args)
    {
        var histories = selectedHistory.Snapshots;
        await ShowInventoryAsync("Recent selected-process history", (view, _) =>
        {
            view.SetTargets(histories.Select(h => $"{h.Name} · PID {h.Identity.Pid} · creation {h.Identity.CreationTimeFileTime}").ToArray(), index =>
            {
                var history = histories[index];
                view.ShowRows($"PID {history.Identity.Pid} · creation {history.Identity.CreationTimeFileTime}. Frozen history since selection; session-only, at most 8 lifetimes / 180 samples each. Missing means not observed, not confirmed exited. Gaps do not imply zero.",
                    history.Points.Select(p => new InventoryRow(p.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        $"{(p.Observed ? "Observed" : "Not observed")}{(p.BreakBefore ? " · Sampling gap before this row" : "")}",
                        DiagnosticData.Bytes(p.PrivateBytes), DiagnosticData.Bytes(p.WorkingSetBytes), DiagnosticData.Bytes(p.PrivateWorkingSetBytes),
                        p.PrivateBytes, p.WorkingSetBytes, p.PrivateWorkingSetBytes,
                        $"{p.At:O}\nPID {history.Identity.Pid} · creation {history.Identity.CreationTimeFileTime}\n{(p.Observed ? "Observed" : "Not observed; this does not establish exit")} · Gap before: {p.BreakBefore}\nPrivate commit: {DiagnosticData.Bytes(p.PrivateBytes)}\nWorking set: {DiagnosticData.Bytes(p.WorkingSetBytes)}\nPrivate working set: {DiagnosticData.Bytes(p.PrivateWorkingSetBytes)}\nCPU (machine normalized): {(p.CpuPercent is { } cpu ? $"{cpu:F2}%" : "Unavailable")}\nPrivate commit is not physical RAM; shared resident pages are not additive.")),
                    "Sample time", "Private commit", "Working set", "Private WS");
            });
            if (histories.Count == 0) view.ShowMessage("Select a stable current process to start retaining its history.");
            return Task.CompletedTask;
        });
    }

    private async Task ShowInventoryAsync(string title, Func<InventoryView, CancellationToken, Task> populate)
    {
        if (detailsOpen) return;
        detailsOpen = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var view = new InventoryView
        {
            Width = Math.Min(800, Math.Max(320, Root.ActualWidth - 100)),
            Height = Math.Min(460, Math.Max(200, Root.ActualHeight - 200))
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = title,
            Content = view, CloseButtonText = "Close / cancel", DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 940d;
        dialog.Closing += (_, _) => cancellation.Cancel();
        async Task Load()
        {
            try { await populate(view, cancellation.Token); }
            catch (OperationCanceledException) { view.ShowMessage("Cancelled; no new observation retained."); }
            catch (Exception ex) { view.ShowMessage($"Unavailable: {ex.Message}"); }
        }
        var load = Load();
        try { await dialog.ShowAsync(); }
        finally
        {
            cancellation.Cancel();
            try { await load; } finally { detailsOpen = false; }
        }
    }
}
