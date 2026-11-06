using Microsoft.UI.Xaml;
using SystemExplorer.Core;
using SystemExplorer.Relationships;
using SystemExplorer.Windows;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly IPagefileProvider pagefiles = new WindowsPagefileProvider();
    private readonly IServiceInspectionProvider serviceInspection = new WindowsServiceInspectionProvider();
    private readonly IScheduledTaskProvider scheduledTasks = new WindowsScheduledTaskProvider();
    private readonly IThreadDiagnosticsProvider threadDiagnostics = new WindowsThreadDiagnosticsProvider();
    private readonly ProcessLifecycleTimeline lifecycle = new();

    private async void ShowExtraInventory(string kind)
    {
        if (kind == "History") { HistoryClicked(this, new RoutedEventArgs()); return; }
        if (kind == "Timeline") { ShowLifecycleTimeline(); return; }
        if (kind is "Disk" or "FileUsers" or "Adapters" or "IoAttribution" or "Cores" or "Gpu" or "DllUsers" or "MemoryLists" or "PoolTags" or "Drivers" or "Persistence") { ShowSystemDepth(kind); return; }
        if (kind == "Tasks")
        {
            await ShowInventoryAsync("Local scheduled tasks", async (view, token) =>
            {
                var result = await scheduledTasks.CaptureAsync(token);
                view.ShowRows($"{result.Status} · {result.CapturedAt.LocalDateTime:T} · {result.Detail}",
                    result.Tasks.Select(t => new InventoryRow(t.Name.Length == 0 ? "Task metadata unavailable" : t.Name,
                        t.Path.Length == 0 ? "Path unavailable" : t.Path, t.State, t.Enabled is { } enabled ? enabled ? "Yes" : "No" : "—",
                        t.Triggers.Length == 0 ? "None reported" : t.Triggers,
                        FullText: $"{t.Name}\n{t.Path}\nState: {t.State}\nEnabled: {t.Enabled?.ToString() ?? "Unavailable"}\nActions: {t.Actions}\nTriggers: {t.Triggers}\n{t.Status}: {t.Detail}\nCaptured {result.CapturedAt:O}\nArguments are not collected. Nothing is executed or modified.")),
                    "Task / path", "State", "Enabled", "Triggers");
            });
            return;
        }
        if (kind != "Pagefiles") return;
        await ShowInventoryAsync("Pagefile usage", async (view, token) =>
        {
            var result = await pagefiles.CaptureAsync(token);
            view.ShowRows($"{result.Status} · {result.CapturedAt.LocalDateTime:T} · {result.Detail}",
                result.Pagefiles.Select(p => new InventoryRow(p.Name, "Pagefile-backed storage, not additional RAM",
                    DiagnosticData.Bytes(p.TotalBytes), DiagnosticData.Bytes(p.InUseBytes), DiagnosticData.Bytes(p.PeakUsageBytes),
                    p.TotalBytes, p.InUseBytes, p.PeakUsageBytes,
                    $"{p.Name}\nSize: {p.TotalBytes:N0} bytes\nIn use: {p.InUseBytes:N0} bytes\nHistorical peak: {p.PeakUsageBytes:N0} bytes\nCaptured {result.CapturedAt:O}\nNot system commit, virtual-region span or physical RAM; historical peaks are not clamped.")),
                "Pagefile", "Size", "In use", "Peak use");
        });
    }

    private async void ShowLifecycleTimeline()
    {
        var events = lifecycle.Events;
        var excluded = lifecycle.UnstableIdentitiesExcluded;
        var overflow = lifecycle.TrackingOverflow;
        await ShowInventoryAsync("Process start / disappearance timeline", (view, _) =>
        {
            static string Kind(ProcessLifecycleKind kind) => kind switch
            {
                ProcessLifecycleKind.Started => "Started",
                ProcessLifecycleKind.NoLongerObserved => "No longer observed",
                _ => "Running at first observation"
            };
            view.ShowRows($"Frozen at open · session-only, newest first, at most {ProcessLifecycleTimeline.MaximumEvents} events · identity = PID + creation time. \"No longer observed\" is not confirmed exit or crash. {excluded} current process(es) without a stable creation time are excluded{(overflow > 0 ? $"; {overflow} identities exceeded tracking bounds" : "")}.",
                events.Select(e =>
                {
                    var lifetime = e.LastObserved - e.FirstObserved;
                    return new InventoryRow($"{e.Name} · PID {e.Identity.Pid}",
                        $"{Kind(e.Kind)}{(e.AfterGap ? " · after sampling gap" : "")}",
                        e.At.LocalDateTime.ToString("HH:mm:ss"), e.Kind == ProcessLifecycleKind.NoLongerObserved ? $"{lifetime.TotalSeconds:F0} s observed" : "—",
                        DiagnosticData.Bytes(e.PeakPrivateBytes),
                        e.At.ToUnixTimeMilliseconds(), e.Kind == ProcessLifecycleKind.NoLongerObserved ? (decimal)lifetime.TotalSeconds : null, e.PeakPrivateBytes,
                        $"{e.Name} · PID {e.Identity.Pid} · creation {e.Identity.CreationTimeFileTime}\n{Kind(e.Kind)} at {e.At:O}{(e.AfterGap ? " (after a sampling gap; the transition time is uncertain)" : "")}\nProcess creation time: {e.CreatedAt?.ToString("O") ?? "Unavailable"}\nParent PID at first observation: {e.ParentPid?.ToString() ?? "Unavailable"} (parent PID values can be reused)\nPath: {e.ExecutablePath ?? "Unavailable"}\nObserved {e.FirstObserved:O} – {e.LastObserved:O} in {e.Observations} sample(s)\nPeak observed private commit: {DiagnosticData.Bytes(e.PeakPrivateBytes)} · working set: {DiagnosticData.Bytes(e.PeakWorkingSetBytes)} · CPU: {DiagnosticData.Percent(e.PeakCpuPercent)}\nPeaks are maxima of retained samples, not true lifetime peaks. Absence is not proof of exit; processes shorter than one sampling interval are never observed.",
                        e.Identity);
                }),
                "Process", "Event time", "Lifetime", "Peak private", reverse: true, initialSort: 1);
            if (events.Count == 0) view.ShowMessage("No process lifetimes have been observed yet.");
            return Task.CompletedTask;
        });
    }

    private async void ThreadsClicked(object sender, RoutedEventArgs args)
    {
        if (selectedIdentity is not { IsStable: true } identity) return;
        await ShowInventoryAsync($"Threads · PID {identity.Pid}", async (view, token) =>
        {
            var result = await threadDiagnostics.CaptureAsync(identity, token);
            void ShowThreads() => view.ShowRows($"{result.Status} · {result.CapturedAt.LocalDateTime:T} · 100% = one logical CPU, unlike the machine-normalized Processes table. Kernel/user are cumulative CPU seconds. {result.Detail}",
                result.Threads.Select(t => new InventoryRow($"Thread {t.Identity.ThreadId}", $"{t.Status} · creation {t.Identity.CreationTimeFileTime}",
                    DiagnosticData.Percent(t.CpuPercent), ThreadTime(t.KernelTimeTicks), ThreadTime(t.UserTimeTicks),
                    t.CpuPercent is { } cpu ? (decimal)cpu : null, t.KernelTimeTicks, t.UserTimeTicks,
                    $"PID {identity.Pid} · process creation {identity.CreationTimeFileTime}\nTID {t.Identity.ThreadId} · thread creation {t.Identity.CreationTimeFileTime}\nCPU: {DiagnosticData.Percent(t.CpuPercent)} (100% = one logical CPU)\nKernel: {ThreadTime(t.KernelTimeTicks)} · User: {ThreadTime(t.UserTimeTicks)}\n{t.Status}: {t.Detail}\nCaptured {result.CapturedAt:O}; total capture span {result.SampleInterval.TotalMilliseconds:F0} ms, not the individual CPU denominator.",
                    t.Identity.IsStable ? t.Identity : null)),
                "Thread identity", "CPU %", "Kernel total", "User total", action: "Inspect selected wait chain", initialSort: 1, reverse: true);
            var busy = false;
            view.BackRequested += ShowThreads;
            view.ActionRequested += async row =>
            {
                if (busy || row.Value is not ThreadIdentity thread) return;
                busy = true;
                view.ShowMessage("Inspecting only this selected thread. Close cancels; safe native cleanup can take longer than the cooperative budget.");
                try
                {
                    var chain = await threadDiagnostics.CaptureWaitChainAsync(thread, token);
                    token.ThrowIfCancellationRequested();
                    var cycle = chain.CycleDetected is { } detected ? detected ? "Cycle reported" : "No cycle reported" : "Cycle unavailable";
                    view.ShowRows($"{chain.Status} · {chain.CapturedAt.LocalDateTime:T} · TID {thread.ThreadId} · {cycle}. {chain.Detail}",
                        chain.Nodes.Select(n => new InventoryRow($"{n.Ordinal + 1:D2} · {WaitType(n.ObjectType)}",
                            n.ObjectName ?? (n.ThreadId is not null ? "Native PID/TID, not independently attributed" : "Unnamed or unavailable object"),
                            WaitStatus(n.ObjectStatus), n.ProcessId?.ToString() ?? "—", n.ThreadId?.ToString() ?? "—",
                            null, n.ProcessId, n.ThreadId,
                            $"Chain position {n.Ordinal + 1}\nObject: {WaitType(n.ObjectType)} ({n.ObjectType})\nStatus: {WaitStatus(n.ObjectStatus)} ({n.ObjectStatus})\nName: {n.ObjectName ?? "Unnamed or unavailable"}\nNative PID: {n.ProcessId?.ToString() ?? "—"} · TID: {n.ThreadId?.ToString() ?? "—"}\nForeign identities are not verified. One node does not establish absence of a problem. Cycles are observations, not root-cause diagnoses.")),
                        "Chain position / object", "Status", "Native PID", "Native TID", back: true);
                }
                catch (OperationCanceledException) { view.ShowMessage("Cancelled; no wait-chain result retained."); }
                catch (Exception ex) { view.ShowMessage($"Wait chain unavailable: {ex.Message}", back: true); }
                finally { busy = false; }
            };
            ShowThreads();
        });
    }

    private static string ThreadTime(long? ticks) => ticks is { } value ? $"{value / (double)TimeSpan.TicksPerSecond:F3} s" : "—";
    private static string WaitType(uint type) => type switch
    {
        1 => "Critical section", 2 => "Send message", 3 => "Mutex", 4 => "ALPC", 5 => "COM", 6 => "Thread wait",
        7 => "Process wait", 8 => "Thread", 9 => "COM activation", _ => $"Unknown ({type})"
    };
    private static string WaitStatus(uint status) => status switch
    {
        1 => "No access", 2 => "Running", 3 => "Blocked", 4 => "PID only", 5 => "PID only (RPCSS)",
        6 => "Owned", 7 => "Not owned", 8 => "Abandoned", 9 => "Unknown", 10 => "Error", _ => $"Unknown ({status})"
    };

    private async void InspectService(string name)
    {
        await ShowInventoryAsync($"Service configuration · {name}", async (view, token) =>
        {
            var result = await serviceInspection.CaptureAsync(name, token);
            var rows = new List<InventoryRow>();
            void Field(string label, string? value, string note = "") => rows.Add(new(label, value ?? "Unavailable", FullText: $"{label}: {value ?? "Unavailable"}\n{note}"));
            Field("Binary command (unexpanded)", result.BinaryPath, "Local configuration only. The target is not resolved, opened or executed.");
            Field("Account / driver object", result.Account, "Driver services can report an object name rather than a logon account.");
            Field("Load-order group", result.LoadOrderGroup);
            Field("Service type", result.ServiceType is { } type ? $"0x{type:X8}" : null);
            Field("Base start type", result.StartType is { } start ? start switch
            {
                0 => "Boot (0)", 1 => "System (1)", 2 => "Automatic (2)", 3 => "Demand (3)", 4 => "Disabled (4)", _ => $"Unknown ({start})"
            } : null, "Base mode does not include delayed auto-start or trigger configuration.");
            Field("Error control", result.ErrorControl is { } error ? $"{error}" : null);
            foreach (var dependency in result.Dependencies)
                Field(dependency.StartsWith('+') ? "Load-order group dependency" : "Service dependency", dependency,
                    "Observed configuration only; not proof of running state or ownership. Leading + denotes a load-order group, not a service name.");
            if (result.Status == InspectionStatus.Available && result.Dependencies.Count == 0)
                Field("Dependencies", "None reported");
            view.ShowRows($"{result.Status} · {result.CapturedAt.LocalDateTime:T} · {result.Detail}", rows,
                "Configuration / dependency", "", "", "");
        });
    }
}
