using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private ProcessIdentity? lastAnimatedInspectorIdentity;
    private DateTimeOffset? selectedCapturedAt;
    private string? selectedRowKey;
    private bool restoringSelection;
    private ScrollViewer? processScrollViewer;
    private global::Windows.Foundation.Point? processPointer;
    private RowViewportAnchor? processAnchor;
    private bool processAnchorScheduled;
    private int processAnchorAttempts;
    private bool restoreProcessKeyboardFocus;
    private bool windowActive;
    private double lastScanMilliseconds;
    private volatile TaskCompletionSource refreshWake = NewWakeSignal();
    private static TaskCompletionSource NewWakeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<StartupItem> startup = Array.Empty<StartupItem>();
    private DateTimeOffset? startupCapturedAt;
    private DateTimeOffset? relationshipsCapturedAt;
    private string? startupError;
    private string? relationshipError;
    private int publisherGeneration;

    public MainWindow()
    {
        InitializeComponent();
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* Unsupported on older Windows/Server builds. */ }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowTitleBar);
        Root.ActualThemeChanged += (_, _) => { UpdateCaptionColors(); UpdateLiveStatusIndicator(); UpdateSectionChrome(section); };
        ProcessList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(ProcessKeyDown), true);
        ProcessList.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(ProcessPointerMoved), true);
        ProcessList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ProcessPointerMoved), true);
        ProcessList.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(ProcessPointerExited), true);
        ProcessList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => CancelProcessAnchor()), true);
        ProcessList.Loaded += (_, _) =>
        {
            if (processScrollViewer is not null) return;
            processScrollViewer = ProcessVisualDescendants(ProcessList).OfType<ScrollViewer>().FirstOrDefault();
            if (processScrollViewer is not null) processScrollViewer.ViewChanged += (_, _) => ScheduleProcessAnchorRestore();
        };
        ProcessList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => CancelProcessAnchor()), true);
        ProcessList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => CancelProcessAnchor()), true);
        Model.RowsRebuilding += ProcessRowsRebuilding;
        Model.RowsRebuilt += (_, _) =>
        {
            RestoreSelection();
            RestoreProcessViewport();
        };
        tray = new NativeTray(this);
        ready = true;
        InitializeOverview();
        InitializeTrace();
        DiagnosticsPanel.InspectRequested += InspectOverviewProcess;
        DiagnosticsPanel.ServiceRequested += InspectService;
        DiagnosticsPanel.InventoryRequested += ShowExtraInventory;
        CpuPanel.InspectRequested += InspectOverviewProcess;
        CpuPanel.InventoryRequested += ShowExtraInventory;
        GpuPanel.InspectRequested += InspectOverviewProcess;
        GpuPanel.InventoryRequested += ShowExtraInventory;
        DisksPanel.InspectRequested += InspectOverviewProcess;
        DisksPanel.InventoryRequested += ShowExtraInventory;
        NetworkPanel.InspectRequested += InspectOverviewProcess;
        NetworkPanel.InventoryRequested += ShowExtraInventory;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        ApplyMinimumWindowSize();
        Root.Loaded += (_, _) =>
        {
            ApplyMinimumWindowSize();
            UpdateLiveStatusIndicator();
            UpdateSectionChrome(section);
            Root.XamlRoot.Changed += (_, _) => ApplyMinimumWindowSize();
        };
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(Math.Min(1440, work.Width), Math.Min(920, work.Height)));
        AppWindow.Move(new global::Windows.Graphics.PointInt32(work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2), work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
        UpdateCaptionColors();
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) => { GlobalSearch.Focus(FocusState.Keyboard); args.Handled = true; };
        Root.KeyboardAccelerators.Add(accelerator);
        Activated += (_, args) =>
        {
            windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (windowActive) refreshWake.TrySetResult();
            polling ??= PollAsync();
        };
        Closed += async (_, _) =>
        {
            CloseStorage();
            TracePanel.Shutdown();
            CancelProcessAnchor();
            tray.Dispose();
            shutdown.Cancel();
            if (polling is not null) await polling;
            if (descriptionRefresh is not null) await descriptionRefresh;
            monitor.Dispose();
            relationships.Dispose();
            systemCounters?.Dispose();
        };
    }

    private void ApplyMinimumWindowSize()
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter) return;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var minimum = WindowSizing.MinimumPhysicalSize(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)), work.Width, work.Height);
        if (presenter.PreferredMinimumWidth != minimum.Width) presenter.PreferredMinimumWidth = minimum.Width;
        if (presenter.PreferredMinimumHeight != minimum.Height) presenter.PreferredMinimumHeight = minimum.Height;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

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
                    var scanStart = Stopwatch.GetTimestamp();
                    var next = await monitor.CaptureAsync(shutdown.Token);
                    lastScanMilliseconds = Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds;
                    if (shutdown.IsCancellationRequested) break;
                    if (paused || generation != observationGeneration) continue;
                    if (Model.Snapshot is { } previous && next.CapturedAt <= previous.CapturedAt)
                        throw new InvalidOperationException("An out-of-order observation was ignored.");
                    Model.Apply(next);
                    selectedHistory.Observe(next);
                    lifecycle.Observe(next);
                    overviewData.Observe(next);
                    StartDescriptionRefresh();
                    overviewStale = false;
                    tray.Update(next);
                    cycle++;
                    if ((SectionConsumesRelationships(section) || (section == "Processes" && selectedIdentity is not null)) &&
                        (relationshipsCapturedAt is null || next.CapturedAt - relationshipsCapturedAt.Value > TimeSpan.FromSeconds(6)))
                    {
                        try
                        {
                            var related = await relationships.CaptureAsync(next.Processes, shutdown.Token);
                            if (shutdown.IsCancellationRequested) break;
                            if (paused || generation != observationGeneration) continue;
                            Model.Relationships = related;
                            relationshipsCapturedAt = next.CapturedAt;
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
                    if (SectionConsumesStartup(section) &&
                        (startupCapturedAt is null || next.CapturedAt - startupCapturedAt.Value > TimeSpan.FromSeconds(60)))
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
                await WaitForNextCycle(shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (paused || generation != observationGeneration) continue;
                overviewData.BreakContinuity();
                Model.History.BreakContinuity();
                selectedHistory.BreakContinuity();
        lifecycle.BreakContinuity();
                overviewStale = true;
                RefreshDiagnostics();
                StatusText.Text = $"Refresh failed; previous data is stale: {ex.Message}";
                try { await Task.Delay(2000, shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Visible windows sample every 2 seconds; a minimized window drops to 6
    // seconds — comfortably below the 10-second history continuity threshold —
    // and restoring the window triggers an immediate out-of-cycle refresh.
    private static readonly TimeSpan VisibleCadence = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimizedCadence = TimeSpan.FromSeconds(6);

    private bool IsWindowMinimized()
    {
        try
        {
            return AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized;
        }
        catch { return false; }
    }

    private async Task WaitForNextCycle(CancellationToken cancellationToken)
    {
        // Capture the current signal once: a wake that arrives before or during
        // the wait completes it exactly once, and the completed instance is
        // swapped out so later wakes re-arm the next wait. No waiter accumulation.
        var signal = refreshWake;
        var cadence = IsWindowMinimized() ? MinimizedCadence : VisibleCadence;
        var delay = Task.Delay(cadence, cancellationToken);
        var finished = await Task.WhenAny(delay, signal.Task).ConfigureAwait(false);
        if (finished == signal.Task)
        {
            Interlocked.CompareExchange(ref refreshWake, NewWakeSignal(), signal);
            GC.KeepAlive(delay);
        }
    }

    private void RefreshSettings()
    {
        if (Model.Snapshot is not { } snapshot) return;
        var self = snapshot.Processes.FirstOrDefault(p => p.Identity.Pid == Environment.ProcessId);
        OverheadCpu.Text = Display.Percent(self?.CpuPercent);
        OverheadPrivate.Text = Display.Bytes(self?.PrivateBytes);
        OverheadWorkingSet.Text = Display.Bytes(self?.WorkingSetBytes);
        OverheadThreadsHandles.Text = self is null ? "—"
            : $"{(self.ThreadCount?.ToString() ?? "—")} / {(self.HandleCount?.ToString() ?? "—")}";
        OverheadScanCost.Text = lastScanMilliseconds > 0 ? $"{lastScanMilliseconds:F1} ms" : "—";
        OverheadCadence.Text = IsWindowMinimized() ? "6 s (minimized)" : "2 s";
    }

    // Endpoint/service and startup scans only run while a section that displays
    // them is visible; Hidden items reuses whatever observation already exists.
    // Collection resumes within one refresh when the user opens a consuming section.
    private static bool SectionConsumesRelationships(string value) =>
        value is "Overview" or "Network" or "Services" or "Startup" or "Insights";

    private static bool SectionConsumesStartup(string value) =>
        value is "Startup" or "Insights";

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
            Model.Rebuild(live: true);
            RefreshInspector();
            RefreshOverview();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || args.SelectedItem is not NavigationViewItem item) return;
        section = item.Tag?.ToString() ?? "Processes";
        PageTitle.Text = section == "Hidden" ? "Hidden & suspicious" : section;
        PageSubtitle.Text = section switch
        {
            "Overview" => "Live resource use, top consumers, and system insights.",
            "Storage" => "Disk-space usage, developer artifacts and AI tool data — metadata only.",
            "Processes" => "See what's using your system and explore related processes.",
            "Memory" => "Physical memory, private commit and reclaimable categories are different views—not additive totals.",
            "Network" => "Observed TCP connections and UDP endpoints. No inferred bandwidth, DNS or geography.",
            "Services" => "Read-only Windows service inventory with verified process-lifetime associations.",
            "Insights" => "Local, deterministic explanations supported by observed measurements.",
            "Hidden" => "Read-only cross-view checks and local leads. Click Run to scan.",
            "Trace" => "Opt-in, read-only activity recording via ETW. Requires administrator. Nothing is saved unless you export.",
            "Startup" => "Observed startup entries. Presence does not measure startup impact.",
            _ => "Read-only diagnostics from your local Windows system. Unavailable measurements are explicit."
        };
        ProcessesPanel.Visibility = section == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        OverviewPanel.Visibility = section == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        HiddenPanel.Visibility = section == "Hidden" ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = section == "Storage" ? Visibility.Visible : Visibility.Collapsed; OnStorageNavigated();
        TracePanel.Visibility = section == "Trace" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        CpuPanel.Visibility = section == "CPU" ? Visibility.Visible : Visibility.Collapsed;
        GpuPanel.Visibility = section == "GPU" ? Visibility.Visible : Visibility.Collapsed;
        DisksPanel.Visibility = section == "Disks" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPanel.Visibility = section == "Network" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = section is "Processes" or "Overview" or "CPU" or "GPU" or "Disks" or "Network" or "Settings" or "Trace" or "Storage" or "Hidden" ? Visibility.Collapsed : Visibility.Visible;
        UIElement activePanel = section switch
        {
            "Processes" => ProcessesPanel,
            "Overview" => OverviewPanel,
            "CPU" => CpuPanel,
            "GPU" => GpuPanel,
            "Disks" => DisksPanel,
            "Network" => NetworkPanel,
            "Hidden" => HiddenPanel,
            "Storage" => StoragePanel,
            "Trace" => TracePanel,
            "Settings" => SettingsPanel,
            _ => DiagnosticsPanel
        };
        UpdateSectionChrome(section);
        Motion.AnimateSectionEntrance(activePanel);
        if (section == "Processes") { Motion.AnimateStaggeredChildren(ProcessSummaryGrid); ProcessSearch.Text = GlobalSearch.Text; }
        else if (section == "Overview") OverviewPanel.AnimateEntrance();
        else if (section == "Hidden") HiddenPanel.AnimateEntrance();
        else if (section == "CPU") CpuPanel.AnimateEntrance();
        else if (section == "GPU") GpuPanel.AnimateEntrance();
        else if (section == "Disks") DisksPanel.AnimateEntrance();
        else if (section == "Network") NetworkPanel.AnimateEntrance();
        else if (section is "Memory" or "Services" or "Startup" or "Insights") DiagnosticsPanel.AnimateEntrance();
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
        (Button Source, RoutedEventHandler Handler)? requestedAction = null;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        void ResizeDetails(object? sender, SizeChangedEventArgs? args)
        {
            scroll.Width = Math.Min(520, Math.Max(0, Root.ActualWidth - 100));
            scroll.MaxHeight = Math.Max(120, Root.ActualHeight - 240);
        }
        ResizeDetails(null, null);
        Root.SizeChanged += ResizeDetails;
        try
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock { Text = "Read-only native inspection", Style = (Style)Application.Current.Resources["SectionLabel"] });
            var actions = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            content.Children.Add(actions);
            content.Children.Add(new TextBlock { Text = $"{SelectedRawName.Text}\n{SelectedStatus.Text}\n{SelectedIdentity.Text}\n{SelectedAvailability.Text}\n\nExecutable path\n{SelectedPath.Value}\n\nCommand line\n{SelectedCommandLine.Text}\n\nOwnership\n{SelectedOwner.Value}\n\nParent process\n{SelectedParent.Value}\n\nMemory\n{SelectedMemory.Text}\n\nAssociated services\n{SelectedServices.Value}\n\nNetwork endpoints\n{SelectedNetwork.Value}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            scroll.Content = content;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = SelectedName.Text, Content = scroll, PrimaryButtonText = "Copy PID", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close };
            foreach (var (source, handler) in new (Button, RoutedEventHandler)[]
                { (ModulesButton, InspectionClicked), (RegionsButton, InspectionClicked), (ThreadsButton, ThreadsClicked),
                  (HistoryButton, HistoryClicked), (SecurityButton, SecurityClicked), (HandlesButton, HandlesClicked), (ActivityButton, ActivityClicked) })
            {
                var button = new Button { Content = source.Content, Style = source.Style, HorizontalAlignment = HorizontalAlignment.Stretch };
                button.SetBinding(Control.IsEnabledProperty, new Microsoft.UI.Xaml.Data.Binding { Source = source, Path = new PropertyPath(nameof(source.IsEnabled)) });
                button.SetBinding(UIElement.VisibilityProperty, new Microsoft.UI.Xaml.Data.Binding { Source = source, Path = new PropertyPath(nameof(source.Visibility)) });
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, source.Content.ToString());
                ToolTipService.SetToolTip(button, ToolTipService.GetToolTip(source));
                button.Click += (_, _) =>
                {
                    requestedAction = (source, handler);
                    dialog.Hide();
                };
                actions.Children.Add(button);
            }
            void WrapActions(double width)
            {
                var columns = Math.Max(1, (int)((width + 6) / 150));
                if (columns == actions.ColumnDefinitions.Count) return;
                actions.ColumnDefinitions.Clear();
                actions.RowDefinitions.Clear();
                for (var i = 0; i < columns; i++) actions.ColumnDefinitions.Add(new ColumnDefinition());
                for (var i = 0; i < (actions.Children.Count + columns - 1) / columns; i++)
                    actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var i = 0; i < actions.Children.Count; i++)
                {
                    Grid.SetColumn((FrameworkElement)actions.Children[i], i % columns);
                    Grid.SetRow((FrameworkElement)actions.Children[i], i / columns);
                }
            }
            WrapActions(scroll.Width);
            actions.SizeChanged += (_, args) => WrapActions(args.NewSize.Width);
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                CopyText(process.Identity.Pid.ToString(CultureInfo.InvariantCulture));
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { if (!shutdown.IsCancellationRequested) StatusText.Text = $"Details unavailable: {ex.Message}"; }
        finally
        {
            Root.SizeChanged -= ResizeDetails;
            detailsOpen = false;
        }
        if (!shutdown.IsCancellationRequested && requestedAction is { } requested && requested.Source.IsEnabled && requested.Source.Visibility == Visibility.Visible)
            requested.Handler(requested.Source, new RoutedEventArgs());
    }
    private async void ProcessKeyDown(object sender, KeyRoutedEventArgs args)
    {
        CancelProcessAnchor();
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
        CancelProcessAnchor();
        foreach (var current in Model.Rows) current.SetSelected(ReferenceEquals(ProcessList.SelectedItem, current));
        if (ProcessList.SelectedItem is ProcessRow row)
        {
            if (selectedIdentity != row.Identity) { publisherGeneration++; PublisherText.Text = ""; }
            selectedIdentity = row.Identity;
            selectedCapturedAt = Model.Snapshot?.CapturedAt;
            selectedRowKey = row.RowKey;
            if (row.Identity is { } identity && Model.Snapshot is { } snapshot) selectedHistory.Track(identity, snapshot);
        }
        else { selectedIdentity = null; selectedCapturedAt = null; selectedRowKey = null; }
        RefreshInspector();
    }

    private static IEnumerable<DependencyObject> ProcessVisualDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in ProcessVisualDescendants(child)) yield return descendant;
        }
    }

    private ListViewItem? FocusedProcessContainer()
    {
        var current = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        ListViewItem? container = null;
        while (current is not null)
        {
            if (ReferenceEquals(current, ProcessList)) return container;
            if (current is ListViewItem item) container = item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ProcessPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(ProcessList).Position;
        if (processPointer is not { } previous || Math.Abs(previous.X - point.X) >= 0.5 || Math.Abs(previous.Y - point.Y) >= 0.5)
            CancelProcessAnchor();
        processPointer = point;
    }

    private void ProcessPointerExited(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(ProcessList).Position;
        processPointer = point.X >= 0 && point.X < ProcessList.ActualWidth && point.Y >= 0 && point.Y < ProcessList.ActualHeight ? point : null;
        if (processPointer is null) CancelProcessAnchor();
    }

    private bool HasProcessKeyboardFocus() => FocusedProcessContainer() is { } focused &&
        (focused.FocusState == FocusState.Keyboard ||
         FocusManager.GetFocusedElement(Root.XamlRoot) is Control { FocusState: FocusState.Keyboard });

    private void ProcessRowsRebuilding(object? sender, EventArgs args)
    {
        restoreProcessKeyboardFocus = windowActive && HasProcessKeyboardFocus();
        if (!Model.IsLiveRebuild)
        {
            CancelProcessAnchor();
            return;
        }
        if (processAnchor is not null || processScrollViewer is not { } viewer || ProcessList.Visibility != Visibility.Visible) return;
        var visible = ProcessVisualDescendants(ProcessList).OfType<ListViewItem>()
            .Where(item => item.Content is ProcessRow)
            .Select(item => (Item: item, Y: item.TransformToVisual(viewer).TransformPoint(new()).Y))
            .Where(entry => entry.Y < viewer.ViewportHeight && entry.Y + entry.Item.ActualHeight > 0)
            .OrderBy(entry => entry.Y).ToArray();
        if (visible.Length == 0) return;
        ListViewItem? anchor = null;
        if (processPointer is { } pointer && pointer.X >= 0 && pointer.X < ProcessList.ActualWidth &&
            pointer.Y >= 0 && pointer.Y < ProcessList.ActualHeight)
            anchor = visible.Select(entry => entry.Item).FirstOrDefault(item =>
            {
                var origin = item.TransformToVisual(ProcessList).TransformPoint(new());
                return pointer.X >= origin.X && pointer.X < origin.X + item.ActualWidth &&
                    pointer.Y >= origin.Y && pointer.Y < origin.Y + item.ActualHeight;
            });
        anchor ??= visible.Select(entry => entry.Item).FirstOrDefault(item => ReferenceEquals(item.Content, ProcessList.SelectedItem));
        if (anchor is null && viewer.VerticalOffset == 0) return;
        anchor ??= visible[0].Item;
        processAnchor = RowViewportAnchoring.Capture(Model.Rows, row => row.RowKey, row => row.RowHeight,
            ((ProcessRow)anchor.Content).RowKey, viewer.VerticalOffset, anchor.TransformToVisual(viewer).TransformPoint(new()).Y);
    }

    private void RestoreProcessViewport()
    {
        if (restoreProcessKeyboardFocus || processAnchor is not null) ProcessList.UpdateLayout();
        if (restoreProcessKeyboardFocus && windowActive && !HasProcessKeyboardFocus() &&
            ProcessList.SelectedItem is ProcessRow selected && ProcessList.ContainerFromItem(selected) is ListViewItem selectedContainer)
            selectedContainer.Focus(FocusState.Keyboard);
        restoreProcessKeyboardFocus = false;
        processAnchorAttempts = 0;
        if (processAnchor is { } saved && processScrollViewer is { } viewer)
        {
            var offset = RowViewportAnchoring.RestoreOffset(saved, Model.Rows, row => row.RowKey, row => row.RowHeight, viewer.ScrollableHeight);
            if (offset is { } target) viewer.ChangeView(null, target, null, disableAnimation: true);
            else CancelProcessAnchor();
        }
        ScheduleProcessAnchorRestore();
    }

    private void ScheduleProcessAnchorRestore()
    {
        if (processAnchor is null || processAnchorScheduled) return;
        if (processAnchorAttempts >= 8) { CancelProcessAnchor(); return; }
        processAnchorScheduled = true;
        CompositionTarget.Rendering += RestoreProcessAnchorAfterLayout;
    }

    private void CancelProcessAnchor()
    {
        processAnchor = null;
        if (!processAnchorScheduled) return;
        CompositionTarget.Rendering -= RestoreProcessAnchorAfterLayout;
        processAnchorScheduled = false;
    }

    private void RestoreProcessAnchorAfterLayout(object? sender, object args)
    {
        CompositionTarget.Rendering -= RestoreProcessAnchorAfterLayout;
        processAnchorScheduled = false;
        if (processAnchor is not { } saved || processScrollViewer is not { } viewer ||
            Model.Rows.FirstOrDefault(row => row.RowKey == saved.Key) is not { } row)
        {
            CancelProcessAnchor();
            return;
        }
        processAnchorAttempts++;
        ProcessList.UpdateLayout();
        if (ProcessList.ContainerFromItem(row) is not ListViewItem container)
        {
            ScheduleProcessAnchorRestore();
            return;
        }
        var delta = container.TransformToVisual(viewer).TransformPoint(new()).Y - saved.Y;
        if (Math.Abs(delta) < 0.5)
        {
            CancelProcessAnchor();
            return;
        }
        var target = Math.Clamp(viewer.VerticalOffset + delta, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5) CancelProcessAnchor();
        else
        {
            viewer.ChangeView(null, target, null, disableAnimation: true);
            ScheduleProcessAnchorRestore();
        }
    }

    private void RestoreSelection()
    {
        if (selectedRowKey is null) return;
        var expired = selectedIdentity is { IsStable: false } && Model.Snapshot?.CapturedAt != selectedCapturedAt;
        var row = expired ? null : Model.Rows.FirstOrDefault(r => r.RowKey == selectedRowKey);
        restoringSelection = true;
        try
        {
            if (!ReferenceEquals(ProcessList.SelectedItem, row)) ProcessList.SelectedItem = row;
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
        HistoryButton.IsEnabled = selectedHistory.HasHistory;
        SecurityButton.IsEnabled = HandlesButton.IsEnabled = ActivityButton.IsEnabled = ModulesButton.IsEnabled = RegionsButton.IsEnabled = ThreadsButton.IsEnabled = selectedIdentity is { IsStable: true } identity && Model.Snapshot?.Processes.Any(p => p.Identity == identity) == true;
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
        if (lastAnimatedInspectorIdentity != process.Identity)
        {
            lastAnimatedInspectorIdentity = process.Identity;
            Motion.AnimateInspectorReveal(Inspector);
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
            selectedHistory.BreakContinuity();
        lifecycle.BreakContinuity();
        }
        RefreshDiagnostics();
        if (Model.Snapshot is { } snapshot) tray.Update(snapshot, paused);
        PauseButton.Content = paused ? "Resume" : "Pause";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseButton, paused ? "Resume live refresh" : "Pause live refresh");
        UpdateLiveStatusIndicator();
        StatusText.Text = paused ? "Paused · displayed measurements are frozen" : "Resuming live local monitoring…";
    }

    private Brush? ResolveThemeBrush(string key)
    {
        var themeKey = Root.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) && themeObj is ResourceDictionary themeDict &&
                themeDict.TryGetValue(key, out var val) && val is Brush b)
                return b;
        }
        return Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Brush fb ? fb : null;
    }

    private void UpdateSectionChrome(string currentSection)
    {
        var accentKey = currentSection switch
        {
            "Memory" => "StatusLiveBrush",
            "Disks" or "Storage" => "StatusWarnBrush",
            "Network" or "GPU" => "ChartVioletBrush",
            "Hidden" or "Trace" => "StatusDangerBrush",
            _ => "InteractionAccent"
        };
        if (ResolveThemeBrush(accentKey) is { } barBrush) PageAccentBar.Background = barBrush;
        PageModeText.Text = currentSection switch
        {
            "Overview" or "Processes" or "Memory" or "CPU" or "GPU" or "Disks" or "Network" => "Live · 2.0s",
            "Storage" => "Metadata only",
            "Hidden" => "Cross-view leads",
            "Trace" => "Opt-in ETW",
            "Settings" or "Help" => "Local · Read-only",
            _ => "Read-only"
        };
        MotionStatusText.Text = Motion.IsEnabled
            ? "Enabled — Fluent section, inspector, and live-beacon animations active."
            : "Reduced motion active — animations snap instantly to match Windows settings.";
    }

    private void UpdateLiveStatusIndicator()
    {
        var surfaceKey = paused ? "StatusWarnSurface" : "StatusLiveSurface";
        var brushKey = paused ? "StatusWarnBrush" : "StatusLiveBrush";
        if (ResolveThemeBrush(surfaceKey) is { } surfaceBrush)
            LiveStatusPill.Background = surfaceBrush;
        if (ResolveThemeBrush(brushKey) is { } accentBrush)
        {
            LiveHalo.Fill = accentBrush;
            LiveDot.Fill = accentBrush;
            LiveStatusLabel.Foreground = accentBrush;
            FooterLiveDot.Fill = accentBrush;
        }
        LiveStatusLabel.Text = paused ? "Paused" : "Live · 2.0s";
        if (paused) Motion.StopLivePulse(LiveHalo);
        else Motion.StartLivePulse(LiveHalo);
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

    private void TableSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width == args.PreviousSize.Width) return;
        ((Controls.ProcessColumns)Root.Resources["ProcessColumns"]).FitViewport(args.NewSize.Width - 2);
    }

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
        if (!ready || section is "Processes") return;
        if (section == "Settings") { RefreshSettings(); return; }
        if (section == "Overview") { RefreshOverview(); return; }
        if (section == "CPU") { RefreshCpu(); return; }
        if (section == "GPU") { RefreshGpu(); return; }
        if (section == "Disks") { RefreshDisks(); return; }
        if (section == "Network") { RefreshNetwork(); return; }
        if (Model.Snapshot is not { } snapshot) return;
        var startupStatus = startupError is not null ? startupError + " Retained entries are stale." :
            startupCapturedAt is { } captured ? $"Last inventory {captured.LocalDateTime:T}; refreshed approximately once per minute." : "Inventory unavailable.";
        DiagnosticsPanel.Update(section, snapshot, Model.Relationships, startup, Model.History,
            GlobalSearch.Text ?? "", Model.Descriptions.GetCached, startupStatus, paused, overviewStale);
    }

    private bool hardwareCountersLoading;
    private void RefreshCpu()
    {
        if (Model.Snapshot is not { } snapshot) return;
        CpuPanel.Update(snapshot, Model.History, GlobalSearch.Text ?? "", Model.Descriptions.GetCached, paused, overviewStale);
        TriggerHardwareCountersIfNeeded();
    }

    private void RefreshGpu()
    {
        if (Model.Snapshot is not { } snapshot) return;
        GpuPanel.Update(snapshot, GlobalSearch.Text ?? "", Model.Descriptions.GetCached);
        TriggerHardwareCountersIfNeeded();
    }

    private void RefreshDisks()
    {
        if (Model.Snapshot is not { } snapshot) return;
        DisksPanel.Update(snapshot, GlobalSearch.Text ?? "", Model.Descriptions.GetCached);
        TriggerHardwareCountersIfNeeded();
    }

    private void RefreshNetwork()
    {
        if (Model.Snapshot is not { } snapshot) return;
        NetworkPanel.Update(snapshot, Model.Relationships, GlobalSearch.Text ?? "");
        TriggerHardwareCountersIfNeeded();
    }

    private void TriggerHardwareCountersIfNeeded()
    {
        if (!paused && !hardwareCountersLoading)
            _ = RefreshHardwareCountersAsync();
    }

    private async Task RefreshHardwareCountersAsync()
    {
        hardwareCountersLoading = true;
        try
        {
            var result = await CaptureCountersAsync(shutdown.Token);
            if (!shutdown.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    CpuPanel.UpdateCores(result.Cpu);
                    GpuPanel.UpdateCounters(result.Gpu);
                    DisksPanel.UpdateCounters(result.Disks);
                    NetworkPanel.UpdateCounters(result.Network);
                    if (Model.Snapshot is { } snap)
                    {
                        if (section == "GPU") GpuPanel.Update(snap, GlobalSearch.Text ?? "", Model.Descriptions.GetCached);
                        else if (section == "Disks") DisksPanel.Update(snap, GlobalSearch.Text ?? "", Model.Descriptions.GetCached);
                        else if (section == "Network") NetworkPanel.Update(snap, Model.Relationships, GlobalSearch.Text ?? "");
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { hardwareCountersLoading = false; }
    }
}
