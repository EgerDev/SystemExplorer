using System.Diagnostics;
using Path = System.IO.Path;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SystemExplorer.Core;
using SystemExplorer.Windows;
using SystemExplorer.Relationships;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SystemExplorer.App;

public sealed partial class MainWindow : Window
{
    public ShellViewModel Model { get; } = new();
    private readonly IMonitorProvider monitor = new WindowsMonitorProvider();
    private readonly IRelationshipProvider relationships = new WindowsRelationshipProvider();
    private readonly CancellationTokenSource shutdown = new();
    private readonly ProcessIcons icons = new();
    private readonly NativeTray tray;
    private Task? polling;
    private bool paused;
    private bool ready;
    private bool detailsOpen;
    private string section = "Processes";
    private ProcessIdentity? selectedIdentity;
    private IReadOnlyList<StartupItem> startup = Array.Empty<StartupItem>();
    private DateTimeOffset? startupCapturedAt;
    private string? startupError;
    private string? relationshipError;
    private int publisherGeneration;

    public MainWindow()
    {
        InitializeComponent();
        tray = new NativeTray(this);
        ready = true;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1440, 920));
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) => { GlobalSearch.Focus(FocusState.Keyboard); args.Handled = true; };
        Root.KeyboardAccelerators.Add(accelerator);
        Activated += (_, _) => polling ??= PollAsync();
        Closed += async (_, _) =>
        {
            tray.Dispose();
            shutdown.Cancel();
            if (polling is not null) await polling;
            monitor.Dispose();
            relationships.Dispose();
        };
    }

    private async Task PollAsync()
    {
        var cycle = 0;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (!paused)
                {
                    var next = await monitor.CaptureAsync(shutdown.Token);
                    if (shutdown.IsCancellationRequested) break;
                    if (paused) continue;
                    Model.Apply(next);
                    tray.Update(next);
                    if (cycle++ % 3 == 0)
                    {
                        try
                        {
                            var related = await relationships.CaptureAsync(next.Processes, shutdown.Token);
                            if (!paused && !shutdown.IsCancellationRequested) Model.Relationships = related;
                            relationshipError = null;
                        }
                        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                        catch (Exception ex) { Model.Relationships = null; relationshipError = $"Relationship data unavailable: {ex.Message}"; }
                    }
                    if (cycle % 30 == 1)
                    {
                        try
                        {
                            startup = await relationships.GetStartupItemsAsync(shutdown.Token);
                            startupCapturedAt = DateTimeOffset.UtcNow;
                            startupError = null;
                        }
                        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                        catch (Exception ex) { startupError = $"Startup inventory unavailable: {ex.Message}"; }
                    }
                    RefreshInspector();
                    RefreshDiagnostics();
                    var gaps = next.Limitations.Concat(Model.Relationships?.Limitations ?? Array.Empty<string>()).ToArray();
                    StatusText.Text = paused ? "Paused · displayed measurements are frozen" : $"Updated {next.CapturedAt.LocalDateTime:T} · Local, read-only · {next.Processes.Count} processes · Private commit ≠ physical RAM" + (relationshipError is not null ? $" · {relationshipError}" : gaps.Length > 0 ? $" · {gaps[0]}" : "");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StatusText.Text = $"Refresh failed; previous data is stale: {ex.Message}";
                try { await Task.Delay(2000, shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || args.SelectedItem is not NavigationViewItem item) return;
        section = item.Tag?.ToString() ?? "Processes";
        PageTitle.Text = section;
        PageSubtitle.Text = section switch
        {
            "Processes" => "Live Windows processes, grouped conservatively by executable and parent chain.",
            "Memory" => "Physical memory, private commit and reclaimable categories are different views—not additive totals.",
            "Network" => "Observed TCP connections and UDP endpoints. No inferred bandwidth, DNS or geography.",
            "Services" => "Read-only Windows service inventory with verified process-lifetime associations.",
            "Insights" => "Local, deterministic explanations supported by observed measurements.",
            "Startup" => "Observed startup entries. Presence does not measure startup impact.",
            _ => "Read-only diagnostics from your local Windows system. Unavailable measurements are explicit."
        };
        ProcessesPanel.Visibility = section == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = section is "Processes" or "Settings" ? Visibility.Collapsed : Visibility.Visible;
        RefreshDiagnostics();
    }

    private void GlobalSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!ready) return;
        Model.Search = sender.Text;
        if (section == "Processes") { ProcessSearch.Text = sender.Text; Model.Rebuild(); }
        else RefreshDiagnostics();
    }
    private void ProcessSearchChanged(object sender, TextChangedEventArgs args)
    {
        if (!ready) return;
        Model.Search = ProcessSearch.Text;
        Model.Rebuild();
    }
    private void OptionsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        Model.Grouped = Grouping.SelectedIndex == 0;
        Model.Sort = Sorting.SelectedItem?.ToString() ?? "Private memory";
        Model.Rebuild();
    }
    private void ReverseSortClicked(object sender, RoutedEventArgs args) { Model.Descending = !Model.Descending; Model.Rebuild(); }
    private void ColumnSortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        var sort = button.Tag?.ToString() ?? "Private memory";
        if (Model.Sort == sort) Model.Descending = !Model.Descending;
        Model.Sort = sort;
        Sorting.SelectedItem = sort;
        Model.Rebuild();
    }
    private void ExpandClicked(object sender, RoutedEventArgs args) { if (sender is Button { Tag: ProcessRow row }) Model.Toggle(row); }
    private async void ProcessIconLoaded(object sender, RoutedEventArgs args) => await LoadProcessIconAsync((Image)sender);
    private async void ProcessIconChanged(FrameworkElement sender, DataContextChangedEventArgs args) => await LoadProcessIconAsync((Image)sender);
    private async Task LoadProcessIconAsync(Image image)
    {
        var row = image.DataContext as ProcessRow;
        var path = row?.Process.ExecutablePath;
        if (Equals(image.Tag, path)) return;
        image.Tag = path;
        image.Source = null;
        if (path is null) return;
        try
        {
            var icon = await icons.LoadAsync(path, shutdown.Token);
            if (!shutdown.IsCancellationRequested && ReferenceEquals(image.DataContext, row) && Equals(image.Tag, path)) image.Source = icon;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The generic icon remains available. */ }
    }
    private async void ProcessDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (ProcessList.SelectedItem is not ProcessRow row) return;
        if (row.HasChildren) Model.Toggle(row);
        else if (Inspector.Visibility == Visibility.Collapsed)
            await ShowDetailsAsync();
    }
    private async Task ShowDetailsAsync()
    {
        if (detailsOpen) return;
        detailsOpen = true;
        try
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = SelectedName.Text, Content = new ScrollViewer { Content = new TextBlock { Text = $"{SelectedIdentity.Text}\n\n{SelectedPath.Text}\n\n{SelectedMemory.Text}\n\n{SelectedServices.Text}\n{SelectedNetwork.Text}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, CloseButtonText = "Close" }.ShowAsync();
        }
        catch (Exception ex) { if (!shutdown.IsCancellationRequested) StatusText.Text = $"Details unavailable: {ex.Message}"; }
        finally { detailsOpen = false; }
    }
    private async void ProcessKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if ((args.Key is VirtualKey.Right or VirtualKey.Left or VirtualKey.Enter) && ProcessList.SelectedItem is ProcessRow row && row.HasChildren)
        {
            if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Right && !row.IsExpanded || args.Key == VirtualKey.Left && row.IsExpanded) Model.Toggle(row);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter && ProcessList.SelectedItem is ProcessRow && Inspector.Visibility == Visibility.Collapsed)
        {
            args.Handled = true;
            await ShowDetailsAsync();
        }
    }
    private void ProcessSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        if (ProcessList.SelectedItem is ProcessRow row)
        {
            if (selectedIdentity != row.Identity) { publisherGeneration++; PublisherText.Text = ""; }
            selectedIdentity = row.Identity;
        }
        else selectedIdentity = null;
        RefreshInspector();
    }

    private ProcessSnapshot? SelectedProcess => selectedIdentity is { } identity ? Model.Snapshot?.Processes.FirstOrDefault(p => p.Identity == identity) : null;
    private void RefreshInspector()
    {
        var process = SelectedProcess;
        if (process is null)
        {
            SelectedName.Text = selectedIdentity is null ? "Process inspector" : "Process no longer observed";
            SelectedIdentity.Text = "Select a current process to inspect its live measurements.";
            SelectedPath.Text = SelectedCommandLine.Text = SelectedParent.Text = SelectedMemory.Text = SelectedServices.Text = SelectedNetwork.Text = "—";
            SelectedAvailability.Text = "";
            PublisherText.Text = "";
            OpenLocation.IsEnabled = CopyPath.IsEnabled = PublisherButton.IsEnabled = false;
            return;
        }
        SelectedName.Text = process.Name;
        SelectedIdentity.Text = $"PID {process.Identity.Pid} · {process.ThreadCount?.ToString() ?? "?"} threads · {process.HandleCount?.ToString() ?? "?"} handles\n{process.UserName ?? "User unavailable"}\nSelected member; group totals are in the table.";
        SelectedPath.Text = process.ExecutablePath ?? "Unavailable (permission or process lifetime)";
        SelectedCommandLine.Text = process.CommandLine ?? "Unavailable from this documented-API provider";
        var byPid = Model.Snapshot!.Processes.ToDictionary(p => p.Identity.Pid);
        var parent = ProcessGrouping.VerifiedParent(process, byPid);
        SelectedParent.Text = parent is null ? "Unverified or no longer observed" : $"{parent.Name} (PID {parent.Identity.Pid}) · observed ancestry";
        SelectedMemory.Text = $"Private commit: {Display.Bytes(process.PrivateBytes)}\nWorking set (includes shared): {Display.Bytes(process.WorkingSetBytes)}\nPrivate working set: {Display.Bytes(process.PrivateWorkingSetBytes)}";
        var rel = Model.Relationships;
        var fresh = rel is not null && Math.Abs((Model.Snapshot.CapturedAt - rel.CapturedAt).TotalSeconds) <= 10;
        SelectedServices.Text = !fresh ? "Unavailable or stale" : string.Join("\n", rel!.Services.Where(s => s.OwnerIdentity == process.Identity && process.Identity.IsStable).Select(s => $"{s.DisplayName} ({s.Name})").DefaultIfEmpty("No verified associations observed"));
        SelectedNetwork.Text = !fresh ? "Unavailable or stale" : $"{rel!.Connections.Count(c => c.OwnerIdentity == process.Identity && process.Identity.IsStable)} verified TCP/UDP entries · throughput unavailable";
        SelectedAvailability.Text = process.UnavailableReason ?? (process.Identity.IsStable ? "Read-only observation. Some fields may be unavailable." : "Creation time unavailable; history and associations are not trusted.");
        var local = IsLocalFile(process.ExecutablePath);
        OpenLocation.IsEnabled = PublisherButton.IsEnabled = local;
        CopyPath.IsEnabled = process.ExecutablePath is not null;
    }

    private static bool IsLocalFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':' || path.AsSpan(2).Contains(':')) return false;
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) return false;
            FileSystemInfo? entry = new FileInfo(path);
            while (entry is not null)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent;
            }
            return File.Exists(path);
        }
        catch (Exception) { return false; }
    }
    private void OpenLocationClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = SelectedProcess?.ExecutablePath;
            if (IsLocalFile(path)) Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(path!)!, UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = $"File location unavailable: {ex.Message}"; }
    }
    private void CopyPathClicked(object sender, RoutedEventArgs args)
    {
        if (SelectedProcess?.ExecutablePath is not { } path) return;
        var data = new DataPackage(); data.SetText(path); Clipboard.SetContent(data);
    }
    private async void PublisherClicked(object sender, RoutedEventArgs args)
    {
        if (SelectedProcess is not { } process || !IsLocalFile(process.ExecutablePath)) return;
        var generation = ++publisherGeneration;
        PublisherText.Text = "Checking local signature…";
        try
        {
            var result = await relationships.GetPublisherAsync(process.ExecutablePath!, shutdown.Token);
            if (!shutdown.IsCancellationRequested && generation == publisherGeneration && SelectedProcess?.Identity == process.Identity && SelectedProcess.ExecutablePath == process.ExecutablePath)
                PublisherText.Text = $"{result.Publisher ?? "Publisher unavailable"}\n{result.VerificationStatus}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == publisherGeneration) PublisherText.Text = $"Verification unavailable: {ex.Message}"; }
    }

    private void PauseClicked(object sender, RoutedEventArgs args)
    {
        paused = !paused;
        if (Model.Snapshot is { } snapshot) tray.Update(snapshot, paused);
        PauseButton.Content = paused ? "Resume" : "Pause";
        StatusText.Text = paused ? "Paused · displayed measurements are frozen" : "Resuming live local monitoring…";
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        Root.RequestedTheme = ThemeSelector.SelectedIndex switch { 1 => ElementTheme.Dark, 2 => ElementTheme.Default, _ => ElementTheme.Light };
        DrawHistory();
    }
    private void ProcessesSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!ready) return;
        var show = args.NewSize.Width >= 1000;
        Inspector.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = new GridLength(show ? 290 : 0);
    }

    private void RefreshDiagnostics()
    {
        if (!ready || section is "Processes" or "Settings") return;
        var snapshot = Model.Snapshot;
        if (snapshot is null) { DetailsList.ItemsSource = new[] { "Waiting for the first real Windows sample…" }; return; }
        var mem = snapshot.Memory;
        var rows = new List<string>();
        switch (section)
        {
            case "Overview":
                rows.Add($"CPU {Display.Percent(snapshot.TotalCpuPercent)}\nPhysical memory {Display.Bytes(mem.PhysicalUsedBytes)} used / {Display.Bytes(mem.PhysicalTotalBytes)} total\n{snapshot.Processes.Count} observed processes");
                rows.AddRange(Model.History.Explain(snapshot, Model.Relationships).Take(4).Select(i => $"{i.Title}\n{i.Evidence}"));
                break;
            case "Memory":
                rows.Add($"Physical: {Display.Bytes(mem.PhysicalUsedBytes)} used · {Display.Bytes(mem.PhysicalAvailableBytes)} available · {Display.Bytes(mem.PhysicalTotalBytes)} total");
                rows.Add($"System commit: {Display.Bytes(mem.CommitTotalBytes)} / {Display.Bytes(mem.CommitLimitBytes)} limit\nCommit is not resident RAM.");
                rows.Add($"System cache: {Display.Bytes(mem.SystemCacheBytes)} · Standby: {Display.Bytes(mem.StandbyBytes)}\nKernel paged: {Display.Bytes(mem.KernelPagedBytes)} · nonpaged: {Display.Bytes(mem.KernelNonpagedBytes)}\nThese categories overlap; do not add them to physical usage. — means unavailable.");
                rows.AddRange(ProcessGrouping.Create(snapshot.Processes).OrderByDescending(g => g.PrivateBytes).Take(50).Select(g => $"{g.Name} · {g.Members.Count} process(es)\nPrivate commit: {Display.Bytes(g.PrivateBytes)} · not physical RAM"));
                rows.AddRange(Model.History.Explain(snapshot).Where(i => i.Title.StartsWith("Sustained")).Select(i => $"{i.Title}\n{i.Evidence}"));
                break;
            case "CPU":
                rows.Add($"System CPU: {Display.Percent(snapshot.TotalCpuPercent)}\nProcess CPU is normalized to total logical-processor capacity. First samples are unavailable.");
                rows.AddRange(snapshot.Processes.OrderByDescending(p => p.CpuPercent).Take(50).Select(p => $"{p.Name} · PID {p.Identity.Pid} · {Display.Percent(p.CpuPercent)}"));
                break;
            case "GPU":
                rows.Add(snapshot.GpuPercent is null ? "GPU utilization unavailable\nThis provider does not yet expose a validated GPU engine counter. No CPU or process-I/O estimate is substituted." : $"GPU utilization: {Display.Percent(snapshot.GpuPercent)}");
                break;
            case "Disks":
                rows.Add($"Physical disk read: {(snapshot.DiskReadBytesPerSecond is { } read ? Display.Rate(read) : "Unavailable")}\nPhysical disk write: {(snapshot.DiskWriteBytesPerSecond is { } write ? Display.Rate(write) : "Unavailable")}\nBelow is process I/O, which may include cached, device and network operations; it is not physical disk throughput.");
                rows.AddRange(snapshot.Processes.Where(p => p.ReadBytesPerSecond.HasValue).OrderByDescending(p => p.ReadBytesPerSecond + p.WriteBytesPerSecond).Take(50).Select(p => $"{p.Name} · PID {p.Identity.Pid}\nRead {Display.Rate(p.ReadBytesPerSecond!.Value)} · Write {(p.WriteBytesPerSecond is { } rate ? Display.Rate(rate) : "Unavailable")}"));
                break;
            case "Network":
                rows.Add("Observed endpoints; UDP has no remote peer. Owner PID is reported separately from verified process identity.");
                if (Model.Relationships is { } network) rows.Add($"Relationship observation: {network.CapturedAt.LocalDateTime:T}. Ownership is verified at capture time; connections can change between refreshes.");
                rows.AddRange(Model.Relationships?.Connections.Select(c => $"{c.Protocol} {c.LocalAddress}:{c.LocalPort} → {(c.RemoteAddress is null ? "—" : $"{c.RemoteAddress}:{c.RemotePort}")}\nPID {c.OwningPid} · {c.State ?? "endpoint"} · {(c.OwnerIdentity is null ? "ownership unverified" : "identity verified")}") ?? new[] { "Endpoint collection unavailable" });
                break;
            case "Services":
                if (Model.Relationships is { } services) rows.Add($"Service observation: {services.CapturedAt.LocalDateTime:T}. Associations refer to this capture, not continuous tracing.");
                rows.AddRange(Model.Relationships?.Services.Select(s => $"{s.DisplayName} ({s.Name})\n{s.State} · PID {s.ProcessId?.ToString() ?? "—"} · Start: {s.StartType ?? "Unavailable"} · {(s.OwnerIdentity is null ? "No verified owner" : "Identity verified")}") ?? new[] { "Service collection unavailable" });
                break;
            case "Startup":
                rows.Add("Registry Run keys and startup folders only. Scheduled tasks and other startup mechanisms may not be included. No startup-impact measurement or disable action is inferred.");
                if (startupCapturedAt is { } captured) rows.Add($"Last successful startup inventory: {captured.LocalDateTime:T}. Refreshed approximately once per minute.");
                if (startupError is not null) rows.Add(startupError + " Any retained entries are stale.");
                rows.AddRange(startup.Select(s => $"{s.Name} · {s.Scope}\n{s.Command}\n{s.Source}"));
                if (startup.Count == 0) rows.Add("No entries observed, or inventory unavailable.");
                break;
            case "Insights": rows.AddRange(Model.History.Explain(snapshot, Model.Relationships).Select(i => $"{i.Kind}: {i.Title}\n{i.Evidence}")); break;
            case "Help":
                rows.Add("SystemExplorer · native Windows diagnostics\nCtrl+K focuses search. Use arrows and Enter to expand application groups. Double-click an individual row to inspect it when the side panel is hidden.");
                rows.Add("Private commit is committed virtual memory, not RAM. Working sets contain shared pages. A growth trend is not proof of a memory leak. — means unavailable, not zero.");
                rows.Add("Grouping is inferred from matching executable, owner and observed ancestry. Missing identity prevents history and verified associations. Read-only: no process termination, suspension, driver installation or automatic elevation.");
                rows.Add("Original System Informer source remains in this repository under its original notices. This frontend does not automatically load its plugins or upload process information.");
                break;
        }
        rows.AddRange(snapshot.Limitations.Select(l => $"Provider limitation: {l}"));
        if (Model.Relationships is { } rel && (section is "Network" or "Services")) rows.AddRange(rel.Limitations.Select(l => $"Relationship limitation: {l}"));
        var query = GlobalSearch.Text?.Trim() ?? "";
        DetailsList.ItemsSource = rows.Where(r => query.Length == 0 || r.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        ChartCard.Visibility = section is "Overview" or "Memory" or "CPU" ? Visibility.Visible : Visibility.Collapsed;
        DrawHistory();
    }

    private void ChartSizeChanged(object sender, SizeChangedEventArgs args) => DrawHistory();
    private void DrawHistory()
    {
        if (!ready) return;
        HistoryChart.Children.Clear();
        var points = Model.History.System;
        var cpu = section == "CPU";
        ChartTitle.Text = cpu ? "System CPU history" : "Physical memory history";
        ChartLegend.Text = $"{points.Count} observed samples · up to 180 samples · gaps are not interpolated · {(cpu ? "0–100%" : "0–physical total")}";
        if (points.Count < 2 || HistoryChart.ActualWidth <= 0) return;
        var maximum = cpu ? 100d : (double)(Model.Snapshot?.Memory.PhysicalTotalBytes ?? 0);
        if (maximum <= 0) return;
        Polyline? line = null;
        for (var i = 0; i < points.Count; i++)
        {
            double? value = cpu ? points[i].CpuPercent : points[i].PhysicalUsedBytes is { } bytes ? bytes : null;
            if (value is null || i > 0 && (points[i].At - points[i - 1].At).TotalSeconds > 10) line = null;
            if (value is null) continue;
            if (line is null)
            {
                line = new Polyline { Stroke = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 120, 212)), StrokeThickness = 2 };
                HistoryChart.Children.Add(line);
            }
            line.Points.Add(new global::Windows.Foundation.Point(i * HistoryChart.ActualWidth / (points.Count - 1), 105 - Math.Clamp(value.Value / maximum, 0, 1) * 100));
        }
    }
}
