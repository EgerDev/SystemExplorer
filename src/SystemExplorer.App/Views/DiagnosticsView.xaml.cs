using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticRows Data { get; } = new();
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action<string>? ServiceRequested;
    public event Action<string>? InventoryRequested;
    private IReadOnlyList<DiagnosticItem> items = [];
    private IReadOnlyList<HistoryPoint> history = [];
    private MonitorSnapshot? snapshot;
    private string section = "";
    private string query = "";
    private int sortColumn;
    private bool descending;
    private ScrollViewer? scroll;
    private bool ready;
    private bool detailsOpen;
    private bool compact;
    private bool historyExpanded = true;

    public DiagnosticsView() { InitializeComponent(); ready = true; }

    public void Update(string nextSection, MonitorSnapshot next, RelationshipSnapshot? relationships,
        IReadOnlyList<StartupItem> startup, DiagnosticsHistory diagnostics, string search,
        Func<ProcessSnapshot, string?> descriptions, string startupStatus, bool paused, bool stale)
    {
        var reset = section != nextSection || query != search;
        if (section != nextSection)
        {
            sortColumn = nextSection is "Memory" or "CPU" or "Disks" ? 2 : 0;
            descending = sortColumn == 2;
        }
        section = nextSection;
        query = search;
        snapshot = next;
        history = diagnostics.System;
        items = DiagnosticData.Create(section, next, relationships, startup, diagnostics, descriptions);
        var headers = DiagnosticData.Headers(section);
        NameHeader.Content = headers[0]; FirstHeader.Content = headers[1]; SecondHeader.Content = headers[2]; ThirdHeader.Content = headers[3];
        MemorySummary.Visibility = section == "Memory" ? Visibility.Visible : Visibility.Collapsed;
        PagefilesButton.Visibility = RecentButton.Visibility = section == "Memory" ? Visibility.Visible : Visibility.Collapsed;
        TimelineButton.Visibility = section == "CPU" ? Visibility.Visible : Visibility.Collapsed;
        TasksButton.Visibility = section == "Startup" ? Visibility.Visible : Visibility.Collapsed;
        ChartCard.Visibility = section is "Memory" or "CPU" ? Visibility.Visible : Visibility.Collapsed;
        ChartSeries.Visibility = section == "Memory" ? Visibility.Visible : Visibility.Collapsed;
        var table = DiagnosticData.IsTable(section);
        TablePanel.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        Notes.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        var mem = next.Memory;
        PhysicalCard.Value = DiagnosticData.Bytes(mem.PhysicalUsedBytes);
        AvailableCard.Value = DiagnosticData.Bytes(mem.PhysicalAvailableBytes);
        CommitCard.Value = $"{DiagnosticData.Bytes(mem.CommitTotalBytes)} / {DiagnosticData.Bytes(mem.CommitLimitBytes)}";
        ContextText.Text = section switch
        {
            "Memory" => $"Physical total {DiagnosticData.Bytes(mem.PhysicalTotalBytes)} · Cache {DiagnosticData.Bytes(mem.SystemCacheBytes)} · Paged pool {DiagnosticData.Bytes(mem.KernelPagedBytes)} · Nonpaged pool {DiagnosticData.Bytes(mem.KernelNonpagedBytes)}\nPrivate commit is not RAM. Working sets include shared pages; do not add these categories. Private WS = resident private pages; — = unavailable.",
            "CPU" => $"System CPU {DiagnosticData.Percent(next.TotalCpuPercent)} · Process CPU is normalized across all logical processors; first samples are unavailable.",
            "Disks" => $"Physical disk read {DiagnosticData.Rate(next.DiskReadBytesPerSecond)} · write {DiagnosticData.Rate(next.DiskWriteBytesPerSecond)}\nThe table is generic process I/O, not physical disk or network attribution. Unknown values are not zero.",
            "Network" => $"TCP/UDP observation: {relationships?.CapturedAt.LocalDateTime.ToString("T") ?? "Unavailable"}. UDP has no remote peer. Endpoints are not bandwidth; Inspect requires a current verified lifetime.",
            "Services" => $"Win32 service observation: {relationships?.CapturedAt.LocalDateTime.ToString("T") ?? "Unavailable"}. Click a row for configuration and dependencies. Driver services are not listed; Inspect requires a current verified process lifetime.",
            "Startup" => $"Run/RunOnce keys and startup-folder entries, not a complete autostart inventory. Targets are not resolved or executed. {startupStatus}",
            _ => "Observations are not causal diagnoses. Unavailable measurements are not zero."
        };
        var limits = next.Limitations.Concat(relationships?.Limitations ?? []).ToList();
        if (section == "Memory") limits.Insert(0, $"System commit peak: {DiagnosticData.Bytes(mem.CommitPeakBytes)} · Standby: {DiagnosticData.Bytes(mem.StandbyBytes)}. Process working-set totals cannot explain distinct physical RAM usage.");
        limits.Insert(0, $"System counts at the performance-information sample: {mem.ProcessCount?.ToString() ?? "—"} processes · {mem.ThreadCount?.ToString() ?? "—"} threads · {mem.HandleCount?.ToString() ?? "—"} handles. Enumeration is a separate observation.");
        LimitsText.Text = string.Join("\n", limits);
        ApplyRows(reset);
        if (!table && Data.Rows.Count == 0) ContextText.Text += " No observations match this search.";
        TableStatus.Text += $" · {(paused ? "Paused" : stale ? "Stale" : "Observed")} {next.CapturedAt.LocalDateTime:T}";
        DrawHistory();
    }

    private void ApplyRows(bool reorder)
    {
        var numeric = sortColumn == 1 || sortColumn > 1 && section is "Memory" or "CPU" or "Disks";
        var desired = DiagnosticData.FilterAndSort(items, query, sortColumn, descending, numeric);
        scroll ??= FindScroll(Table);
        var offset = scroll?.VerticalOffset ?? 0;
        var anchorIndex = Math.Min((int)(offset / 56), Data.Rows.Count - 1);
        var anchor = anchorIndex >= 0 ? Data.Rows[anchorIndex].Key : null;
        Data.Apply(desired, reorder);
        if (reorder && Data.Rows.Count > 0)
        {
            Table.ScrollIntoView(Data.Rows[0], ScrollIntoViewAlignment.Leading);
            Table.UpdateLayout();
        }
        if (scroll is not null)
        {
            if (reorder) scroll.ChangeView(null, 0, null, true);
            else if (anchor is not null)
            {
                var index = Data.Rows.ToList().FindIndex(row => row.Key == anchor);
                if (index >= 0 && index != anchorIndex)
                {
                    Table.UpdateLayout();
                    scroll.ChangeView(null, index * 56 + offset % 56, null, true);
                }
            }
        }
        EmptyText.Text = query.Length == 0 ? "No observations available for this section." : "No observations match. Clear search to show all rows.";
        EmptyText.Visibility = Data.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TableStatus.Text = $"{Data.Rows.Count} of {items.Count} observations · Order held; click a column to re-rank · Click a row for full text · {(descending ? "Descending" : "Ascending")}";
    }

    private void SortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var column)) return;
        descending = sortColumn == column ? !descending : column > 0;
        sortColumn = column;
        ApplyRows(true);
    }
    private void InspectClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: DiagnosticRow { Item.Identity: { IsStable: true } identity } }) InspectRequested?.Invoke(identity);
    }
    private async void RowClicked(object sender, ItemClickEventArgs args)
    {
        if (detailsOpen || args.ClickedItem is not DiagnosticRow row) return;
        if (section == "Services") { ServiceRequested?.Invoke(row.Key); return; }
        detailsOpen = true;
        try
        {
            var headers = DiagnosticData.Headers(section);
            var text = $"{row.Detail}\nPID: {row.Pid}\n{headers[1]}: {row.First}\n{headers[2]}: {row.Second}\n{headers[3]}: {row.Third}\n\nThis is a frozen detail view of the observation when opened.";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = row.Name, CloseButtonText = "Close",
                Content = new ScrollViewer { MaxHeight = 400, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }
            };
            await dialog.ShowAsync();
        }
        finally { detailsOpen = false; }
    }
    private void TableLoaded(object sender, RoutedEventArgs args) => scroll = FindScroll(Table);
    private void InventoryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string kind }) InventoryRequested?.Invoke(kind);
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private void SeriesChanged(object sender, SelectionChangedEventArgs args) { if (ready) DrawHistory(); }
    private void ViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!ready || ActualHeight <= 0) return;
        var nextCompact = ActualHeight < 520;
        if (compact != nextCompact) { compact = nextCompact; historyExpanded = !compact; }
        UpdateHistoryLayout();
    }
    private void HistoryClicked(object sender, RoutedEventArgs args)
    {
        historyExpanded = !historyExpanded;
        UpdateHistoryLayout();
    }
    private void UpdateHistoryLayout()
    {
        Layout.RowSpacing = compact ? 6 : 8;
        ChartCard.Padding = historyExpanded ? new Thickness(12) : new Thickness(8, 4, 8, 4);
        ChartLayout.RowSpacing = historyExpanded ? 4 : 0;
        PlotRow.Height = new GridLength(historyExpanded ? 72 : 0);
        Trend.Visibility = ChartLegend.Visibility = historyExpanded ? Visibility.Visible : Visibility.Collapsed;
        HistoryToggle.Content = historyExpanded ? "Hide chart" : "Show chart";
    }
    private void DrawHistory()
    {
        if (snapshot is null || section is not ("Memory" or "CPU")) return;
        var cpu = section == "CPU";
        var commit = !cpu && ChartSeries.SelectedIndex == 1;
        double? Value(HistoryPoint point) => cpu ? point.CpuPercent : commit ? point.CommitBytes : point.PhysicalUsedBytes;
        var scale = cpu ? 100d : (double)(commit ? snapshot.Memory.CommitLimitBytes ?? 0 : snapshot.Memory.PhysicalTotalBytes ?? 0);
        scale = Math.Max(scale, history.Select(Value).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max());
        var label = cpu ? "System CPU" : commit ? "System commit (not resident RAM)" : "Physical memory in use";
        ChartTitle.Text = label;
        Trend.Update(DiagnosticData.BuildTrend(history, Value, scale), label, bytes: !cpu);
        ChartLegend.Text = history.Count == 0 ? "Waiting for observed history" :
            $"{history[0].At.LocalDateTime:T} — {history[^1].At.LocalDateTime:T} · {history.Count} samples · Axis 0–{(cpu ? "100%" : DiagnosticData.Bytes((ulong)scale))} · Elapsed-time spacing; gaps are not joined";
    }
}
