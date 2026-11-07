using System.Collections;
using System.Globalization;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;
using SystemExplorer.Windows;

namespace SystemExplorer.App.Views;

public sealed partial class TraceView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly bool administrator;
    private WindowsActivityTraceRecorder? recorder;
    private Task<WindowsActivityTraceRecorder>? starting;
    private TraceResult? result;
    private TraceFilter appliedFilter = new();
    private Func<IReadOnlyList<TraceProcessSeed>> seeds = () => [];
    private Func<ProcessIdentity, bool> isCurrent = _ => false;
    private Action<ProcessIdentity>? inspect;
    private nint owner;
    private CancellationTokenSource? filterCancellation;
    private int queryGeneration, summaryGeneration;
    private int selectedProcess = -1;
    private bool initialized, busy, querying, filterFailed, exporting, closing, rebuilding, treeMode;
    private ContentDialog? summaryDialog;

    public TraceView()
    {
        InitializeComponent();
        using var identity = WindowsIdentity.GetCurrent();
        administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        AdminNotice.IsOpen = !administrator;
        Limitations.Text = TraceExport.Limitations;
        timer.Tick += (_, _) => { if (busy) RefreshLive(); else RefreshInspect(); };
        initialized = true;
        UpdateControls();
    }

    public void Initialize(nint owner, Func<IReadOnlyList<TraceProcessSeed>> seeds, Func<ProcessIdentity, bool> isCurrent, Action<ProcessIdentity> inspect)
    { this.owner = owner; this.seeds = seeds; this.isCurrent = isCurrent; this.inspect = inspect; }

    public void Shutdown()
    {
        closing = true; timer.Stop(); filterCancellation?.Cancel();
        var active = recorder ?? starting?.GetAwaiter().GetResult();
        active?.Dispose();
    }

    private void UpdateControls()
    {
        StartButton.IsEnabled = administrator && !busy && !exporting;
        StopButton.IsEnabled = busy && recorder is not null;
        ClearButton.IsEnabled = !busy && !exporting;
        Setup.IsEnabled = !busy;
        CsvButton.IsEnabled = JsonButton.IsEnabled = result is not null && !busy && !querying && !filterFailed && !exporting;
        Timeline.IsEnabled = !querying;
        ConfigureLayout();
    }

    private async void StartClicked(object sender, RoutedEventArgs args)
    {
        if (busy || !administrator) return;
        TraceCategories categories = TraceCategories.None;
        var checks = new[] { Processes, Threads, Images, Files, Registry, Network };
        var flags = new[] { TraceCategories.Process, TraceCategories.Thread, TraceCategories.Image, TraceCategories.File, TraceCategories.Registry, TraceCategories.Network };
        for (int i = 0; i < checks.Length; i++) if (checks[i].IsChecked == true) categories |= flags[i];
        if (categories == TraceCategories.None) { Notice.Text = "Choose at least one activity category."; return; }
        var options = new TraceOptions(categories, TimeSpan.FromSeconds(new[] { 30, 60, 120, 300, 600 }[Duration.SelectedIndex]), new[] { 100000, 200000, 500000 }[EventCap.SelectedIndex], ExcludeSelf.IsOn);
        var seed = seeds();
        recorder?.Dispose(); recorder = null; starting = null;
        ResetView(); busy = true; Setup.IsExpanded = false; UpdateControls();
        CaptureStatus.Text = "Starting private ETW recording…";
        try
        {
            starting = Task.Run(() => WindowsActivityTraceRecorder.StartAsync(options, seed));
            recorder = await starting;
            if (closing) return;
            UpdateControls(); RefreshLive(); timer.Start();
            var completed = await recorder.Completion;
            timer.Stop();
            if (closing) return;
            result = completed; busy = false;
            var reasons = new List<string>();
            if (completed.EventsLost > 0) reasons.Add("ETW events lost");
            if (completed.BufferDropped > 0) reasons.Add("ring buffer dropped oldest events");
            if (completed.CapsHit) reasons.Add("bounded map, process, string or summary cap hit");
            if (completed.Malformed > 0) reasons.Add("malformed events skipped");
            CaptureStatus.Text = StatusLabel(completed.Status) + (reasons.Count > 0 ? " · " + string.Join(" · ", reasons) : "");
            if (completed.Status is TraceStatus.Failed or TraceStatus.RequiresAdministrator) CaptureStatus.Text += " · " + completed.Detail;
            Counters.Text = CounterText(completed.StoppedUtc - completed.StartedUtc, completed.EventsSeen, completed.Events.Count, completed.ProcessesStarted, completed.EventsLost, completed.BufferDropped, completed.UnresolvedNames);
            Limitations.Text = $"{StatusLabel(completed.Status)}: {completed.Detail}\nMalformed skipped: {completed.Malformed:N0} · Excluded own events: {completed.ExcludedSelf:N0} · Caps hit: {completed.CapsHit}\n" + TraceExport.Limitations;
            Notice.Text = string.IsNullOrWhiteSpace(completed.Detail) ? "Frozen recording. Select an event or process for its retained-event summary." : completed.Detail;
            rebuilding = true;
            ProcessPicker.ItemsSource = new[] { "All processes" }.Concat(completed.Processes.Select(p => $"{p.ImageName ?? "(name unavailable)"} ({p.Pid}) · #{p.Index}")).ToArray();
            ProcessPicker.SelectedIndex = 0;
            rebuilding = false;
            Filters.Visibility = ViewTabs.Visibility = Visibility.Visible;
            Summary.Visibility = completed.Processes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var rows = await Task.Run(() =>
            {
                var counts = new Dictionary<int, int>();
                foreach (var item in completed.Events) counts[item.ProcessIndex] = counts.GetValueOrDefault(item.ProcessIndex) + 1;
                return TraceTree.Build(completed.Processes).Select(row => new TraceProcessRow(row, counts.GetValueOrDefault(row.Process.Index))).ToArray();
            });
            if (closing || !ReferenceEquals(result, completed)) return;
            ProcessTree.ItemsSource = rows;
            ShowMode(false); timer.Start(); UpdateControls();
        }
        catch (Exception ex)
        {
            timer.Stop(); busy = false;
            if (!closing) { CaptureStatus.Text = "Failed"; Notice.Text = $"Recording unavailable: {ex.GetType().Name}. No activity is saved automatically."; UpdateControls(); }
            recorder?.Dispose();
        }
    }

    private async void StopClicked(object sender, RoutedEventArgs args)
    { StopButton.IsEnabled = false; if (recorder is not null) await recorder.StopAsync(); }

    private static string CounterText(TimeSpan elapsed, long seen, int stored, long processes, long lost, long dropped, long unresolved)
        => $"Elapsed {elapsed:mm\\:ss} · Events {stored:N0} retained / {seen:N0} seen · Processes started {processes:N0} · Lost {lost:N0} · Dropped {dropped:N0} · Unresolved {unresolved:N0}";

    private static string StatusLabel(TraceStatus status) => status switch
    {
        TraceStatus.NotStarted => "Not started",
        TraceStatus.Running => "Recording",
        TraceStatus.Completed => "Completed",
        TraceStatus.Partial => "Partial",
        TraceStatus.Cancelled => "Cancelled",
        TraceStatus.Failed => "Failed",
        TraceStatus.RequiresAdministrator => "Requires administrator",
        TraceStatus.SessionUnavailable => "Session unavailable",
        _ => "Unknown status"
    };

    private void RefreshLive()
    {
        if (recorder is null || closing) return;
        var live = recorder.Live();
        CaptureStatus.Text = StatusLabel(recorder.Status) + " · private, read-only ETW recording";
        Counters.Text = CounterText(live.Elapsed, live.EventsSeen, live.Stored, live.ProcessesStarted, 0, live.BufferDropped, live.UnresolvedNames);
        Notice.Text = "Live tail only (last 100 events). Full timeline, process tree and filters appear after stop. ETW lost count is finalized on stop.";
        Timeline.ItemsSource = new TraceTimelineItems(live.Tail, [], null);
        Empty.Visibility = live.Tail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = "Waiting for selected activity…";
        ViewCount.Text = $"Live tail · {live.Tail.Count:N0} rows · malformed {live.Malformed:N0} · excluded own events {live.ExcludedSelf:N0}";
    }

    private void ResetView()
    {
        filterCancellation?.Cancel(); queryGeneration++; summaryGeneration++;
        result = null; appliedFilter = new(); selectedProcess = -1; querying = filterFailed = false;
        Timeline.ItemsSource = ProcessTree.ItemsSource = null;
        Filters.Visibility = ViewTabs.Visibility = Summary.Visibility = Visibility.Collapsed;
        TopScroll.ChangeView(null, 0, null, true);
        rebuilding = true; Search.Text = From.Text = To.Text = ""; CategoryPicker.SelectedIndex = 0; ProcessPicker.ItemsSource = null; rebuilding = false;
        SummaryTitle.Text = "Select an event or process"; SummaryText.Text = TopText.Text = ""; InspectButton.IsEnabled = false;
        Empty.Text = "Your recorded activity will appear here."; Empty.Visibility = Visibility.Visible;
        ShowMode(false);
    }
    private void ClearClicked(object sender, RoutedEventArgs args)
    {
        timer.Stop(); recorder?.Dispose(); recorder = null; starting = null; ResetView();
        CaptureStatus.Text = StatusLabel(TraceStatus.NotStarted) + " · ready for an opt-in recording";
        Counters.Text = CounterText(TimeSpan.Zero, 0, 0, 0, 0, 0, 0);
        ViewCount.Text = "No recording"; Notice.Text = "Recording cleared from memory. Exported files, if any, are unchanged.";
        Limitations.Text = TraceExport.Limitations; Setup.IsExpanded = true; UpdateControls();
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs args) => ApplyFilter();
    private void FilterSelectionChanged(object sender, SelectionChangedEventArgs args) => ApplyFilter();
    private void FilterChanged(object sender, RoutedEventArgs args) => ApplyFilter();
    private void ProcessChanged(object sender, SelectionChangedEventArgs args)
    {
        if (rebuilding || result is null) return;
        if (ProcessPicker.SelectedIndex > 0) ShowSummary(ProcessPicker.SelectedIndex - 1);
        ApplyFilter();
    }
    private async void ApplyFilter()
    {
        if (!initialized || rebuilding || result is not { } captured || busy || closing) return;
        filterCancellation?.Cancel(); filterCancellation?.Dispose(); filterCancellation = new();
        var token = filterCancellation.Token; int generation = ++queryGeneration;
        querying = true; filterFailed = false; UpdateControls();
        try
        {
            DateTimeOffset? Range(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > 86400)
                    throw new FormatException("Time range must be seconds from recording start (0–86400).");
                return captured.StartedUtc.AddSeconds(seconds);
            }
            var categories = new[] { TraceCategories.All, TraceCategories.Process, TraceCategories.Image, TraceCategories.File, TraceCategories.Registry, TraceCategories.Network };
            var filter = new TraceFilter(ProcessPicker.SelectedIndex > 0 ? ProcessPicker.SelectedIndex - 1 : null, Descendants.IsChecked == true, categories[Math.Max(0, CategoryPicker.SelectedIndex)], Search.Text, Range(From.Text), Range(To.Text));
            if (filter.FromUtc > filter.ToUtc) throw new FormatException("From time must not exceed To time.");
            ViewCount.Text = "Filtering…";
            await Task.Delay(250, token);
            var positions = await Task.Run(() => filter.Match(captured.Events, captured.Processes), token);
            if (token.IsCancellationRequested || generation != queryGeneration || closing || !ReferenceEquals(result, captured)) return;
            appliedFilter = filter;
            Timeline.SelectedIndex = -1;
            Timeline.ItemsSource = new TraceTimelineItems(captured.Events, captured.Processes, positions);
            ViewCount.Text = $"{positions.Length:N0} of {captured.Events.Count:N0} retained events · chronological order · summaries describe retained events";
            Empty.Text = "No events match these filters."; Empty.Visibility = !treeMode && positions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == queryGeneration) { filterFailed = true; ViewCount.Text = $"Filter not applied: {ex.Message}"; } }
        finally { if (generation == queryGeneration && !closing) { querying = false; UpdateControls(); } }
    }

    private void EventsClicked(object sender, RoutedEventArgs args) => ShowMode(false);
    private void TreeClicked(object sender, RoutedEventArgs args) => ShowMode(true);
    private void ShowMode(bool tree)
    {
        treeMode = tree; EventsTab.IsChecked = !tree; TreeTab.IsChecked = tree;
        Timeline.Visibility = EventHeader.Visibility = tree ? Visibility.Collapsed : Visibility.Visible;
        ProcessTree.Visibility = TreeHeader.Visibility = tree ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = Visibility.Collapsed;
        if (tree) ViewCount.Text = $"{result?.Processes.Count ?? 0:N0} observed process instances · seeded = started before recording · parent not observed is not proof of no parent";
        else if (result is not null) ApplyFilter();
    }
    private void EventSelected(object sender, SelectionChangedEventArgs args)
    { if (Timeline.SelectedItem is TraceTimelineRow row) ShowSummary(row.ProcessIndex); }
    private void TreeSelected(object sender, SelectionChangedEventArgs args)
    {
        if (ProcessTree.SelectedItem is not TraceProcessRow row) return;
        if (ProcessPicker.SelectedIndex != row.Process.Index + 1) ProcessPicker.SelectedIndex = row.Process.Index + 1;
        else ShowSummary(row.Process.Index);
    }
    private async void ShowSummary(int index)
    {
        if (result is not { } captured || index < 0 || index >= captured.Processes.Count) return;
        int generation = ++summaryGeneration; selectedProcess = index; var p = captured.Processes[index];
        SummaryTitle.Text = $"{p.ImageName ?? "(name unavailable)"} ({p.Pid})";
        SummaryText.Text = "Calculating retained-event summary…"; TopText.Text = ""; RefreshInspect();
        var summary = await Task.Run(() => TraceProcessSummary.Build(captured, index));
        if (generation != summaryGeneration || !ReferenceEquals(result, captured) || closing) return;
        string Distinct(TraceDistinctSummary value) => $"{value.DistinctRetained:N0}" + (value.Capped ? $" (capped; {value.UntrackedObservations:N0} untracked observations)" : "");
        SummaryText.Text = $"Parent PID {p.ParentPid}: {(p.ParentIndex < 0 ? "parent not observed" : captured.Processes[p.ParentIndex].ImageName ?? "name unavailable")}\nStart: {(p.Seeded ? "before recording (seeded)" : p.StartUtc?.ToLocalTime().ToString("G") ?? "unavailable")}\nExit: {p.ExitUtc?.ToLocalTime().ToString("G") ?? "not observed"} · code {p.ExitCode?.ToString() ?? "unavailable"}\nThreads started / stopped: {p.ThreadsStarted:N0} / {p.ThreadsStopped:N0}\nImages loaded: {summary.ImagesLoaded:N0}\nFile read / write: {Display.Bytes(summary.BytesRead)} / {Display.Bytes(summary.BytesWritten)}\nNetwork sent / received: {Display.Bytes(summary.BytesSent)} / {Display.Bytes(summary.BytesReceived)}\nFiles: {Distinct(summary.Files)}\nRegistry keys: {Distinct(summary.RegistryKeys)}\nEndpoints: {Distinct(summary.Endpoints)}\n" + string.Join(" · ", summary.CategoryCounts.Select(pair => $"{pair.Key}: {pair.Value:N0}"));
        static string Top(string title, TraceDistinctSummary value) => title + "\n" + (value.Top.Count == 0 ? "No retained observations" : string.Join("\n", value.Top.Select(item => $"{item.Count:N0} × {item.Name}")));
        TopText.Text = Top("Top files", summary.Files) + "\n\n" + Top("Top registry keys", summary.RegistryKeys) + "\n\n" + Top("Top endpoints", summary.Endpoints);
        RefreshInspect();
    }
    private void RefreshInspect()
    {
        var p = result is { } r && selectedProcess >= 0 && selectedProcess < r.Processes.Count ? r.Processes[selectedProcess] : null;
        InspectButton.IsEnabled = p is not null && p.ExitUtc is null && isCurrent(new(p.Pid, p.CreateTimeFileTime));
        InspectReason.Text = InspectButton.IsEnabled ? "PID and creation time match the current live snapshot." : "Unavailable: no matching current live PID + creation time. An identical PID alone is not a match.";
        if (summaryDialog is { } dialog)
        {
            dialog.IsPrimaryButtonEnabled = InspectButton.IsEnabled;
            dialog.Title = SummaryTitle.Text;
            if (dialog.Content is ScrollViewer { Content: StackPanel content })
            {
                ((TextBlock)content.Children[0]).Text = InspectReason.Text;
                ((TextBlock)content.Children[1]).Text = SummaryText.Text;
                ((TextBlock)content.Children[2]).Text = TopText.Text;
            }
        }
    }
    private void InspectClicked(object sender, RoutedEventArgs args)
    {
        RefreshInspect();
        if (InspectButton.IsEnabled && result is { } r && selectedProcess >= 0) { var p = r.Processes[selectedProcess]; inspect?.Invoke(new(p.Pid, p.CreateTimeFileTime)); }
    }
    private async void SummaryClicked(object sender, RoutedEventArgs args)
    {
        if (summaryDialog is not null || closing) return;
        RefreshInspect();
        var content = new StackPanel { Spacing = 10 };
        foreach (string text in new[] { InspectReason.Text, SummaryText.Text, TopText.Text })
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = SummaryTitle.Text,
            Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(160, XamlRoot.Size.Height * .55), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close", PrimaryButtonText = "Inspect live process", IsPrimaryButtonEnabled = InspectButton.IsEnabled
        };
        summaryDialog = dialog;
        try { if (await dialog.ShowAsync() == ContentDialogResult.Primary && !closing) InspectClicked(sender, args); }
        catch (Exception ex) { if (!closing) Notice.Text = $"Summary unavailable: {ex.Message}"; }
        finally { summaryDialog = null; }
    }

    private async void ExportClicked(object sender, RoutedEventArgs args)
    {
        if (result is not { } captured || sender is not Button { Tag: string format } || querying || filterFailed || exporting) return;
        try
        {
            string? path = TraceSaveDialog.Choose(owner, format);
            if (path is null) return;
            exporting = true; UpdateControls(); var filter = appliedFilter;
            Notice.Text = "Exporting the current filtered view…";
            await Task.Run(() =>
            {
                if (format == "csv") { using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true)); TraceExport.WriteCsv(writer, captured, filter); }
                else { using var stream = File.Create(path); TraceExport.WriteJson(stream, captured, filter); }
            });
            if (!closing) Notice.Text = $"Exported filtered {format.ToUpperInvariant()} to {path}";
        }
        catch (Exception ex) { if (!closing) Notice.Text = $"Export failed: {ex.Message}"; }
        finally { exporting = false; if (!closing) UpdateControls(); }
    }

    private void ViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!initialized) return;
        ConfigureLayout(); RefreshInspect();
    }
    private void ResultsSizeChanged(object sender, SizeChangedEventArgs args)
    { if (initialized) ConfigureLayout(); }
    private void ConfigureLayout()
    {
        double width = ActualWidth;
        bool compact = width < 1050;
        bool setup = result is null && !busy;
        ResultsLayout.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
        ResultsRow.Height = setup ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TopScroll.MaxHeight = setup ? Math.Max(140, ActualHeight - Actions.ActualHeight - Math.Max(48, LimitsPanel.ActualHeight) - 18)
            : Math.Max(140, ActualHeight * (result is not null && compact ? .4 : .52));
        Setup.Visibility = busy || result is not null ? Visibility.Collapsed : Visibility.Visible;
        Notice.Visibility = result is not null && compact ? Visibility.Collapsed : Visibility.Visible;
        bool narrow = width < 760;
        var checks = new[] { Processes, Threads, Images, Files, Registry, Network };
        int columns = compact ? 3 : 6;
        for (int i = 0; i < checks.Length; i++) { Grid.SetColumn(checks[i], i % columns); Grid.SetRow(checks[i], i / columns); }
        for (int i = 0; i < CategorySetup.ColumnDefinitions.Count; i++) CategorySetup.ColumnDefinitions[i].Width = i < columns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ExcludeSelf, narrow ? 1 : 0); Grid.SetColumn(ExcludeSelf, narrow ? 0 : 2); Grid.SetColumnSpan(ExcludeSelf, narrow ? 3 : 1);
        bool wide = !compact;
        SummaryColumn.Width = new GridLength(wide && result is not null ? 300 : 0);
        Summary.Visibility = wide && result is { Processes.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SummaryButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        SummaryScroll.MaxHeight = Math.Max(100, ResultsLayout.ActualHeight - 80);
    }
}

