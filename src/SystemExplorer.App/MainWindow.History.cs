using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using SystemExplorer.App.Views;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly SelectedProcessHistory selectedHistory = new();

    private async void HistoryClicked(object sender, RoutedEventArgs args)
    {
        if (detailsOpen) return;
        detailsOpen = true;
        var histories = selectedHistory.Snapshots;
        ProcessIdentity? identity = histories.FirstOrDefault()?.Identity;
        var targets = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(targets, "Retained process lifetime");
        var toggle = new Button { Content = "Show table" };
        var chart = new ProcessHistoryChart();
        var table = new InventoryView { Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var content = new Grid { RowSpacing = 5 };
        void ResizeContent()
        {
            content.Width = Math.Min(800, Math.Max(0, Root.ActualWidth - 100));
            content.Height = Math.Min(470, Math.Max(0, Root.ActualHeight - 190));
        }
        void RootSizeChanged(object sender, SizeChangedEventArgs e) => ResizeContent();
        void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs e) => ResizeContent();
        ResizeContent();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(status);
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(targets);
        Grid.SetColumn(toggle, 1); controls.Children.Add(toggle);
        Grid.SetRow(controls, 1); content.Children.Add(controls);
        Grid.SetRow(chart, 2); content.Children.Add(chart);
        Grid.SetRow(table, 2); content.Children.Add(table);
        bool updating = false;
        bool rendered = false;
        DateTimeOffset? renderedAt = null;
        ProcessIdentity? tableIdentity = null;
        void Render()
        {
            var history = histories.FirstOrDefault(h => h.Identity == identity);
            status.Text = ProcessHistoryStatus.Build(history);
            if (chart.Visibility == Visibility.Visible) chart.Update(history);
            else if (history is null)
            {
                table.ShowMessage("Select a stable current process to start retaining its history.");
                tableIdentity = null;
            }
            else
            {
                table.ShowRows($"PID {history.Identity.Pid} · creation {history.Identity.CreationTimeFileTime}. Session-only history. Select a sample for CPU and I/O details; missing does not establish exit.",
                    history.Points.Select(p => new InventoryRow(p.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        $"{(p.Observed ? "Observed" : "Not observed")}{(p.BreakBefore ? " · Sampling gap before this row" : "")}",
                        DiagnosticData.Bytes(p.PrivateBytes), DiagnosticData.Bytes(p.WorkingSetBytes), DiagnosticData.Bytes(p.PrivateWorkingSetBytes),
                        p.PrivateBytes, p.WorkingSetBytes, p.PrivateWorkingSetBytes,
                        $"{p.At:O}\nPID {history.Identity.Pid} · creation {history.Identity.CreationTimeFileTime}\n{(p.Observed ? "Observed" : "Not observed; this does not establish exit")} · Gap before: {p.BreakBefore}\nPrivate commit: {DiagnosticData.Bytes(p.PrivateBytes)}\nWorking set: {DiagnosticData.Bytes(p.WorkingSetBytes)}\nPrivate working set: {DiagnosticData.Bytes(p.PrivateWorkingSetBytes)}\nCPU (machine normalized): {(p.CpuPercent is { } cpu ? $"{cpu:F2}%" : "Unavailable")}\nRead: {(p.ReadBytesPerSecond is { } read ? Display.Rate(read) : "Unavailable")}\nWrite: {(p.WriteBytesPerSecond is { } write ? Display.Rate(write) : "Unavailable")}\nPrivate commit is not physical RAM; shared resident pages are not additive.",
                        NameKey: p.At.UtcTicks, Key: $"{history.Identity}:{p.At.UtcTicks}")),
                    "Sample time", "Private commit", "Working set", "Private WS", reverse: true,
                    preserveState: tableIdentity == history.Identity, live: true);
                tableIdentity = history.Identity;
            }
            renderedAt = history?.Points.LastOrDefault()?.At;
            rendered = true;
        }
        void Refresh()
        {
            var next = selectedHistory.Snapshots;
            bool changed = !histories.Select(h => h.Identity).SequenceEqual(next.Select(h => h.Identity));
            histories = next;
            if (changed || targets.ItemsSource is null)
            {
                updating = true;
                if (!histories.Any(h => h.Identity == identity)) identity = histories.FirstOrDefault()?.Identity;
                targets.ItemsSource = histories.Select(h => $"{h.Name} · PID {h.Identity.Pid} · creation {h.Identity.CreationTimeFileTime}").ToArray();
                targets.SelectedIndex = histories.ToList().FindIndex(h => h.Identity == identity);
                updating = false;
            }
            if (!rendered || changed || renderedAt != histories.FirstOrDefault(h => h.Identity == identity)?.Points.LastOrDefault()?.At) Render();
        }
        targets.SelectionChanged += (_, _) =>
        {
            if (updating || targets.SelectedIndex < 0) return;
            identity = histories[targets.SelectedIndex].Identity;
            Render();
        };
        toggle.Click += (_, _) =>
        {
            bool showChart = chart.Visibility != Visibility.Visible;
            chart.Visibility = showChart ? Visibility.Visible : Visibility.Collapsed;
            table.Visibility = showChart ? Visibility.Collapsed : Visibility.Visible;
            toggle.Content = showChart ? "Show table" : "Show chart";
            Render();
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Selected-process history",
            Content = content, CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 940d;
        var xamlRoot = Root.XamlRoot;
        Root.SizeChanged += RootSizeChanged;
        xamlRoot.Changed += XamlRootChanged;
        try { Refresh(); timer.Start(); await dialog.ShowAsync(); }
        finally
        {
            timer.Stop();
            Root.SizeChanged -= RootSizeChanged;
            xamlRoot.Changed -= XamlRootChanged;
            detailsOpen = false;
        }
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
