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
    private Task? descriptionRefresh;
    private bool paused;
    private bool ready;
    private bool detailsOpen;
    private string section = "Processes";
    private ProcessIdentity? selectedIdentity;
    private DateTimeOffset? selectedCapturedAt;
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
        DiagnosticsPanel.InspectRequested += InspectOverviewProcess;
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
            if (descriptionRefresh is not null) await descriptionRefresh;
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
                    StartDescriptionRefresh();
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
                Model.History.BreakContinuity();
                overviewStale = true;
                RefreshDiagnostics();
                StatusText.Text = $"Refresh failed; previous data is stale: {ex.Message}";
                try { await Task.Delay(2000, shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void StartDescriptionRefresh()
    {
        if (shutdown.IsCancellationRequested || descriptionRefresh is { IsCompleted: false }) return;
        descriptionRefresh = RefreshDescriptionsAsync(shutdown.Token);
    }

    private async Task RefreshDescriptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await Model.Descriptions.RefreshAsync(cancellationToken) || cancellationToken.IsCancellationRequested) return;
            Model.Rebuild();
            RefreshInspector();
            RefreshOverview();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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
            selectedCapturedAt = Model.Snapshot?.CapturedAt;
            selectedRowKey = row.RowKey;
        }
        else { selectedIdentity = null; selectedCapturedAt = null; selectedRowKey = null; }
        RefreshInspector();
    }

    private void RestoreSelection()
    {
        if (selectedRowKey is null) return;
        var expired = selectedIdentity is { IsStable: false } && Model.Snapshot?.CapturedAt != selectedCapturedAt;
        var row = expired ? null : Model.Rows.FirstOrDefault(r => r.RowKey == selectedRowKey);
        restoringSelection = true;
        try
        {
            ProcessList.SelectedItem = row;
            foreach (var current in Model.Rows) current.SetSelected(ReferenceEquals(row, current));
            if (expired || row is null && SelectedProcess is not null)
            {
                selectedIdentity = null;
                selectedCapturedAt = null;
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
        if (process.Identity.CreationTimeFileTime is long created && created > 0)
        {
            try { SelectedIdentity.Text += $"\nStarted {DateTimeOffset.FromFileTime(created).LocalDateTime:g}"; }
            catch (ArgumentOutOfRangeException) { SelectedIdentity.Text += "\nStart time unavailable"; }
        }
        SelectedPath.Value = process.ExecutablePath ?? "Unavailable";
        SelectedCommandLine.Text = process.CommandLine ?? "Unavailable from this documented-API provider";
        var byPid = Model.Snapshot!.Processes.ToDictionary(p => p.Identity.Pid);
        var parent = ProcessGrouping.VerifiedParent(process, byPid);
        SelectedParent.Value = parent is null ? "Unverified or no longer observed" : $"{parent.Name} · PID {parent.Identity.Pid}";
        SelectedMemory.Text = $"Private commit: {Display.Bytes(process.PrivateBytes)}\nWorking set (includes shared): {Display.Bytes(process.WorkingSetBytes)}\nPrivate working set: {Display.Bytes(process.PrivateWorkingSetBytes)}";
        SelectedMemory.Text += $"\nPeak working set: {Display.Bytes(process.PeakWorkingSetBytes)}\nPage faults (cumulative, not just hard faults): {process.PageFaultCount?.ToString("N0") ?? "—"}";
        SelectedMemory.Text += $"\nCPU kernel / user seconds (cumulative): {CpuSeconds(process.KernelCpuTimeTicks)} / {CpuSeconds(process.UserCpuTimeTicks)}";
        SelectedMemory.Text += $"\nI/O operations read / write / other (cumulative): {process.ReadOperationCount?.ToString("N0") ?? "—"} / {process.WriteOperationCount?.ToString("N0") ?? "—"} / {process.OtherOperationCount?.ToString("N0") ?? "—"}\nOther transfer bytes (cumulative): {Display.Bytes(process.OtherTransferBytes)}";
        var rel = Model.Relationships;
        var fresh = rel is not null && Math.Abs((Model.Snapshot.CapturedAt - rel.CapturedAt).TotalSeconds) <= 10;
        SelectedServices.Value = !fresh || !process.Identity.IsStable ? "Unavailable or stale" : string.Join("\n", rel!.Services.Where(s => s.OwnerIdentity == process.Identity).Select(s => $"{s.DisplayName} ({s.Name})").DefaultIfEmpty("None observed"));
        SelectedNetwork.Value = !fresh || !process.Identity.IsStable ? "Unavailable or stale" : $"{rel!.Connections.Count(c => c.OwnerIdentity == process.Identity)} verified TCP/UDP entries";
        SelectedAvailability.Text = $"{process.ThreadCount?.ToString() ?? "—"} threads · {process.HandleCount?.ToString() ?? "—"} handles\nSession {process.SessionId?.ToString() ?? "—"} · SID {process.UserSid ?? "Unavailable"}\n{process.UnavailableReason ?? (process.Identity.IsStable ? "Read-only observation. Some fields may be unavailable." : "Creation time unavailable; history and associations are not trusted.")}";
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

    private static string CpuSeconds(ulong? ticks) => ticks is { } value ? $"{value / 10_000_000d:F2}" : "—";

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
        if (paused)
        {
            overviewData.BreakContinuity();
            Model.History.BreakContinuity();
        }
        RefreshOverview();
        if (Model.Snapshot is { } snapshot) tray.Update(snapshot, paused);
        PauseButton.Content = paused ? "Resume" : "Pause";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseButton, paused ? "Resume live refresh" : "Pause live refresh");
        StatusText.Text = paused ? "Paused · displayed measurements are frozen" : "Resuming live local monitoring…";
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        Root.RequestedTheme = ThemeSelector.SelectedIndex switch { 1 => ElementTheme.Dark, 2 => ElementTheme.Default, _ => ElementTheme.Light };
        RefreshDiagnostics();
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
        if (Model.Snapshot is not { } snapshot) return;
        var startupStatus = startupError is not null ? startupError + " Retained entries are stale." :
            startupCapturedAt is { } captured ? $"Last inventory {captured.LocalDateTime:T}; refreshed approximately once per minute." : "Inventory unavailable.";
        DiagnosticsPanel.Update(section, snapshot, Model.Relationships, startup, Model.History,
            GlobalSearch.Text ?? "", Model.Descriptions.GetCached, startupStatus, paused, overviewStale);
    }
}