public sealed class TraceTimelineRow
{
    internal object Source { get; }
    internal int Position { get; }
    public int ProcessIndex { get; }
    public string Time { get; }
    public string Process { get; }
    public string Operation { get; }
    public string Path { get; }
    public string Detail { get; }
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
    public string Bytes { get; }
    public string Description => $"{Time} · {Process} · {Operation}\n{Path}\n{Detail} · {Bytes} bytes";
    internal TraceTimelineRow(SystemExplorer.Core.TraceEvent item, IReadOnlyList<TraceProcess> processes, object source, int position)
    {
        Source = source; Position = position;
        ProcessIndex = item.ProcessIndex;
        Time = new DateTimeOffset(item.TimestampUtc, TimeSpan.Zero).ToLocalTime().ToString("HH:mm:ss.fff");
        Process = item.ProcessIndex >= 0 && item.ProcessIndex < processes.Count ? $"{processes[item.ProcessIndex].ImageName ?? "(name unavailable)"} ({item.Pid})" : $"PID {item.Pid}";
        Operation = item.Operation.ToString(); Path = item.Path ?? "—"; Detail = item.Detail ?? ""; Bytes = item.Bytes.ToString("N0");
    }
}

// IList lets ListView request only realized rows; no event-sized array of UI objects.
public sealed class TraceTimelineItems(IReadOnlyList<SystemExplorer.Core.TraceEvent> events, IReadOnlyList<TraceProcess> processes, int[]? positions) : IList
{
    private readonly Dictionary<int, TraceTimelineRow> rows = [];
    private readonly Queue<int> recent = [];
    public int Count => positions?.Length ?? events.Count;
    public object? this[int index]
    {
        get
        {
            if (rows.TryGetValue(index, out var row)) return row;
            row = new(events[positions is null ? index : positions[index]], processes, this, index);
            if (rows.Count == 256) rows.Remove(recent.Dequeue());
            rows.Add(index, row); recent.Enqueue(index); return row;
        }
        set => throw new NotSupportedException();
    }
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value) => value is TraceTimelineRow row && ReferenceEquals(row.Source, this) ? row.Position : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
}

public sealed class TraceProcessRow
{
    public TraceProcess Process { get; }
    public Thickness Indent { get; }
    public string Name => $"{Process.ImageName ?? "(name unavailable)"} ({Process.Pid})";
    public string Parent { get; }
    public string Start => Process.Seeded ? "Before recording" : Process.StartUtc?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "Unavailable";
    public string Exit => Process.ExitUtc is { } exit ? $"{exit.ToLocalTime():HH:mm:ss.fff}\ncode {Process.ExitCode?.ToString() ?? "unavailable"}" : "Not observed";
    public string Description => $"{Name}\n{Parent}\n{Process.ImagePath}\nStarted {Start} · Exit {Exit}";
    public TraceProcessRow(TraceTreeRow row, int events)
    {
        Process = row.Process; Indent = new(Math.Min(row.Depth, 16) * 12, 0, 6, 0);
        Parent = $"Parent PID {Process.ParentPid} · {(Process.ParentIndex < 0 ? "parent not observed" : $"instance #{Process.ParentIndex}")} · {events:N0} events";
    }
}
