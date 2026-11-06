using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using SystemExplorer.Core;
using Windows.System;

namespace SystemExplorer.App.Views;

public sealed partial class OverviewView : UserControl
{
    private readonly HashSet<ProcessIdentity> expanded = new();
    private readonly ProcessIcons icons = new();
    private readonly ApplicationDescriptions descriptions = new();
    private OverviewReading? reading;
    private bool ready;
    private bool? compactHeight;

    public ObservableCollection<OverviewConsumerRow> Rows { get; } = new();
    public ConsumerRanking Ranking { get; private set; } = ConsumerRanking.PrivateCommit;
    public Func<ProcessSnapshot, string?> DescriptionLookup => descriptions.GetCached;
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action? InsightsRequested;
    public event Action? RankingChanged;
    public event Action? ViewProcessesRequested;

    public OverviewView()
    {
        InitializeComponent();
        NetworkCard.TrendHeight = double.NaN;
        ready = true;
        Unloaded += (_, _) => { foreach (var row in Rows) row.Dispose(); };
        Loaded += (_, _) => ReconcileConsumers();
        DashboardScroller.SizeChanged += (_, _) => UpdateDensity();
        foreach (var element in new FrameworkElement[] { DashboardScroller, ResourcesGrid, ReadingStatus, ConsumerHeader, ConsumerTableHeader, ConsumerFooter })
            element.SizeChanged += (_, _) => ResizeConsumerList();
    }

    public void Update(OverviewReading? reading, bool paused, bool stale)
    {
        this.reading = reading;
        CpuCard.Value = Display.Percent(reading?.CpuPercent);
        CpuCard.Detail = "Across all processors";
        MemoryCard.Value = Display.Percent(reading?.PhysicalMemoryPercent);
        MemoryCard.Detail = $"{Display.Bytes(reading?.PhysicalUsedBytes)} / {Display.Bytes(reading?.PhysicalTotalBytes)}";
        DiskCard.Value = reading?.DiskTotalBytesPerSecond is { } total ? Display.Rate(total) : "—";
        DiskCard.Detail = $"Read\u00a0{Rate(reading?.DiskReadBytesPerSecond)} · Write\u00a0{Rate(reading?.DiskWriteBytesPerSecond)}";
        NetworkObservation.Text = reading?.ObservedEndpointCount is { } endpoints
            ? $"{endpoints} observed endpoints · {reading.RelationshipsCapturedAt?.LocalDateTime:T}"
            : "Endpoint observation unavailable";
        CpuTrend.Update(reading?.CpuTrend, "CPU");
        MemoryTrend.Update(reading?.MemoryTrend, "Physical memory");
        DiskTrend.Update(reading?.DiskTrend, "Disk read + write throughput", true);
        MemoryHeadline.Text = reading?.PhysicalMemoryPercent is { } percent ? $"Physical memory usage ({percent:F0}%)" : "Physical memory unavailable";
        MemoryEvidence.Text = $"{Display.Bytes(reading?.PhysicalUsedBytes)} used / {Display.Bytes(reading?.PhysicalTotalBytes)} total.";
        AvailableValue.Text = Display.Bytes(reading?.PhysicalAvailableBytes);
        CacheValue.Text = Display.Bytes(reading?.SystemCacheBytes);
        CommitValue.Text = $"{Display.Bytes(reading?.CommitTotalBytes)} / {Display.Bytes(reading?.CommitLimitBytes)}";
        AutomationProperties.SetName(AvailableValue, $"Available physical memory: {AvailableValue.Text}");
        AutomationProperties.SetName(CacheValue, $"System cache, overlaps physical memory: {CacheValue.Text}");
        AutomationProperties.SetName(CommitValue, $"Committed virtual memory / limit: {CommitValue.Text}");
        ReadingStatus.Text = reading is null ? "Waiting for the first live sample…" :
            $"{(stale ? "Stale · last successful observation" : paused ? "Paused · frozen observation" : "Observed")} {reading.CapturedAt.LocalDateTime:T} · Local, read-only · Up to 50 ranked groups";
        ReadingStatus.Visibility = reading is null || paused || stale ? Visibility.Visible : Visibility.Collapsed;
        ReconcileConsumers();
    }

    private static string Rate(double? value) => value is { } number ? Display.Rate(number).Replace(' ', '\u00a0') : "—";

