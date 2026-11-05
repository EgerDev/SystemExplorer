using System.Diagnostics;
using System.Globalization;
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
    private string? selectedRowKey;
    private bool restoringSelection;
    private IReadOnlyList<StartupItem> startup = Array.Empty<StartupItem>();
    private DateTimeOffset? startupCapturedAt;
    private string? startupError;
    private string? relationshipError;
    private int publisherGeneration;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowTitleBar);
        Root.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        ProcessList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(ProcessKeyDown), true);
        Model.RowsRebuilt += (_, _) => RestoreSelection();
        tray = new NativeTray(this);
        ready = true;
        InitializeOverview();
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(Math.Min(1440, work.Width), Math.Min(920, work.Height)));
        AppWindow.Move(new global::Windows.Graphics.PointInt32(work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2), work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
        UpdateCaptionColors();
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
            var generation = observationGeneration;
            try
            {
                if (!paused)
                {
                    var next = await monitor.CaptureAsync(shutdown.Token);
                    if (shutdown.IsCancellationRequested) break;
                    if (paused || generation != observationGeneration) continue;
                    if (Model.Snapshot is { } previous && next.CapturedAt <= previous.CapturedAt)
                        throw new InvalidOperationException("An out-of-order observation was ignored.");
                    Model.Apply(next);
                    overviewData.Observe(next);
                    overviewStale = false;
                    tray.Update(next);
                    if (cycle++ % 3 == 0)
                    {
                        try
                        {
                            var related = await relationships.CaptureAsync(next.Processes, shutdown.Token);
                            if (shutdown.IsCancellationRequested) break;
                            if (paused || generation != observationGeneration) continue;
                            Model.Relationships = related;
                            relationshipError = null;
                        }
                        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                        catch (Exception ex)
                        {
                            if (paused || generation != observationGeneration) continue;
                            Model.Relationships = null;
                            relationshipError = $"Relationship data unavailable: {ex.Message}";
                        }
                    }
                    if (cycle % 30 == 1)
                    {
                        try
                        {
                            var observedStartup = await relationships.GetStartupItemsAsync(shutdown.Token);
                            if (shutdown.IsCancellationRequested) break;
                            if (paused || generation != observationGeneration) continue;
                            startup = observedStartup;
                            startupCapturedAt = DateTimeOffset.UtcNow;
                            startupError = null;
                        }
                        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                        catch (Exception ex)
                        {
                            if (paused || generation != observationGeneration) continue;
                            startupError = $"Startup inventory unavailable: {ex.Message}";
                        }
                    }
                    RefreshInspector();
                    RefreshDiagnostics();
                    var gaps = next.Limitations.Concat(Model.Relationships?.Limitations ?? Array.Empty<string>()).ToArray();
                    StatusText.Text = paused ? "Paused · displayed measurements are frozen" : $"Updated {next.CapturedAt.LocalDateTime:T} · Local, read-only · {next.Processes.Count} processes" + (relationshipError is not null ? " · Relationship data unavailable" : "");
                    ToolTipService.SetToolTip(StatusText, string.Join("\n", gaps.Append(relationshipError).Where(g => !string.IsNullOrEmpty(g))));
                }
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (paused || generation != observationGeneration) continue;
                overviewData.BreakContinuity();
                overviewStale = true;
                RefreshOverview();
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
            "Overview" => "Live resource use, top consumers, and system insights.",
            "Processes" => "See what's using your system and explore related processes.",
            "Memory" => "Physical memory, private commit and reclaimable categories are different views—not additive totals.",
            "Network" => "Observed TCP connections and UDP endpoints. No inferred bandwidth, DNS or geography.",
            "Services" => "Read-only Windows service inventory with verified process-lifetime associations.",
            "Insights" => "Local, deterministic explanations supported by observed measurements.",
            "Startup" => "Observed startup entries. Presence does not measure startup impact.",
            _ => "Read-only diagnostics from your local Windows system. Unavailable measurements are explicit."
        };
        ProcessesPanel.Visibility = section == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        OverviewPanel.Visibility = section == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = section is "Processes" or "Overview" or "Settings" ? Visibility.Collapsed : Visibility.Visible;
        if (section == "Processes") ProcessSearch.Text = GlobalSearch.Text;
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
        if (GlobalSearch.Text != ProcessSearch.Text) GlobalSearch.Text = ProcessSearch.Text;
        Model.Rebuild();
    }
    private void OptionsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        Model.Grouped = Grouping.SelectedIndex == 0;
        Model.Sort = Sorting.SelectedIndex switch { 1 => "CPU", 2 => "Name", 3 => "PID", _ => "Private memory" };
        Model.Rebuild();
    }
    private void ReverseSortClicked(object sender, RoutedEventArgs args) { Model.Descending = !Model.Descending; Model.Rebuild(); }
    private void ColumnSortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        var sort = button.Tag?.ToString() ?? "Private memory";
        if (Model.Sort == sort) Model.Descending = !Model.Descending;
        Model.Sort = sort;
        Sorting.SelectedIndex = sort switch { "CPU" => 1, "Name" => 2, "PID" => 3, _ => 0 };
        Model.Rebuild();
    }
    private void ExpandClicked(object sender, RoutedEventArgs args) { if (sender is Button { Tag: ProcessRow row }) Model.Toggle(row); }
    private void ProcessContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) args.ItemContainer.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty);
        else if (args.Item is ProcessRow row)
            args.ItemContainer.SetBinding(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
                new Microsoft.UI.Xaml.Data.Binding
                {
                    Source = row,
                    Path = new PropertyPath(nameof(ProcessRow.AutomationName)),
                    Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
                });
    }
    private async void ProcessIconLoaded(object sender, RoutedEventArgs args) => await LoadProcessIconAsync((Image)sender);
    private async void ProcessIconChanged(FrameworkElement sender, DataContextChangedEventArgs args) => await LoadProcessIconAsync((Image)sender);
    private async Task LoadProcessIconAsync(Image image)
    {
        var row = image.DataContext as ProcessRow;
        var identity = row?.Identity;
        var path = row?.Process.ExecutablePath;
        var key = (identity, path);
        if (Equals(image.Tag, key)) return;
        image.Tag = key;
        image.Source = null;
        if (path is null) return;
        try
        {
            if (row is not null)
            {
                var description = await Model.Descriptions.LoadAsync(row.Process, shutdown.Token);
                if (shutdown.IsCancellationRequested || !ReferenceEquals(image.DataContext, row) ||
                    row.Identity != identity || row.Process.ExecutablePath != path || !Equals(image.Tag, key)) return;
                row.SetDescription(description);
            }
            var icon = await icons.LoadAsync(path, shutdown.Token);
            if (!shutdown.IsCancellationRequested && ReferenceEquals(image.DataContext, row) && row?.Identity == identity &&
                row?.Process.ExecutablePath == path && Equals(image.Tag, key)) image.Source = icon;
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
        if (detailsOpen || SelectedProcess is not { } process) return;
        detailsOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = SelectedName.Text, Content = new ScrollViewer { Content = new TextBlock { Text = $"{SelectedRawName.Text}\n{SelectedStatus.Text}\n{SelectedIdentity.Text}\n{SelectedAvailability.Text}\n\nExecutable path\n{SelectedPath.Value}\n\nCommand line\n{SelectedCommandLine.Text}\n\nOwnership\n{SelectedOwner.Value}\n\nParent process\n{SelectedParent.Value}\n\nMemory\n{SelectedMemory.Text}\n\nAssociated services\n{SelectedServices.Value}\n\nNetwork endpoints\n{SelectedNetwork.Value}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, PrimaryButtonText = "Copy PID", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                CopyText(process.Identity.Pid.ToString(CultureInfo.InvariantCulture));
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { if (!shutdown.IsCancellationRequested) StatusText.Text = $"Details unavailable: {ex.Message}"; }
        finally { detailsOpen = false; }
    }
    private async void ProcessKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is not ListViewItem) return;
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
        if (!ready || Model.IsRebuilding || restoringSelection) return;
        foreach (var current in Model.Rows) current.SetSelected(ReferenceEquals(ProcessList.SelectedItem, current));
        if (ProcessList.SelectedItem is ProcessRow row)
        {
            if (selectedIdentity != row.Identity) { publisherGeneration++; PublisherText.Text = ""; }
            selectedIdentity = row.Identity;
            selectedRowKey = row.RowKey;
        }
        else { selectedIdentity = null; selectedRowKey = null; }
        RefreshInspector();
    }

    private void RestoreSelection()
    {
        if (selectedRowKey is null) return;
        var row = Model.Rows.FirstOrDefault(r => r.RowKey == selectedRowKey);
        restoringSelection = true;
        try
        {
            ProcessList.SelectedItem = row;
            foreach (var current in Model.Rows) current.SetSelected(ReferenceEquals(row, current));
            if (row is null && SelectedProcess is not null)
            {
                selectedIdentity = null;
                selectedRowKey = null;
                publisherGeneration++;
                PublisherText.Text = "";
            }
        }
        finally { restoringSelection = false; }
        RefreshInspector();
    }

    private ProcessSnapshot? SelectedProcess => selectedIdentity is { } identity ? Model.Snapshot?.Processes.FirstOrDefault(p => p.Identity == identity) : null;
    private async void DetailsClicked(object sender, RoutedEventArgs args) => await ShowDetailsAsync();
    private void RefreshInspector()
    {
        var process = SelectedProcess;
        if (process is null)
        {
            SelectedName.Text = selectedIdentity is null ? "Process inspector" : "Process no longer observed";
            SelectedIdentity.Text = "Select a current process to inspect its live measurements.";
            SelectedPath.Value = SelectedCommandLine.Text = SelectedParent.Value = SelectedMemory.Text = SelectedServices.Value = SelectedNetwork.Value = "—";
            SelectedRawName.Text = "";
            SelectedCommit.Text = SelectedCpu.Text = "—";
            SelectedIcon.Source = null;
            SelectedIcon.Tag = null;
            SelectedAvailability.Text = "";
            SelectedStatus.Text = selectedIdentity is null ? "No selection" : "Unavailable";
            SelectedOwner.Value = "—";
            DetailsButton.IsEnabled = false;
            PublisherText.Text = "";
            OpenLocation.IsEnabled = CopyPath.IsEnabled = CopyPid.IsEnabled = PublisherButton.IsEnabled = false;
            return;
        }
        SelectedName.Text = Model.Descriptions.GetCached(process) ?? process.Name;
        SelectedRawName.Text = process.Name;
        ToolTipService.SetToolTip(SelectedName, $"{process.Name}. Display name may use local file description; this is not verified publisher or application identity.");
        SelectedCommit.Text = Display.Bytes(process.PrivateBytes);
        SelectedCpu.Text = Display.Percent(process.CpuPercent);
        RefreshSelectedIcon(process);
        SelectedStatus.Text = "Observed";
        SelectedOwner.Value = process.UserName ?? "Unavailable";
        DetailsButton.IsEnabled = true;
        CopyPid.IsEnabled = true;
        SelectedIdentity.Text = $"Selected member · PID {process.Identity.Pid}";
        SelectedPath.Value = process.ExecutablePath ?? "Unavailable";
        SelectedCommandLine.Text = process.CommandLine ?? "Unavailable from this documented-API provider";
        var byPid = Model.Snapshot!.Processes.ToDictionary(p => p.Identity.Pid);
        var parent = ProcessGrouping.VerifiedParent(process, byPid);
        SelectedParent.Value = parent is null ? "Unverified or no longer observed" : $"{parent.Name} · PID {parent.Identity.Pid}";
        SelectedMemory.Text = $"Private commit: {Display.Bytes(process.PrivateBytes)}\nWorking set (includes shared): {Display.Bytes(process.WorkingSetBytes)}\nPrivate working set: {Display.Bytes(process.PrivateWorkingSetBytes)}";
        var rel = Model.Relationships;
        var fresh = rel is not null && Math.Abs((Model.Snapshot.CapturedAt - rel.CapturedAt).TotalSeconds) <= 10;
        SelectedServices.Value = !fresh || !process.Identity.IsStable ? "Unavailable or stale" : string.Join("\n", rel!.Services.Where(s => s.OwnerIdentity == process.Identity).Select(s => $"{s.DisplayName} ({s.Name})").DefaultIfEmpty("None observed"));
        SelectedNetwork.Value = !fresh || !process.Identity.IsStable ? "Unavailable or stale" : $"{rel!.Connections.Count(c => c.OwnerIdentity == process.Identity)} verified TCP/UDP entries";
        SelectedAvailability.Text = $"{process.ThreadCount?.ToString() ?? "—"} threads · {process.HandleCount?.ToString() ?? "—"} handles\n{process.UnavailableReason ?? (process.Identity.IsStable ? "Read-only observation. Some fields may be unavailable." : "Creation time unavailable; history and associations are not trusted.")}";
        var local = IsLocalFile(process.ExecutablePath);
        OpenLocation.IsEnabled = PublisherButton.IsEnabled = local;
        CopyPath.IsEnabled = process.ExecutablePath is not null;
    }

    private async void RefreshSelectedIcon(ProcessSnapshot process)
    {
        var key = (process.Identity, process.ExecutablePath);
        if (Equals(SelectedIcon.Tag, key)) return;
        SelectedIcon.Tag = key;
        SelectedIcon.Source = null;
        if (process.ExecutablePath is not { } path) return;
        try
        {
            var icon = await icons.LoadAsync(path, shutdown.Token);
            if (!shutdown.IsCancellationRequested && SelectedProcess?.Identity == process.Identity &&
                SelectedProcess.ExecutablePath == path && Equals(SelectedIcon.Tag, key)) SelectedIcon.Source = icon;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Keep the generic process icon. */ }
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
        CopyText(path);
    }
    private void CopyPidClicked(object sender, RoutedEventArgs args)
    {
        if (SelectedProcess is not { } process) return;
        CopyText(process.Identity.Pid.ToString(CultureInfo.InvariantCulture));
    }
    private void CopyText(string text)
    {
        try
        {
            var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data);
        }
        catch (Exception ex) { StatusText.Text = $"Copy unavailable: {ex.Message}"; }
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
        observationGeneration++;
        paused = !paused;
        if (paused) overviewData.BreakContinuity();
        RefreshOverview();
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
        var width = Root.ActualWidth;
        MemorySummaryColumn.Width = new GridLength(width < 1200 ? 1.4 : 1, GridUnitType.Star);
        var show = width >= 1040;
        Inspector.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = new GridLength(show ? width >= 1320 ? 320 : 280 : 0);
        DetailsButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TableSizeChanged(object sender, SizeChangedEventArgs args) =>
        ((Controls.ProcessColumns)Root.Resources["ProcessColumns"]).FitViewport(args.NewSize.Width - 2);

    private void UpdateCaptionColors()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray;
    }

    private void RefreshDiagnostics()
    {
        if (!ready || section is "Processes" or "Settings") return;
        if (section == "Overview") { RefreshOverview(); return; }
        var snapshot = Model.Snapshot;
        if (snapshot is null) { DetailsList.ItemsSource = new[] { "Waiting for the first real Windows sample…" }; return; }
        var mem = snapshot.Memory;
        var rows = new List<string>();
        switch (section)
        {
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
        var filtered = rows.Where(r => query.Length == 0 || r.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        DetailsList.ItemsSource = filtered.Length > 0 ? filtered : new[] { query.Length > 0 ? "No displayed observations match this search. Clear search to show this section." : "No observations are available for this section yet." };
        ChartCard.Visibility = section is "Memory" or "CPU" ? Visibility.Visible : Visibility.Collapsed;
        DrawHistory();
    }

    private void ChartSizeChanged(object sender, SizeChangedEventArgs args) => DrawHistory();
    private void DrawHistory()
    {
        if (!ready || section is not ("Memory" or "CPU")) return;
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
