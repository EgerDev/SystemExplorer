using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed record CoreBarItem(string Label, string PercentText, double BarWidth, Brush BarBrush);

public sealed record CpuConsumerItem(
    ProcessIdentity Identity,
    string DisplayName,
    string Detail,
    string CpuText,
    double LoadBarWidth,
    Brush LoadBarBrush,
    string ThreadsText,
    string HandlesText);

public sealed partial class CpuView : UserControl
{
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action<string>? InventoryRequested;

    private CpuCounterSection? latestCores;

    public CpuView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize.Width);
    }

    public void AnimateEntrance()
    {
        Motion.AnimateStaggeredChildren(CpuKpiGrid);
    }

    public void UpdateCores(CpuCounterSection? cores)
    {
        latestCores = cores;
        RenderCores();
    }

    public void Update(
        MonitorSnapshot snapshot,
        DiagnosticsHistory diagnostics,
        string search,
        Func<ProcessSnapshot, string?> descriptions,
        bool paused,
        bool stale)
    {
        var logicalCount = Math.Max(1, latestCores?.LogicalProcessors.Count ?? Environment.ProcessorCount);
        var totalCpu = snapshot.TotalCpuPercent;
        var totalText = totalCpu is { } pct ? $"{pct:0.0}% total" : "—";
        var privText = latestCores?.TotalPrivilegedTimePercent.Value is { } priv
            ? $" · Kernel {priv:0.0}%"
            : "";
        CpuChartHeadline.Text = $"CPU utilization · {totalText}{privText}";

        CpuContextLine.Text = $"{logicalCount} logical processors · Normalized per-process CPU · Read-only";
        CoreCountText.Text = $"{logicalCount} observed";
        ConsumersHeading.Text = $"Top CPU consumers · Normalized across {logicalCount} logical cores";
        SampleWindowText.Text = paused ? "Paused" : stale ? "Stale sample" : "2.0s sample window";

        // Avg / Peak pill from DiagnosticsHistory.System
        var history = diagnostics.System;
        var validValues = history.Where(p => p.CpuPercent.HasValue).Select(p => p.CpuPercent!.Value).ToArray();
        if (validValues.Length > 0)
            CpuStatsText.Text = $"Avg {validValues.Average():0.0}% · Peak {validValues.Max():0.0}%";
        else
            CpuStatsText.Text = "Avg — · Peak —";

        // Update trend chart with theme-aware blue accent
        if (ResolveThemeBrush("InteractionAccent") is { } accent)
            CpuTrend.Foreground = accent;
        var scale = Math.Max(100d, validValues.Length > 0 ? validValues.Max() : 0d);
        CpuTrend.Update(DiagnosticData.BuildTrend(history, p => p.CpuPercent, scale), "System CPU");

        // 4 KPI Summary Cards. Exact system-wide counts come from GetPerformanceInfo
        // through the memory snapshot; per-process sums are only a fallback and are
        // never presented as complete totals when any process query was denied.
        if (snapshot.Memory.ThreadCount is { } exactThreads)
        {
            ThreadsCard.Label = "Threads";
            ThreadsCard.Value = exactThreads.ToString("N0");
        }
        else
        {
            var missingThreads = snapshot.Processes.Any(p => p.ThreadCount is null);
            var totalThreads = snapshot.Processes.Sum(p => (long)Math.Max(0, p.ThreadCount ?? 0));
            ThreadsCard.Label = missingThreads ? "Observed threads" : "Threads";
            ThreadsCard.Value = totalThreads > 0 ? (missingThreads ? $"{totalThreads:N0} (partial)" : totalThreads.ToString("N0")) : "—";
        }
        if (snapshot.Memory.HandleCount is { } exactHandles)
        {
            HandlesCard.Label = "Handles";
            HandlesCard.Value = exactHandles.ToString("N0");
        }
        else
        {
            var missingHandles = snapshot.Processes.Any(p => p.HandleCount is null);
            var totalHandles = snapshot.Processes.Sum(p => (long)Math.Max(0, p.HandleCount ?? 0));
            HandlesCard.Label = missingHandles ? "Observed handles" : "Handles";
            HandlesCard.Value = totalHandles > 0 ? (missingHandles ? $"{totalHandles:N0} (partial)" : totalHandles.ToString("N0")) : "—";
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeCard.Value = uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : $"{uptime.Hours}h {uptime.Minutes}m";

        var busiest = snapshot.Processes
            .Where(p => p.CpuPercent.HasValue && p.Identity.Pid != 0)
            .OrderByDescending(p => p.CpuPercent!.Value)
            .FirstOrDefault();
        BusiestAppCard.Value = busiest is not null ? busiest.Name : "—";

        // Top CPU consumers list (with inline Load bars)
        var query = search.Trim();
        var filtered = snapshot.Processes
            .Where(p => p.Identity.Pid != 0)
            .Where(p => string.IsNullOrEmpty(query) ||
                        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (descriptions(p)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        p.Identity.Pid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CpuPercent ?? -1)
            .ThenByDescending(p => p.ThreadCount ?? 0)
            .Take(25)
            .ToArray();

        var blueBrush = ResolveThemeBrush("InteractionAccent") ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var greenBrush = ResolveThemeBrush("StatusLiveBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
        var amberBrush = ResolveThemeBrush("StatusWarnBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Orange);

        var items = new CpuConsumerItem[filtered.Length];
        for (int i = 0; i < filtered.Length; i++)
        {
            var p = filtered[i];
            var cpu = p.CpuPercent;
            var pctVal = Math.Clamp(cpu ?? 0, 0, 100);
            var barWidth = cpu.HasValue ? Math.Max(4, pctVal / 100.0 * 124.0) : 0;
            var barBrush = pctVal >= 50 ? amberBrush : pctVal >= 10 ? blueBrush : greenBrush;
            var friendly = descriptions(p);
            var title = string.IsNullOrWhiteSpace(friendly)
                ? $"{p.Name} ({p.Identity.Pid})"
                : $"{friendly} ({p.Identity.Pid})";
            var detail = $"{p.Name} · {p.UserName ?? "Owner unavailable"}";
            items[i] = new CpuConsumerItem(
                p.Identity,
                title,
                detail,
                cpu is { } c ? $"{c:0.0}%" : "—",
                barWidth,
                barBrush,
                p.ThreadCount is { } t ? t.ToString("N0") : "—",
                p.HandleCount is { } h ? h.ToString("N0") : "—");
        }

        ConsumersList.ItemsSource = items;
        ConsumersEmptyText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderCores();
    }

    private void RenderCores()
    {
        if (latestCores is null || latestCores.LogicalProcessors.Count == 0)
        {
            CorePlaceholderText.Visibility = Visibility.Visible;
            CoreMatrixScroll.Visibility = Visibility.Collapsed;
            BusiestCoreText.Text = "Busiest: —";
            return;
        }

        var blueBrush = ResolveThemeBrush("InteractionAccent") ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var greenBrush = ResolveThemeBrush("StatusLiveBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
        var amberBrush = ResolveThemeBrush("StatusWarnBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Orange);

        var procs = latestCores.LogicalProcessors;
        var items = new CoreBarItem[procs.Count];
        string? busiestLabel = null;
        double busiestValue = -1;

        for (int i = 0; i < procs.Count; i++)
        {
            var p = procs[i];
            var label = $"Core {p.Number?.ToString() ?? p.Instance}";
            var val = p.ProcessorUtilityPercent.Value;
            if (val is { } v && double.IsFinite(v))
            {
                if (v > busiestValue) { busiestValue = v; busiestLabel = label; }
                var clamped = Math.Clamp(v, 0, 100);
                var barWidth = Math.Max(4, clamped / 100.0 * 165.0);
                var brush = clamped >= 75 ? amberBrush : clamped >= 45 ? blueBrush : greenBrush;
                items[i] = new CoreBarItem(label, $"{v:0}%", barWidth, brush);
            }
            else
            {
                items[i] = new CoreBarItem(label, "—", 0, greenBrush);
            }
        }

        CoreCountText.Text = $"{procs.Count} observed";
        BusiestCoreText.Text = busiestLabel is not null ? $"Busiest: {busiestLabel}" : "Busiest: —";
        CoreMatrixList.ItemsSource = items;
        CorePlaceholderText.Visibility = Visibility.Collapsed;
        CoreMatrixScroll.Visibility = Visibility.Visible;
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0) return;
        HeroGrid.ColumnDefinitions[1].Width = width < 820 ? new GridLength(240) : new GridLength(320);
    }

    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
            InventoryRequested?.Invoke(action);
    }

    private void InspectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProcessIdentity identity })
            InspectRequested?.Invoke(identity);
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
