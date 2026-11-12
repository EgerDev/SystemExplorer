using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;
using Windows.UI;

namespace SystemExplorer.App.Views;

public sealed partial class RamAttributionView : UserControl
{
    private readonly MonitorSnapshot snapshot;
    private readonly IRamBreakdownProvider machineProvider;
    private readonly IProcessResidentProvider processProvider;
    private readonly IPoolTagProvider poolProvider;
    private readonly IPoolTagDriverProvider driverProvider;
    private readonly IPhysicalMemoryProvider physicalProvider;
    private readonly Func<RamExportDocument, string, CancellationToken, Task<string?>> export;
    private readonly CancellationToken lifetime;
    private readonly OperationGate operation = new();
    private Task active = Task.CompletedTask;
    private RamOperationKind operationKind;
    private readonly Dictionary<int, List<RamExportRow>> exports = [];
    private readonly Dictionary<int, DateTimeOffset> captured = [];
    private PoolTagInventory? pool;
    private PoolTagDriverInspection? drivers;
    private DateTimeOffset driverCaptured;

    public RamAttributionView(MonitorSnapshot snapshot, IRamBreakdownProvider machineProvider, IProcessResidentProvider processProvider,
        IPoolTagProvider poolProvider, IPoolTagDriverProvider driverProvider, IPhysicalMemoryProvider physicalProvider,
        bool administrator, Func<RamExportDocument, string, CancellationToken, Task<string?>> export, CancellationToken lifetime)
    {
        InitializeComponent();
        this.snapshot = snapshot;
        this.machineProvider = machineProvider; this.processProvider = processProvider; this.poolProvider = poolProvider;
        this.driverProvider = driverProvider; this.physicalProvider = physicalProvider; this.export = export; this.lifetime = lifetime;
        AdminCard.Visibility = administrator ? Visibility.Collapsed : Visibility.Visible;
        PhysicalPanel.Visibility = administrator ? Visibility.Visible : Visibility.Collapsed;
        exports[3] = [new("Physical memory", administrator ? "Not scanned" : "RequiresAdministrator",
            administrator ? "No query made. Click Scan physical pages." : "Nothing is elevated automatically.", new Dictionary<string, string>())];
        captured[3] = DateTimeOffset.UtcNow;
        exports[1] = [new("Processes", "Not scanned", "No process scan performed.", new Dictionary<string, string>())];
        captured[1] = snapshot.CapturedAt;
    }

    public Task InitializeAsync() => Start(RamOperationKind.Initialize, async ticket =>
    {
        var token = ticket.CancellationToken;
        var machine = await machineProvider.CaptureAsync(snapshot, token);
        token.ThrowIfCancellationRequested();
        ShowMachine(machine);
        pool = await poolProvider.CaptureAsync(token);
        token.ThrowIfCancellationRequested();
        ShowPool();
        Message.Text = "Ready. Scans are explicit and bounded; no live polling in this dialog.";
    });

    public async Task StopAsync()
    {
        operation.RequestCancel();
        await active;
    }

    private Task Start(RamOperationKind kind, Func<OperationTicket, Task> work)
    {
        if (lifetime.IsCancellationRequested || !operation.TryBegin(out var ticket, lifetime)) return Task.CompletedTask;
        operationKind = kind;
        active = Run(ticket, kind, work);
        return active;
    }

    private async Task Run(OperationTicket ticket, RamOperationKind kind, Func<OperationTicket, Task> work)
    {
        UpdateOperationControls();
        try { await work(ticket); }
        catch (OperationCanceledException)
        {
            if (operation.IsCurrent(ticket)) { FinishPanel(kind, true); Message.Text = InvestigationOperationStatus.CancellationNotice; }
        }
        catch (Exception ex)
        {
            if (operation.IsCurrent(ticket)) { FinishPanel(kind, false); Message.Text = (kind == RamOperationKind.Export ? "Export failed: " : "Unavailable: ") + ex.Message; }
        }
        finally { if (operation.Complete(ticket)) UpdateOperationControls(); }
    }

    private void FinishPanel(RamOperationKind kind, bool cancelled)
    {
        var status = InvestigationOperationStatus.TerminalScanStatus(cancelled);
        if (kind == RamOperationKind.Processes) { ProcessStatus.Text = status; ProcessProgress.Value = 0; }
        if (kind == RamOperationKind.Physical) { PhysicalStatus.Text = status; PhysicalProgress.Value = 0; }
        if (kind == RamOperationKind.Drivers) { PoolStatus.Text = status; PoolProgress.Value = 0; }
    }

