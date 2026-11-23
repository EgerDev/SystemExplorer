using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed record PhysicalDiskCardItem(
    string Instance,
    string ActiveText,
    double ActiveBarWidth,
    Brush BarBrush,
    string ThroughputText,
    string LatencyText);

public sealed record DiskProcessItem(
    ProcessIdentity Identity,
    string DisplayName,
    string Detail,
    string ReadText,
    string WriteText,
    double BarWidth,
    Brush BarBrush);

public sealed partial class DisksView : UserControl
{
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action<string>? InventoryRequested;

    private DiskCounterSection? latestDisks;

    public DisksView()
    {
        InitializeComponent();
    }

    public void AnimateEntrance() => Motion.AnimateStaggeredChildren(DisksKpiGrid);

    public void UpdateCounters(DiskCounterSection? disks) => latestDisks = disks;

    public void Update(MonitorSnapshot snapshot, string search, Func<ProcessSnapshot, string?> descriptions)
    {
        var d = latestDisks;
        var amber = ResolveThemeBrush("StatusWarnBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Orange);
        var blue = ResolveThemeBrush("InteractionAccent") ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var green = ResolveThemeBrush("StatusLiveBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);

        // KPI cards from hardware total counters (fallback to snapshot disk rates)
        var readRate = d?.Total?.ReadBytesPerSecond.Value ?? snapshot.DiskReadBytesPerSecond;
        var writeRate = d?.Total?.WriteBytesPerSecond.Value ?? snapshot.DiskWriteBytesPerSecond;
        ReadRateCard.Value = readRate is { } rr ? Display.Rate(rr) : "—";
        WriteRateCard.Value = writeRate is { } wr ? Display.Rate(wr) : "—";

        if (d?.Total is { } tot)
        {
            var rLat = tot.ReadLatencyMilliseconds;
            var wLat = tot.WriteLatencyMilliseconds;
            if (rLat.Value.HasValue && wLat.Value.HasValue)
                LatencyCard.Value = $"R {rLat.Value.Value:0.0} / W {wLat.Value.Value:0.0} ms";
            else if (rLat.Value.HasValue)
                LatencyCard.Value = $"R {rLat.Value.Value:0.0} ms";
            else if (wLat.Value.HasValue)
                LatencyCard.Value = $"W {wLat.Value.Value:0.0} ms";
            else if (rLat.State == CounterValueState.NoActivityInInterval && wLat.State == CounterValueState.NoActivityInInterval)
                LatencyCard.Value = "Idle";
            else
                LatencyCard.Value = "—";
            QueueCard.Value = tot.CurrentQueueLength.Value is { } q ? $"{q:0.##}" : "—";
        }
        else
        {
            LatencyCard.Value = "—";
            QueueCard.Value = "—";
        }

        // Physical disks list
        if (d is not null && d.Disks.Count > 0)
        {
            DisksStatusBadge.Text = d.Status.ToString();
            var cards = new PhysicalDiskCardItem[d.Disks.Count];
            for (int i = 0; i < d.Disks.Count; i++)
            {
                var row = d.Disks[i];
                var idle = row.IdleTimePercent.Value;
                var activePct = idle.HasValue ? Math.Clamp(100.0 - idle.Value, 0, 100) : (double?)null;
                var activeText = activePct.HasValue ? $"{activePct.Value:0}% active" : "—";
                var barW = activePct.HasValue ? Math.Max(4, activePct.Value / 100.0 * 240.0) : 0;
                var brush = (activePct ?? 0) >= 75 ? amber : (activePct ?? 0) >= 30 ? blue : green;
                var rb = row.ReadBytesPerSecond.Value is { } r1 ? Display.Rate(r1) : "—";
                var wb = row.WriteBytesPerSecond.Value is { } w1 ? Display.Rate(w1) : "—";
                var rl = FormatLatency(row.ReadLatencyMilliseconds);
                var wl = FormatLatency(row.WriteLatencyMilliseconds);
                var qd = row.CurrentQueueLength.Value is { } q1 ? $"{q1:0.##}" : "—";
                cards[i] = new PhysicalDiskCardItem(
                    $"Disk {row.Instance}",
                    activeText,
                    barW,
                    brush,
                    $"Read {rb} · Write {wb}",
                    $"Latency R {rl} / W {wl} · Queue {qd}");
            }
            PhysicalDisksList.ItemsSource = cards;
            PhysicalDisksEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            DisksStatusBadge.Text = d?.Status.ToString() ?? "Warming up";
            PhysicalDisksList.ItemsSource = Array.Empty<PhysicalDiskCardItem>();
            PhysicalDisksEmpty.Text = d?.Detail ?? "Warming up physical disk counters…";
            PhysicalDisksEmpty.Visibility = Visibility.Visible;
        }

        // Top disk I/O processes
        var query = search.Trim();
        var filtered = snapshot.Processes
            .Where(p => p.Identity.Pid != 0)
            .Where(p => string.IsNullOrEmpty(query) ||
                        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (descriptions(p)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        p.Identity.Pid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => (p.ReadBytesPerSecond ?? 0) + (p.WriteBytesPerSecond ?? 0))
            .Take(25)
            .ToArray();

        var maxIo = filtered.Select(p => (p.ReadBytesPerSecond ?? 0) + (p.WriteBytesPerSecond ?? 0)).DefaultIfEmpty(0).Max();
        var items = new DiskProcessItem[filtered.Length];
        for (int i = 0; i < filtered.Length; i++)
        {
            var p = filtered[i];
            var totIo = (p.ReadBytesPerSecond ?? 0) + (p.WriteBytesPerSecond ?? 0);
            var barW = maxIo > 0 && totIo > 0 ? Math.Max(4, totIo / maxIo * 90.0) : 0;
            var friendly = descriptions(p);
            var title = string.IsNullOrWhiteSpace(friendly) ? $"{p.Name} ({p.Identity.Pid})" : $"{friendly} ({p.Identity.Pid})";
            items[i] = new DiskProcessItem(
                p.Identity,
                title,
                $"{p.Name} · {p.UserName ?? "Owner unavailable"}",
                p.ReadBytesPerSecond is { } r ? Display.Rate(r) : "—",
                p.WriteBytesPerSecond is { } w ? Display.Rate(w) : "—",
                barW,
                totIo > 1024 * 1024 ? amber : blue);
        }
        DiskProcessesList.ItemsSource = items;
        DiskProcsEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatLatency(CounterValue cv) => cv.Value is { } ms ? $"{ms:0.0} ms" : cv.State switch
    {
        CounterValueState.NoActivityInInterval => "idle",
        CounterValueState.Warmup => "warming up",
        _ => "—"
    };

    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) InventoryRequested?.Invoke(action);
    }

    private void InspectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProcessIdentity id }) InspectRequested?.Invoke(id);
    }

    private Brush? ResolveThemeBrush(string key)
    {
        var themeKey = ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var tObj) && tObj is ResourceDictionary td &&
                td.TryGetValue(key, out var val) && val is Brush b)
                return b;
        return null;
    }
}