    private void ReconcileConsumers()
    {
        if (!ready) return;
        var groups = reading?.Consumers ?? Array.Empty<ProcessGroup>();
        var live = groups.Where(g => g.Key.IsStable).Select(g => g.Key).ToHashSet();
        expanded.RemoveWhere(id => !live.Contains(id));
        var selected = ConsumersList.SelectedItem as OverviewConsumerRow;
        var selectedKey = selected?.Identity.IsStable == true ? selected.Key : null;
        var desired = new List<(ProcessSnapshot Process, ProcessGroup? Group, bool Child)>();
        foreach (var group in groups)
        {
            var representative = group.Members.FirstOrDefault(p => p.Identity == group.Key);
            if (representative is null) continue;
            desired.Add((representative, group, false));
            if (group.Members.Count > 1 && expanded.Contains(group.Key))
                desired.AddRange(group.Members.Select(p => (p, (ProcessGroup?)null, true)));
        }
        var wanted = desired.Select(r => $"{(r.Child ? "member" : "group")}:{r.Process.Identity}").ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Rows[i].Key) || !Rows[i].Identity.IsStable) { Rows[i].Dispose(); Rows.RemoveAt(i); }
        var existing = Rows.ToDictionary(r => r.Key);
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var key = $"{(item.Child ? "member" : "group")}:{item.Process.Identity}";
            if (!existing.TryGetValue(key, out var row))
            {
                row = new OverviewConsumerRow(item.Process, item.Group, item.Child, expanded.Contains(item.Process.Identity));
                Rows.Insert(i, row);
            }
            else if (Rows.IndexOf(row) != i) Rows.Move(Rows.IndexOf(row), i);
            row.Update(item.Process, item.Group, expanded.Contains(item.Process.Identity), icons, descriptions);
        }
        ConsumersList.SelectedItem = selectedKey is null ? null : Rows.FirstOrDefault(r => r.Key == selectedKey);
        EmptyConsumers.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyConsumers.Text = reading is null ? "Waiting for the first live sample…" : "No processes match this view. Clear the search to show available processes.";
    }

    private void RankingSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready) return;
        Ranking = RankingSelector.SelectedIndex == 1 ? ConsumerRanking.Cpu : ConsumerRanking.PrivateCommit;
        RankingChanged?.Invoke();
    }

    private void ExpandClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: OverviewConsumerRow row }) Toggle(row);
    }

    private void Toggle(OverviewConsumerRow row)
    {
        if (!row.HasChildren || !row.Identity.IsStable) return;
        if (!expanded.Add(row.Identity)) expanded.Remove(row.Identity);
        ReconcileConsumers();
    }

    private void ConsumerKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not ListViewItem item ||
            ConsumersList.ItemFromContainer(item) is not OverviewConsumerRow row) return;
        if (args.Key == VirtualKey.Right && row.HasChildren && !row.Expanded || args.Key == VirtualKey.Left && row.Expanded)
        {
            Toggle(row);
            args.Handled = true;
        }
    }

    private void InspectClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: OverviewConsumerRow row } && Rows.Contains(row))
        {
            ConsumersList.SelectedItem = row;
            InspectRequested?.Invoke(row.Identity);
        }
    }

    private void InsightsClicked(object sender, RoutedEventArgs args) => InsightsRequested?.Invoke();
    private void ViewProcessesClicked(object sender, RoutedEventArgs args) => ViewProcessesRequested?.Invoke();

    private void UpdateDensity()
    {
        if (!ready || DashboardScroller.ActualHeight <= 0) return;
        var compact = DashboardScroller.ActualHeight < 520;
        if (compactHeight == compact) return;
        compactHeight = compact;
        foreach (var card in new[] { CpuCard, MemoryCard, DiskCard, NetworkCard }) card.IsCompact = compact;
        foreach (var card in new[] { CpuCard, MemoryCard, DiskCard }) card.TrendHeight = compact ? 24 : 32;
        ConsumersPanel.Padding = InsightsPanel.Padding = new Thickness(compact ? 12 : 16);
        ConsumersLayout.RowSpacing = compact ? 6 : 10;
        ConsumerCaption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ConsumerTableHeader.Padding = new Thickness(10, compact ? 4 : 8, 42, compact ? 4 : 8);
        ResourcesGrid.Margin = new Thickness(0, 0, 0, compact ? 12 : 16);
        InsightsContent.Spacing = compact ? 8 : 12;
        MemorySummary.Padding = new Thickness(compact ? 8 : 12);
        MemoryFacts.RowSpacing = compact ? 8 : 14;
        InsightsAction.Padding = new Thickness(12, compact ? 8 : 10, 12, compact ? 8 : 10);
        ResizeConsumerList();
    }

    private void ResizeConsumerList()
    {
        if (!ready || DashboardScroller.ActualHeight <= 0) return;
        var statusHeight = ReadingStatus.Visibility == Visibility.Visible
            ? ReadingStatus.ActualHeight + ReadingStatus.Margin.Top + ReadingStatus.Margin.Bottom : 0;
        var chromeHeight = ConsumersPanel.Padding.Top + ConsumersPanel.Padding.Bottom +
            ConsumersPanel.BorderThickness.Top + ConsumersPanel.BorderThickness.Bottom +
            ConsumerHeader.ActualHeight + ConsumerTableHeader.ActualHeight + ConsumerFooter.ActualHeight +
            ConsumersLayout.RowSpacing * (ConsumersLayout.RowDefinitions.Count - 1);
        var panelHeight = DashboardScroller.ActualHeight - statusHeight -
            ResourcesGrid.ActualHeight - ResourcesGrid.Margin.Top - ResourcesGrid.Margin.Bottom;
        InsightsPanel.Height = Dashboard.ActualWidth >= 820 ? Math.Max(180, Math.Floor(panelHeight)) : double.NaN;
        var height = Math.Max(80, Math.Floor(panelHeight - chromeHeight));
        if (Math.Abs(ConsumersList.MaxHeight - height) > 1) ConsumersList.MaxHeight = height;
    }

    private void DashboardSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < 820;
        ResourcesGrid.RowSpacing = compact ? 12 : 0;
        PanelsGrid.RowSpacing = compact ? 16 : 0;
        Grid.SetColumn(MemoryCard, compact ? 2 : 1);
        Grid.SetColumn(DiskCard, compact ? 0 : 2);
        Grid.SetColumn(NetworkCard, compact ? 2 : 3);
        foreach (var card in new FrameworkElement[] { CpuCard, MemoryCard, DiskCard, NetworkCard })
            Grid.SetColumnSpan(card, compact ? 2 : 1);
        Grid.SetRow(DiskCard, compact ? 1 : 0);
        Grid.SetRow(NetworkCard, compact ? 1 : 0);
        Grid.SetColumnSpan(ConsumersPanel, compact ? 2 : 1);
        Grid.SetColumnSpan(InsightsPanel, compact ? 2 : 1);
        Grid.SetColumn(InsightsPanel, compact ? 0 : 1);
        Grid.SetRow(InsightsPanel, compact ? 1 : 0);
    }
}
