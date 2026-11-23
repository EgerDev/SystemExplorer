using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed record GpuEngineBarItem(string Engine, string PercentText, double BarWidth, Brush BarBrush);

public sealed record GpuAdapterCardItem(
    string Name,
    string HeadlineText,
    string MemorySummary,
    GpuEngineBarItem[] Engines);

public sealed record GpuProcessListItem(
    ProcessIdentity? Identity,
    bool CanInspect,
    string DisplayName,
    string EngineDetail,
    string UtilText,
    string DedicatedText);

public sealed partial class GpuView : UserControl
{
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action<string>? InventoryRequested;

    private GpuCounterSection? latestGpu;

    public GpuView()
    {
        InitializeComponent();
    }

    public void AnimateEntrance() => Motion.AnimateStaggeredChildren(GpuKpiGrid);

    public void UpdateCounters(GpuCounterSection? gpu)
    {
        latestGpu = gpu;
    }

    public void Update(MonitorSnapshot snapshot, string search, Func<ProcessSnapshot, string?> descriptions)
    {
        var gpu = latestGpu;
        if (gpu is null)
        {
            GpuStatusBadge.Text = "Warming up";
            GpuStatusDetail.Text = "Capturing Windows DXGI adapter inventory and WDDM engine counters…";
            return;
        }

        GpuStatusBadge.Text = gpu.Status.ToString();
        GpuStatusDetail.Text = string.IsNullOrWhiteSpace(gpu.Detail) ? gpu.InventoryDetail : gpu.Detail;

        var adapters = gpu.Adapters;
        AdaptersCard.Value = adapters.Count > 0 ? adapters.Count.ToString("N0") : "0";

        // Headline utilization = max across all adapters' headline engine utilization
        var maxUtil = adapters.Select(a => a.HeadlineUtilizationPercent.Value).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        HeadlineUtilCard.Value = maxUtil.Length > 0 ? $"{maxUtil.Max():0.0}%" : "Unavailable";

        var firstWithDed = adapters.FirstOrDefault(a => a.DedicatedUsageBytes.Value.HasValue || a.DedicatedVideoMemoryBytes.HasValue);
        if (firstWithDed is not null)
        {
            var used = firstWithDed.DedicatedUsageBytes.Value is { } du ? DiagnosticData.Bytes((ulong)Math.Max(0, du)) : "—";
            var cap = firstWithDed.DedicatedVideoMemoryBytes is { } dc ? DiagnosticData.Bytes(dc) : "—";
            DedicatedVramCard.Value = firstWithDed.DedicatedUsageBytes.Value.HasValue ? $"{used} / {cap}" : cap;
        }
        else DedicatedVramCard.Value = "Unavailable";

        var firstWithShared = adapters.FirstOrDefault(a => a.SharedUsageBytes.Value.HasValue || a.SharedSystemMemoryBytes.HasValue);
        if (firstWithShared is not null)
        {
            var used = firstWithShared.SharedUsageBytes.Value is { } su ? DiagnosticData.Bytes((ulong)Math.Max(0, su)) : "—";
            var cap = firstWithShared.SharedSystemMemoryBytes is { } sc ? DiagnosticData.Bytes(sc) : "—";
            SharedMemCard.Value = firstWithShared.SharedUsageBytes.Value.HasValue ? $"{used} / {cap}" : cap;
        }
        else SharedMemCard.Value = "Unavailable";

        var violet = ResolveThemeBrush("ChartVioletBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.MediumPurple);
        var blue = ResolveThemeBrush("InteractionAccent") ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var amber = ResolveThemeBrush("StatusWarnBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Orange);

        var adapterCards = new GpuAdapterCardItem[adapters.Count];
        for (int i = 0; i < adapters.Count; i++)
        {
            var a = adapters[i];
            var name = a.Description ?? $"Adapter 0x{a.Luid:X}";
            var headline = a.HeadlineUtilizationPercent.Value is { } hv
                ? $"{hv:0.0}% ({a.HeadlineEngineType ?? "Engine"})"
                : a.HasCounterInstances ? "Warming up" : "No WDDM counters";
            var dedU = a.DedicatedUsageBytes.Value is { } d1 ? DiagnosticData.Bytes((ulong)Math.Max(0, d1)) : "—";
            var dedT = a.DedicatedVideoMemoryBytes is { } d2 ? DiagnosticData.Bytes(d2) : "—";
            var shU = a.SharedUsageBytes.Value is { } s1 ? DiagnosticData.Bytes((ulong)Math.Max(0, s1)) : "—";
            var shT = a.SharedSystemMemoryBytes is { } s2 ? DiagnosticData.Bytes(s2) : "—";
            var mem = $"Dedicated {dedU} / {dedT} · Shared {shU} / {shT} (shared is system RAM)";

            var engBars = new GpuEngineBarItem[a.Engines.Count];
            for (int j = 0; j < a.Engines.Count; j++)
            {
                var e = a.Engines[j];
                if (e.UtilizationPercent.Value is { } ev && double.IsFinite(ev))
                {
                    var clamped = Math.Clamp(ev, 0, 100);
                    var brush = clamped >= 75 ? amber : clamped >= 35 ? violet : blue;
                    engBars[j] = new GpuEngineBarItem(e.EngineType, $"{ev:0.0}%", Math.Max(4, clamped / 100.0 * 180.0), brush);
                }
                else
                {
                    engBars[j] = new GpuEngineBarItem(e.EngineType, "—", 0, violet);
                }
            }
            adapterCards[i] = new GpuAdapterCardItem(name, headline, mem, engBars);
        }
        AdaptersList.ItemsSource = adapterCards;

        // Per-process GPU list
        var procByPid = snapshot.Processes.GroupBy(p => p.Identity.Pid).ToDictionary(g => g.Key, g => g.First());
        var query = search.Trim();
        var procItems = gpu.Processes
            .Select(gp =>
            {
                procByPid.TryGetValue(gp.Pid, out var liveProc);
                var label = liveProc is not null ? (descriptions(liveProc) ?? liveProc.Name) : $"PID {gp.Pid}";
                var disp = $"{label} ({gp.Pid})";
                var eng = $"Busiest: {gp.MaxEngineType ?? "—"} · {(gp.Identity is null ? gp.IdentityStatus.ToString() : "Verified")}";
                var util = gp.MaxUtilizationPercent.Value is { } uv ? $"{uv:0.0}%" : "—";
                var ded = gp.DedicatedUsageBytes.Value is { } dv ? DiagnosticData.Bytes((ulong)Math.Max(0, dv)) : "—";
                return (gp, item: new GpuProcessListItem(gp.Identity, gp.Identity is not null, disp, eng, util, ded));
            })
            .Where(x => string.IsNullOrEmpty(query) ||
                        x.item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.item.EngineDetail.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.gp.MaxUtilizationPercent.Value ?? -1)
            .ThenByDescending(x => x.gp.DedicatedUsageBytes.Value ?? -1)
            .Take(25)
            .Select(x => x.item)
            .ToArray();

        GpuProcCount.Text = $"{procItems.Length} observed";
        GpuProcessesList.ItemsSource = procItems;
        GpuProcsEmpty.Visibility = procItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