    private void UpdateOperationControls()
    {
        ProcessEmpty.Visibility = ProcessTable.Content is null && !operation.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ProcessScan.IsEnabled = PhysicalScan.IsEnabled = !operation.IsBusy;
        FindDrivers.IsEnabled = !operation.IsBusy && pool is not null;
        ProcessCancel.IsEnabled = PoolCancel.IsEnabled = PhysicalCancel.IsEnabled = operation.Phase == OperationPhase.Running && operationKind != RamOperationKind.Export;
        ExportButton.IsEnabled = !operation.IsBusy;
    }

    private void CancelClicked(object sender, RoutedEventArgs args) { operation.RequestCancel(); UpdateOperationControls(); }
    private void PhysicalTableChanged(object sender, SelectionChangedEventArgs args)
    {
        if (UseTable is null || PriorityTable is null || FileTable is null) return;
        UseTable.Visibility = PhysicalTableSelector.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PriorityTable.Visibility = PhysicalTableSelector.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        FileTable.Visibility = PhysicalTableSelector.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string Bytes(ulong? bytes) => bytes.HasValue ? DiagnosticData.Bytes(bytes) : "Unavailable";
    private static string SignedBytes(long? bytes) => bytes.HasValue ? (bytes < 0 ? "−" : "") + Bytes((ulong)Math.Abs(bytes.Value)) : "Unavailable";
    private static string Count(ulong value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private void ShowMachine(RamBreakdown machine)
    {
        captured[0] = DateTimeOffset.UtcNow;
        exports[0] = [];
        var availabilityRows = machine.Categories.Where(c => c.Name.StartsWith("Standby", StringComparison.Ordinal) || c.Name == "Free / zero").ToArray();
        long? available = availabilityRows.All(c => c.Bytes.HasValue) ? availabilityRows.Sum(c => c.Bytes!.Value) : null;
        var modified = machine.Categories.FirstOrDefault(c => c.Name.StartsWith("Modified", StringComparison.Ordinal))?.Bytes;
        long? used = machine.UsableBytes.HasValue && available.HasValue && modified.HasValue ? (long)machine.UsableBytes.Value - available.Value - modified.Value : null;
        UsableCard.Value = Bytes(machine.UsableBytes); AvailableCard.Value = SignedBytes(available); InUseCard.Value = SignedBytes(used);
        ReservedCard.Value = SignedBytes(machine.HardwareReservedBytes);
        ToolTipService.SetToolTip(ReservedCard, machine.Detail);
        MachineNote.Text = $"Captured {captured[0]:g}. {machine.Detail}";
        var rows = machine.Categories.Select(c => Row([c.Name, SignedBytes(c.Bytes), machine.UsableBytes is > 0 && c.Bytes.HasValue
            ? ((double)c.Bytes.Value / machine.UsableBytes.Value).ToString("P1") : "Unavailable", c.Source,
            c.Name == "Compression store" && c.Bytes == 0 ? "Off / no store" : RamAttributionPresentation.StatusName(c.Status)],
            [null, c.Bytes, c.Bytes, null, null], c.Status.ToString(), c.Detail)).ToArray();
        Table(MachineTable, ["Category", "Size", "% usable", "Source", "Status"], [290, 108, 85, 270, 120], [false, true, true, false, false], rows);
        SaveRows(0, "Machine breakdown", ["Category", "Size", "% usable", "Source", "Status"], rows);
        exports[0].Insert(0, new("Physical memory summary", "Observed / unavailable as indicated", machine.Detail,
            new Dictionary<string, string> { ["Usable bytes"] = machine.UsableBytes?.ToString() ?? "Unavailable", ["Installed bytes"] = machine.InstalledBytes?.ToString() ?? "Unavailable",
                ["Hardware reserved bytes"] = machine.HardwareReservedBytes?.ToString() ?? "Unavailable", ["In use bytes"] = used?.ToString() ?? "Unavailable", ["Available bytes"] = available?.ToString() ?? "Unavailable" }));
        Composition.Children.Clear(); Composition.ColumnDefinitions.Clear(); Legend.Items.Clear();
        var segments = RamAttributionPresentation.BarSegments(machine);
        BarNote.Text = segments.Count == 0 ? "Stack unavailable: missing inputs or overlapping counters. See the signed remainder below; no values are clamped." : "Bar partitions usable memory. Legend and source table below; observations are not atomic.";
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var color = RamAttributionPresentation.CategoryColor(segment.Name);
            var brush = new SolidColorBrush(Color.FromArgb(255, Convert.ToByte(color.Substring(1, 2), 16), Convert.ToByte(color.Substring(3, 2), 16), Convert.ToByte(color.Substring(5, 2), 16)));
            Composition.ColumnDefinitions.Add(new() { Width = new GridLength(segment.Fraction, GridUnitType.Star) });
            var block = new Border { Background = brush }; Grid.SetColumn(block, i);
            ToolTipService.SetToolTip(block, $"{segment.Name}: {segment.Fraction:P1}"); Composition.Children.Add(block);
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            legend.Children.Add(new Border { Width = 16, Height = 16, Background = brush, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center });
            var size = machine.Categories.First(c => c.Name == segment.Name).Bytes;
            var label = segment.Name.StartsWith("Other in-use", StringComparison.Ordinal) ? "Other in-use (remainder)" : segment.Name;
            legend.Children.Add(new TextBlock { Text = $"{label}\n{SignedBytes(size)} · {segment.Fraction:P1}", Style = (Style)Application.Current.Resources["Muted"], Width = 290, TextWrapping = TextWrapping.Wrap });
            ToolTipService.SetToolTip(legend, segment.Name);
            Legend.Items.Add(legend);
        }
    }

    private async void ProcessScanClicked(object sender, RoutedEventArgs args) => await Start(RamOperationKind.Processes, async ticket =>
    {
        var token = ticket.CancellationToken;
        ProcessEmpty.Visibility = Visibility.Collapsed;
        ProcessStatus.Text = $"Scanning 0 / {snapshot.Processes.Count}; identities from {snapshot.CapturedAt:g}";
        ProcessProgress.Value = 0; ProcessProgress.Maximum = Math.Max(1, snapshot.Processes.Count);
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (token.IsCancellationRequested || !operation.IsCurrent(ticket)) return;
            ProcessProgress.Maximum = Math.Max(1, p.Total); ProcessProgress.Value = p.Done;
            ProcessStatus.Text = $"Scanning {p.Done} / {p.Total}";
        });
        var result = await processProvider.CaptureAllAsync(snapshot.Processes, progress, token);
        token.ThrowIfCancellationRequested();
        captured[1] = DateTimeOffset.UtcNow; exports[1] = [];
        var headers = new[] { "Process", "PID", "Private resident", "Actually shared", "Shareable (sole)", "Proportional est.", "Shared commit", "Private commit", "Status" };
        var rows = result.Select(r =>
        {
            var status = RamAttributionPresentation.ProcessStatusName(r.Identity, r.Status);
            var valid = r.PagesScanned > 0 || r.Status == RamReadingStatus.Available;
            string Resident(ulong n) => valid ? Bytes(n) : "Unavailable";
            return Row([r.Name, r.Identity.Pid.ToString(), Resident(r.PrivateResidentBytes), Resident(r.ActuallySharedBytes), Resident(r.ShareableSoleBytes),
                valid ? Bytes((ulong)r.ProportionalEstimateBytes) : "Unavailable", Bytes(r.SharedCommitBytes), Bytes(r.PrivateCommitBytes), status],
                [null, r.Identity.Pid, valid ? r.PrivateResidentBytes : null, valid ? r.ActuallySharedBytes : null, valid ? r.ShareableSoleBytes : null,
                    valid ? (decimal)r.ProportionalEstimateBytes : null, r.SharedCommitBytes, r.PrivateCommitBytes, null], status,
                $"{r.Identity}; {r.PagesScanned:N0} pages; source: NtQueryVirtualMemory MemoryWorkingSetInformation / VM_COUNTERS_EX2. {r.Detail}");
        }).ToArray();
        Table(ProcessTable, headers, [170, 55, 112, 112, 112, 118, 110, 110, 120], [false, true, true, true, true, true, true, true, false], rows, 2, true);
        SaveRows(1, "Process resident attributes", headers, rows);
        ProcessStatus.Text = $"Captured {captured[1]:g} · {result.Count} rows · " + string.Join(", ", result.GroupBy(r => RamAttributionPresentation.ProcessStatusName(r.Identity, r.Status)).Select(g => $"{g.Key}: {g.Count()}"));
        ProcessScan.Content = "Rescan processes";
        Message.Text = "Process scan complete. Status tooltips include denied / truncated reasons. Sort with column headers; scroll horizontally for all columns.";
    });

    private void ShowPool()
    {
        if (pool is null) return;
        captured[2] = pool.CapturedAt;
        exports[2] = [new("Pool capture", pool.Status.ToString(), pool.Detail, new Dictionary<string, string>() )];
        var headers = new[] { "Tag", "Paged", "Nonpaged", "Total", "Candidate drivers (lead)" };
        var leads = drivers?.Leads.ToDictionary(l => l.Tag);
        var rows = pool.Tags.Select(t =>
        {
            var lead = leads?.GetValueOrDefault(t.RawTag);
            var candidates = lead is null ? "Not scanned" : lead.TotalMatches == 0 ? "Unknown" : string.Join(", ", lead.CandidateFiles) + (lead.TotalMatches > lead.CandidateFiles.Count ? $" (+{lead.TotalMatches - lead.CandidateFiles.Count})" : "");
            return Row([RamAttributionPresentation.PoolTag(t.RawTag), Bytes(t.PagedUsedBytes), Bytes(t.NonPagedUsedBytes), Bytes(t.PagedUsedBytes + t.NonPagedUsedBytes), candidates],
                [null, t.PagedUsedBytes, t.NonPagedUsedBytes, (decimal)t.PagedUsedBytes + t.NonPagedUsedBytes, null], drivers?.Status.ToString() ?? pool.Status.ToString(),
                $"Source: SystemPoolTagInformation. {pool.Detail} {lead?.Detail} {(drivers is null ? "Driver scan not run." : $"Driver scan {driverCaptured:O}: {drivers.Detail}")}");
        }).ToArray();
        Table(PoolTable, headers, [95, 115, 115, 115, 510], [false, true, true, true, false], rows, 3, true);
        SaveRows(2, "Pool tags / local driver string leads", headers, rows);
        PoolStatus.Text = $"Tags captured {pool.CapturedAt:g} · {pool.Status} · {pool.Tags.Count} tags\n" + (drivers is null ? pool.Detail : $"Driver scan {driverCaptured:g} · {drivers.Status} · {drivers.Detail}");
    }

    private async void FindDriversClicked(object sender, RoutedEventArgs args) => await Start(RamOperationKind.Drivers, async ticket =>
    {
        var token = ticket.CancellationToken;
        if (pool is null) return;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (token.IsCancellationRequested || !operation.IsCurrent(ticket)) return;
            PoolProgress.Maximum = Math.Max(1, p.Total); PoolProgress.Value = p.Done; PoolStatus.Text = $"Finding driver string leads: {p.Done} / {p.Total} files";
        });
        var result = await driverProvider.CaptureAsync(pool.Tags.Select(t => t.RawTag).ToArray(), progress, token);
        token.ThrowIfCancellationRequested(); drivers = result; driverCaptured = DateTimeOffset.UtcNow; ShowPool();
        Message.Text = "Driver scan complete. Candidates are leads only, not allocation ownership.";
    });

    private async void PhysicalScanClicked(object sender, RoutedEventArgs args) => await Start(RamOperationKind.Physical, async ticket =>
    {
        var token = ticket.CancellationToken;
        var progress = new Progress<(ulong Done, ulong Total)>(p =>
        {
            if (token.IsCancellationRequested || !operation.IsCurrent(ticket)) return;
            PhysicalProgress.Maximum = Math.Max(1, p.Total); PhysicalProgress.Value = p.Done;
            PhysicalStatus.Text = $"Scanning {p.Done:N0} / {p.Total:N0} PFNs";
        });
        var result = await physicalProvider.CaptureAsync(progress, token);
        token.ThrowIfCancellationRequested(); captured[3] = DateTimeOffset.UtcNow;
        exports[3] = [new("Physical capture", result.Status.ToString(), result.Detail, new Dictionary<string, string> { ["Pages scanned"] = result.PagesScanned.ToString() })];
        PhysicalStatus.Text = $"Captured {captured[3]:g} · {RamAttributionPresentation.StatusName(result.Status)}\n{result.Detail}";
        var headers = new[] { "Use", "Total", "Active", "Standby", "Modified", "Modified no-write", "Transition" };
        RamTableRow UseRow(string name, IEnumerable<PhysicalPageCount> entries)
        {
            var list = entries.ToArray();
            ulong Sum(PhysicalPageList state) => list.Where(c => c.List == state).Aggregate(0UL, (sum, c) => sum + c.Pages);
            var values = new[] { list.Aggregate(0UL, (sum, c) => sum + c.Pages), Sum(PhysicalPageList.Active), Sum(PhysicalPageList.Standby), Sum(PhysicalPageList.Modified), Sum(PhysicalPageList.ModifiedNoWrite), Sum(PhysicalPageList.Transition) };
            return Row(new[] { name }.Concat(values.Select(Count)).ToArray(), new decimal?[] { null }.Concat(values.Select(v => (decimal?)v)).ToArray(), result.Status.ToString(), "Source: Superfetch PFN attributes. " + result.Detail);
        }
        var rows = result.Counts.Where(c => c.Use != PhysicalPageUse.Unused).GroupBy(c => c.Use)
            .Select(g => UseRow(RamAttributionPresentation.UseName(g.Key), g)).ToList();
        rows.AddRange(result.Counts.Where(c => c.Use == PhysicalPageUse.Unused).Select(c => UseRow("Unused / " + c.List, [c])));
        Table(UseTable, headers, [215, 100, 100, 100, 100, 145, 100], [false, true, true, true, true, true, true], rows.ToArray(), 1, true);
        SaveRows(3, "Use × list (pages); Superfetch PFN attributes", headers, rows);
        var priorityHeaders = new[] { "Priority", "Standby pages", "Repurposed pages" };
        var priorityRows = result.MemoryLists is { } lists ? Enumerable.Range(0, 8).Select(i => Row([i.ToString(), Count(lists.StandbyByPriority[i]), Count(lists.RepurposedByPriority[i])],
            [i, lists.StandbyByPriority[i], lists.RepurposedByPriority[i]], result.Status.ToString(), "Source: SystemMemoryListInformation; repurposed counts are cumulative, not current residency.")).ToArray() : [];
        Table(PriorityTable, priorityHeaders, [150, 180, 180], [true, true, true], priorityRows);
        SaveRows(3, "Priority (pages); SystemMemoryListInformation", priorityHeaders, priorityRows);
        var fileHeaders = new[] { "File object", "Total", "Active", "Standby", "Modified", "Modified no-write", "Transition", "Zero", "Free", "Bad" };
        var fileRows = result.FileObjects.Select(f =>
        {
            var values = new[] { f.PagesByList.Aggregate(0UL, (sum, p) => sum + p), f.PagesByList[6], f.PagesByList[2], f.PagesByList[3], f.PagesByList[4], f.PagesByList[7], f.PagesByList[0], f.PagesByList[1], f.PagesByList[5] };
            return Row(new[] { f.Name }.Concat(values.Select(Count)).ToArray(), new decimal?[] { null }.Concat(values.Select(v => (decimal?)v)).ToArray(), result.Status.ToString(), "Source: Superfetch PFN attributes. " + PhysicalMemoryParser.FileNamesDetail);
        }).ToArray();
        Table(FileTable, fileHeaders, [220, 100, 100, 100, 100, 145, 100, 100, 100, 100], [false, true, true, true, true, true, true, true, true, true], fileRows, 1, true);
        SaveRows(3, "File objects (pages); Superfetch PFN attributes", fileHeaders, fileRows);
        if (exports[3].Count == 0) exports[3].Add(new("Physical memory", result.Status.ToString(), result.Detail, new Dictionary<string, string>()));
        PhysicalScan.Content = "Rescan physical pages";
        Message.Text = "Physical scan finished. Values are page counts from PFN attributes, not merged with performance-counter bytes on Overview.";
    });

    private static RamTableRow Row(string[] cells, decimal?[] numbers, string status, string detail) => new(cells, numbers, status, detail);
    private void SaveRows(int tab, string section, string[] headers, IEnumerable<RamTableRow> rows)
    {
        foreach (var row in rows) exports[tab].Add(new(section, row.Status, row.Detail, headers.Select((h, i) => (h, row.Cells[i])).ToDictionary(v => v.h, v => v.Item2)));
    }

    private void Table(ContentControl host, string[] headers, int[] widths, bool[] numeric, RamTableRow[] rows, int sort = 0, bool descending = false)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        var template = new StringBuilder("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Padding='0,6' MinHeight='38' ToolTipService.ToolTip='{Binding Detail}'><Grid.ColumnDefinitions>");
        for (var i = 0; i < headers.Length; i++)
        {
            header.ColumnDefinitions.Add(new() { Width = new GridLength(widths[i]) });
            template.Append($"<ColumnDefinition Width='{widths[i]}'/>");
            var index = i;
            var button = new Button { Content = headers[i] + (sort == i ? descending ? " ↓" : " ↑" : ""), Style = (Style)Application.Current.Resources["ColumnHeader"] };
            ToolTipService.SetToolTip(button, "Sort by " + headers[i]); Grid.SetColumn(button, i); header.Children.Add(button);
            button.HorizontalContentAlignment = numeric[i] ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            button.Click += (_, _) => Table(host, headers, widths, numeric, rows, index, index == sort ? !descending : numeric[index]);
        }
        template.Append("</Grid.ColumnDefinitions>");
        for (var i = 0; i < headers.Length; i++) template.Append("<TextBlock Grid.Column='" + i + "' Text='{Binding C" + i + "}' Style='{StaticResource " + (numeric[i] ? "NumericCell" : "DataCell") + "}' TextWrapping='Wrap' MaxLines='3'/>");
        template.Append("</Grid></DataTemplate>");
        IEnumerable<RamTableRow> ordered = numeric[sort]
            ? descending ? rows.OrderBy(r => r.Numbers[sort] is null).ThenByDescending(r => r.Numbers[sort]) : rows.OrderBy(r => r.Numbers[sort] is null).ThenBy(r => r.Numbers[sort])
            : descending ? rows.OrderByDescending(r => r.Cells[sort], StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Cells[sort], StringComparer.OrdinalIgnoreCase);
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, ItemsSource = ordered.ToArray(),
            ItemTemplate = (DataTemplate)XamlReader.Load(template.ToString()), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Width = widths.Sum() };
        if (host == MachineTable) list.Height = 320;
        list.ItemContainerStyle = (Style)XamlReader.Load("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='Padding' Value='0'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListViewItem'><ContentPresenter Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' HorizontalContentAlignment='Stretch' Padding='0'/></ControlTemplate></Setter.Value></Setter></Style>");
        Grid.SetRow(list, 1); grid.Children.Add(header); grid.Children.Add(list); grid.Width = widths.Sum();
        host.Content = new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(0), Child = new ScrollViewer
        { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled } };
    }

    private async void ExportClicked(object sender, RoutedEventArgs args)
    {
        if (operation.IsBusy || sender is not MenuFlyoutItem { Tag: string format }) return;
        var tab = Tabs.SelectedIndex;
        if (!exports.TryGetValue(tab, out var rows)) { Message.Text = "No capture available on this tab."; return; }
        var names = new[] { "Overview", "Processes", "Pool tags", "Physical memory" };
        await Start(RamOperationKind.Export, async ticket =>
        {
            var token = ticket.CancellationToken;
            var path = await export(new(captured[tab], names[tab], rows.ToArray()), format, token);
            token.ThrowIfCancellationRequested();
            Message.Text = path is null ? "Export cancelled; nothing saved." : "Exported to " + path;
        });
    }
}

public sealed record RamTableRow(string[] Cells, decimal?[] Numbers, string Status, string Detail)
{
    public string C0 => Cells.ElementAtOrDefault(0) ?? "";
    public string C1 => Cells.ElementAtOrDefault(1) ?? "";
    public string C2 => Cells.ElementAtOrDefault(2) ?? "";
    public string C3 => Cells.ElementAtOrDefault(3) ?? "";
    public string C4 => Cells.ElementAtOrDefault(4) ?? "";
    public string C5 => Cells.ElementAtOrDefault(5) ?? "";
    public string C6 => Cells.ElementAtOrDefault(6) ?? "";
    public string C7 => Cells.ElementAtOrDefault(7) ?? "";
    public string C8 => Cells.ElementAtOrDefault(8) ?? "";
    public string C9 => Cells.ElementAtOrDefault(9) ?? "";
}
