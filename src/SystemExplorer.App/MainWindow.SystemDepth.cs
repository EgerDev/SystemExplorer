using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;
using SystemExplorer.Relationships;
using SystemExplorer.Windows;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly ISystemCounterProvider systemCounters = new WindowsSystemCounterProvider();
    private readonly IHandleInventoryProvider handleInventory = new WindowsHandleInventoryProvider();
    private readonly IPoolTagProvider poolTags = new WindowsPoolTagProvider();
    private readonly IThreadActivityProvider threadActivity = new WindowsThreadActivityProvider();
    private readonly IFileUsageProvider fileUsage = new WindowsFileUsageProvider();
    private readonly IProcessSecurityProvider processSecurity = new WindowsProcessSecurityProvider();
    private readonly IDriverServiceProvider driverServices = new WindowsDriverServiceProvider();
    private readonly IPersistenceInventoryProvider persistence = new WindowsPersistenceProvider();
    private readonly IIoAttributionCapture ioAttribution = new WindowsIoAttributionCapture();

    private async Task<SystemCounterSnapshot> CaptureCountersAsync(CancellationToken token)
    {
        var result = await systemCounters.CaptureAsync(token);
        if (!result.IsWarmup) return result;
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        return await systemCounters.CaptureAsync(token);
    }

    private static string V(CounterValue value, Func<double, string> format) => value.Value is { } x ? format(x) : value.State switch
    {
        CounterValueState.Warmup => "Warming up",
        CounterValueState.NoActivityInInterval => "No I/O in interval",
        _ => "Unavailable"
    };
    private static decimal? N(CounterValue value) => value.Value is { } x && double.IsFinite(x) ? (decimal)x : null;
    private static string Ms(double value) => $"{value:F2} ms";
    private static string Pct(double value) => $"{value:F1}%";
    private static string Num(double value) => $"{value:F1}";
    private static string Rate(double value) => Display.Rate(value);
    private static string B(double value) => DiagnosticData.Bytes((ulong)Math.Max(0, value));
    private static string Why(CounterValue value) => value.Reason is { Length: > 0 } reason ? $" ({reason})" : "";

    private async void ShowSystemDepth(string kind)
    {
        switch (kind)
        {
            case "IoAttribution":
                if (detailsOpen) return;
                detailsOpen = true;
                var durations = new ComboBox { ItemsSource = new[] { "10 s", "30 s", "60 s" }, SelectedIndex = 0, Header = "Capture duration", HorizontalAlignment = HorizontalAlignment.Stretch };
                var consent = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Capture I/O (admin)",
                    PrimaryButtonText = "Capture", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "Administrator-only, opt-in, bounded local capture. Observes per-process traffic and requested file I/O byte counts only: no addresses, ports, file names or contents are retained. Nothing is saved. No elevation is attempted. Close the capture dialog to cancel and stop the session.", TextWrapping = TextWrapping.Wrap },
                            durations
                        }
                    }
                };
                ContentDialogResult choice;
                try { choice = await consent.ShowAsync(); }
                finally { detailsOpen = false; }
                if (choice != ContentDialogResult.Primary || shutdown.IsCancellationRequested) return;
                var seconds = new[] { 10, 30, 60 }[durations.SelectedIndex];
                await ShowInventoryAsync("Per-process I/O attribution", async (view, token) =>
                {
                    view.ShowMessage($"Capturing for {seconds} s. Close / cancel stops the private ETW session. Nothing is saved.");
                    var result = await ioAttribution.CaptureAsync(TimeSpan.FromSeconds(seconds), token);
                    if (!token.IsCancellationRequested) view.Content = new Views.IoAttributionView(result);
                });
                break;
            case "Disk":
                await ShowInventoryAsync("Disk latency and queue", async (view, token) =>
                {
                    var r = await CaptureCountersAsync(token);
                    var rows = (r.Disks.Total is { } total ? new[] { total } : []).Concat(r.Disks.Disks);
                    view.ShowRows($"{r.Disks.Status} · {r.CapturedAt.LocalDateTime:T} · interval {r.Interval?.TotalSeconds:F1} s · {r.Disks.Detail} Aggregate physical-disk counters; not attributed to processes.",
                        rows.Select(d => new InventoryRow(d.IsAggregate ? "All physical disks" : d.Instance, $"Queue {V(d.CurrentQueueLength, Num)} · idle {V(d.IdleTimePercent, Pct)} · read {V(d.ReadBytesPerSecond, Rate)} · write {V(d.WriteBytesPerSecond, Rate)}",
                            V(d.ReadLatencyMilliseconds, Ms), V(d.WriteLatencyMilliseconds, Ms), V(d.CurrentQueueLength, Num),
                            N(d.ReadLatencyMilliseconds), N(d.WriteLatencyMilliseconds), N(d.CurrentQueueLength),
                            $"{d.Instance}\nAvg read latency: {V(d.ReadLatencyMilliseconds, Ms)}{Why(d.ReadLatencyMilliseconds)}\nAvg write latency: {V(d.WriteLatencyMilliseconds, Ms)}{Why(d.WriteLatencyMilliseconds)}\nReads/s: {V(d.ReadsPerSecond, Num)} · Writes/s: {V(d.WritesPerSecond, Num)}\nCurrent queue length: {V(d.CurrentQueueLength, Num)}\nRead: {V(d.ReadBytesPerSecond, Rate)} · Write: {V(d.WriteBytesPerSecond, Rate)}\nIdle: {V(d.IdleTimePercent, Pct)}\nLatency averages cover only completed I/O in the interval; \"No I/O\" is not 0 ms.")),
                        "Disk", "Read latency", "Write latency", "Queue", initialSort: 2, reverse: true);
                });
                break;
            case "Adapters":
                await ShowInventoryAsync("Network adapter throughput", async (view, token) =>
                {
                    var r = await CaptureCountersAsync(token);
                    var n = r.Network;
                    view.ShowRows($"{n.Status} · {r.CapturedAt.LocalDateTime:T} · total receive {V(n.TotalReceiveBytesPerSecond, Rate)}, send {V(n.TotalTransmitBytesPerSecond, Rate)} (hardware interfaces only) · {n.Detail} Interface totals; not per-process bandwidth.",
                        n.Adapters.Select(a => new InventoryRow(a.Alias, $"{a.Kind} · {a.OperationalStatus}{(a.IncludedInTotal ? "" : " · excluded from total")} · {a.Description}",
                            V(a.ReceiveBytesPerSecond, Rate), V(a.TransmitBytesPerSecond, Rate), a.ReceiveLinkSpeedBitsPerSecond is { } s ? $"{s / 1e6:F0} Mb/s" : "—",
                            N(a.ReceiveBytesPerSecond), N(a.TransmitBytesPerSecond), a.ReceiveLinkSpeedBitsPerSecond,
                            $"{a.Alias} · {a.Description}\nIndex {a.InterfaceIndex} · LUID 0x{a.InterfaceLuid:X} · type {a.InterfaceType} · {a.Kind}\nStatus: {a.OperationalStatus}\nReceive: {V(a.ReceiveBytesPerSecond, Rate)}{Why(a.ReceiveBytesPerSecond)} · Send: {V(a.TransmitBytesPerSecond, Rate)}{Why(a.TransmitBytesPerSecond)}\nLink speed rx/tx: {a.ReceiveLinkSpeedBitsPerSecond?.ToString("N0") ?? "Unknown"} / {a.TransmitLinkSpeedBitsPerSecond?.ToString("N0") ?? "Unknown"} bit/s\nIncluded in total: {a.IncludedInTotal} (loopback, virtual and filter interfaces are excluded to avoid double counting).")),
                        "Adapter", "Receive", "Send", "Link speed", initialSort: 1, reverse: true);
                });
                break;
            case "Cores":
                await ShowInventoryAsync("Logical processors", async (view, token) =>
                {
                    var r = await CaptureCountersAsync(token);
                    var c = r.Cpu;
                    view.ShowRows($"{c.Status} · {r.CapturedAt.LocalDateTime:T} · total utility {V(c.TotalProcessorUtilityPercent, Pct)}, privileged {V(c.TotalPrivilegedTimePercent, Pct)} · context switches {V(c.ContextSwitchesPerSecond, Num)}/s · processor queue {V(c.ProcessorQueueLength, Num)} · {c.Detail} Utility can exceed 100% when cores run above base frequency.",
                        c.LogicalProcessors.Select(p => new InventoryRow($"CPU {p.Instance}", p.Group is { } g ? $"Group {g} · number {p.Number}" : "Group unavailable",
                            V(p.ProcessorUtilityPercent, Pct), V(p.PrivilegedTimePercent, Pct), "",
                            N(p.ProcessorUtilityPercent), N(p.PrivilegedTimePercent), null,
                            $"Logical processor {p.Instance}\nProcessor utility: {V(p.ProcessorUtilityPercent, Pct)}{Why(p.ProcessorUtilityPercent)}\nPrivileged (kernel) time: {V(p.PrivilegedTimePercent, Pct)}{Why(p.PrivilegedTimePercent)}\nMeasured as reported; values above 100% are not clamped.")),
                        "Logical processor", "Utility", "Privileged", "");
                });
                break;
            case "Gpu":
                await ShowInventoryAsync("GPU engines and memory", async (view, token) =>
                {
                    var r = await CaptureCountersAsync(token);
                    var g = r.Gpu;
                    var rows = g.Adapters.Select(a => new InventoryRow(a.Description ?? $"Adapter 0x{a.Luid:X}", $"Busiest engine: {a.HeadlineEngineType ?? "—"} · {string.Join(", ", a.Engines.Select(e => $"{e.EngineType} {V(e.UtilizationPercent, Pct)}"))}",
                        V(a.HeadlineUtilizationPercent, Pct), V(a.DedicatedUsageBytes, B), V(a.SharedUsageBytes, B),
                        N(a.HeadlineUtilizationPercent), N(a.DedicatedUsageBytes), N(a.SharedUsageBytes),
                        $"{a.Description ?? "Description unavailable"} · LUID 0x{a.Luid:X}\nBusiest engine type: {a.HeadlineEngineType ?? "—"} at {V(a.HeadlineUtilizationPercent, Pct)} (maximum over engine types, not a sum)\nDedicated in use: {V(a.DedicatedUsageBytes, B)} of {DiagnosticData.Bytes(a.DedicatedVideoMemoryBytes)}\nShared in use: {V(a.SharedUsageBytes, B)} of {DiagnosticData.Bytes(a.SharedSystemMemoryBytes)} (shared GPU memory is system RAM, not additional memory)"))
                        .Concat(g.Processes.Select(p => new InventoryRow($"PID {p.Pid}", $"{(p.Identity is null ? $"{p.IdentityStatus}: {p.IdentityDetail}" : "Identity verified")} · busiest {p.MaxEngineType ?? "—"}",
                            V(p.MaxUtilizationPercent, Pct), V(p.DedicatedUsageBytes, B), V(p.SharedUsageBytes, B),
                            N(p.MaxUtilizationPercent), N(p.DedicatedUsageBytes), N(p.SharedUsageBytes),
                            $"PID {p.Pid} · creation {p.Identity?.CreationTimeFileTime.ToString() ?? "unverified"} · adapter 0x{p.AdapterLuid:X}\n{string.Join("\n", p.Engines.Select(e => $"{e.EngineType}: {V(e.UtilizationPercent, Pct)}"))}\nDedicated: {V(p.DedicatedUsageBytes, B)} · Shared: {V(p.SharedUsageBytes, B)}",
                            p.Identity)));
                    view.ShowRows($"{g.Status} · {r.CapturedAt.LocalDateTime:T} · {g.Detail} {g.InventoryDetail}{(g.MalformedInstanceCount > 0 ? $" {g.MalformedInstanceCount} malformed counter instance(s) skipped." : "")}",
                        rows, "Adapter / process", "Busiest engine", "Dedicated", "Shared", initialSort: 1, reverse: true);
                });
                break;
            case "MemoryLists":
                await ShowInventoryAsync("Memory lists and paging", async (view, token) =>
                {
                    var m = (await CaptureCountersAsync(token)).Memory;
                    InventoryRow Row(string name, CounterValue value, Func<double, string> format, string note) =>
                        new(name, note, V(value, format), Number1: N(value), FullText: $"{name}: {V(value, format)}{Why(value)}\n{note}");
                    view.ShowRows($"{m.Status} · {m.Detail}", [
                        Row("Standby (reserve priority)", m.StandbyCacheReserveBytes, B, "Cached pages that can be repurposed; counted as available."),
                        Row("Standby (normal priority)", m.StandbyCacheNormalPriorityBytes, B, "Cached pages that can be repurposed; counted as available."),
                        Row("Standby (core)", m.StandbyCacheCoreBytes, B, "Cached pages that can be repurposed; counted as available."),
                        Row("Modified page list", m.ModifiedPageListBytes, B, "Dirty pages that must be written before reuse."),
                        Row("Free and zero page lists", m.FreeAndZeroPageListBytes, B, "Immediately usable pages."),
                        Row("Page faults/s", m.PageFaultsPerSecond, Num, "Soft and hard faults combined."),
                        Row("Page reads/s (hard faults)", m.PageReadsPerSecond, Num, "Disk read operations to resolve hard faults."),
                        Row("Pages input/s", m.PagesInputPerSecond, Num, "Pages read from disk to resolve hard faults."),
                        Row("Memory Compression store working set", m.CompressionStoreResidentWorkingSetBytes, B, $"{m.CompressionStatus}: {m.CompressionDetail} Resident size of the store, not bytes saved.")],
                        "Counter", "Value", "", "");
                });
                break;
            case "PoolTags":
                await ShowInventoryAsync("Kernel pool tags", async (view, token) =>
                {
                    var r = await poolTags.CaptureAsync(token);
                    view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.Detail} Tags identify allocating code by convention only; no driver attribution is downloaded.",
                        r.Tags.Select(t => new InventoryRow(t.Tag, $"Paged allocs {t.PagedAllocations - Math.Min(t.PagedAllocations, t.PagedFrees):N0} outstanding · nonpaged {t.NonPagedAllocations - Math.Min(t.NonPagedAllocations, t.NonPagedFrees):N0} outstanding",
                            DiagnosticData.Bytes(t.PagedUsedBytes), DiagnosticData.Bytes(t.NonPagedUsedBytes), DiagnosticData.Bytes(t.PagedUsedBytes + t.NonPagedUsedBytes),
                            t.PagedUsedBytes, t.NonPagedUsedBytes, t.PagedUsedBytes + t.NonPagedUsedBytes,
                            $"Tag {t.Tag} (0x{t.RawTag:X8})\nPaged: {t.PagedUsedBytes:N0} bytes · {t.PagedAllocations:N0} allocs / {t.PagedFrees:N0} frees\nNonpaged: {t.NonPagedUsedBytes:N0} bytes · {t.NonPagedAllocations:N0} allocs / {t.NonPagedFrees:N0} frees")),
                        "Tag", "Paged", "Nonpaged", "Total", initialSort: 3, reverse: true);
                });
                break;
            case "FileUsers":
            case "DllUsers":
                var dll = kind == "DllUsers";
                var query = await PromptAsync(dll ? "Find processes that loaded a DLL" : "Find processes using a file",
                    dll ? "Module name or path fragment, e.g. user32.dll" : @"Local absolute file path, e.g. C:\data\report.xlsx");
                if (string.IsNullOrWhiteSpace(query)) return;
                await ShowInventoryAsync(dll ? $"Module users · {query}" : $"File users · {query}", async (view, token) =>
                {
                    if (dll)
                    {
                        var r = await fileUsage.FindModuleUsersAsync(query, token);
                        view.ShowRows($"{r.Status} · {r.Detail}", r.Matches.Select(m => new InventoryRow(ProcessName(m.Identity), m.ModulePath,
                            $"PID {m.Identity.Pid}", $"0x{m.BaseAddress:X}", "", m.Identity.Pid, null, null,
                            $"PID {m.Identity.Pid} · creation {m.Identity.CreationTimeFileTime}\n{m.ModuleName}\n{m.ModulePath}\nBase 0x{m.BaseAddress:X}", m.Identity)),
                            "Process", "PID", "Base", "");
                    }
                    else
                    {
                        var r = await fileUsage.FindFileUsersAsync(query, token);
                        view.ShowRows($"{r.Status} · {r.Detail} Restart Manager reports registered users only; it is read-only here and nothing is closed or restarted.",
                            r.Users.Select(u => new InventoryRow(u.AppName ?? ProcessName(u.Identity), u.ServiceShortName is { } svc ? $"Service {svc}" : u.AppType.ToString(),
                                $"PID {u.Identity.Pid}", u.AppType.ToString(), "", u.Identity.Pid, null, null,
                                $"{u.AppName} · PID {u.Identity.Pid} · creation {u.Identity.CreationTimeFileTime}\nType: {u.AppType} ({u.RawAppType}) · service: {u.ServiceShortName ?? "—"}", u.Identity)),
                            "Process", "PID", "Type", "");
                    }
                });
                break;
            case "Drivers":
                await ShowInventoryAsync("Driver services", async (view, token) =>
                {
                    var r = await driverServices.CaptureAsync(token);
                    view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.Detail} Configuration only; driver files are not opened.",
                        r.Drivers.Select(d => new InventoryRow(d.DisplayName ?? d.Name, d.ImagePath ?? "Image path unavailable",
                            d.CurrentState == 4 ? "Running" : d.CurrentState == 1 ? "Stopped" : $"State {d.CurrentState}", StartName(d.StartType), d.LoadOrderGroup ?? "—",
                            FullText: $"{d.Name} · {d.DisplayName}\nType 0x{d.ServiceType:X} · state {d.CurrentState} · start {StartName(d.StartType)} · error control {d.ErrorControl?.ToString() ?? "—"}\nImage (unexpanded): {d.ImagePath ?? "Unavailable"}\nGroup: {d.LoadOrderGroup ?? "—"}\n{d.ConfigStatus}: {d.ConfigDetail}")),
                        "Driver / image", "State", "Start", "Group");
                });
                break;
            case "Persistence":
                await ShowInventoryAsync("Other persistence locations", async (view, token) =>
                {
                    var r = await persistence.CaptureAsync(token);
                    var denied = r.Sources.Where(s => s.Status is not InspectionStatus.Available).Select(s => $"{s.Source}/{s.Scope}: {s.Status} — {s.Detail}");
                    view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.Detail} {string.Join("; ", denied)} Presence is not evidence of compromise; KnownDLLs rows are reference values.",
                        r.Entries.Select(e => new InventoryRow(e.Name, e.Location, e.Source.ToString(), e.Scope.ToString(), e.IsReference ? "Reference" : e.Status.ToString(),
                            FullText: $"{e.Source} · {e.Scope}\n{e.Location}\n{e.Name} = {e.Value ?? "(no value)"}\n{string.Join("\n", e.Properties.Select(p => $"{p.Key}: {p.Value}"))}\n{e.Status}: {e.Detail}\nRaw and unexpanded; nothing is resolved or executed.")),
                        "Name / location", "Source", "Scope", "Status");
                });
                break;
        }
    }

    private static string StartName(uint? start) => start switch { 0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Demand", 4 => "Disabled", null => "—", _ => $"{start}" };
    private string ProcessName(ProcessIdentity identity) => Model.Snapshot?.Processes.FirstOrDefault(p => p.Identity == identity)?.Name ?? $"PID {identity.Pid}";

    private async Task<string?> PromptAsync(string title, string placeholder)
    {
        if (detailsOpen) return null;
        var box = new TextBox { PlaceholderText = placeholder, MinWidth = 420 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = title, Content = box,
            PrimaryButtonText = "Search", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private async void SecurityClicked(object sender, RoutedEventArgs args)
    {
        if (selectedIdentity is not { IsStable: true } identity) return;
        await ShowInventoryAsync($"Security and provenance · PID {identity.Pid}", async (view, token) =>
        {
            var r = await processSecurity.CaptureAsync(identity, token);
            var rows = new List<InventoryRow>();
            void Add(string name, string value, string status, string note) => rows.Add(new(name, value, status, FullText: $"{name}: {value}\n{status}\n{note}"));
            var t = r.Token;
            Add("Command line", r.CommandLine.LengthCharacters is { } len ? $"{len} characters · select this row, then Show command line" : "Unavailable", r.CommandLine.Status.ToString(), r.CommandLine.Detail);
            Add("User", t.UserAccount ?? t.UserSid ?? "Unavailable", t.Status.ToString(), t.Detail);
            Add("Integrity level", t.Integrity?.ToString() ?? "Unavailable", t.Status.ToString(), $"RID 0x{t.IntegrityRid:X}");
            Add("Elevation", t.IsElevated is { } e ? $"{(e ? "Elevated" : "Not elevated")} · {t.ElevationType}" : "Unavailable", t.Status.ToString(), "");
            Add("AppContainer / package", $"{t.IsAppContainer?.ToString() ?? "—"} / {t.HasPackageIdentity?.ToString() ?? "—"}", t.Status.ToString(), "");
            Add("Protection", r.Protection.LevelName ?? "Unavailable", r.Protection.Status.ToString(), r.Protection.Detail);
            Add("In a job object", r.Job.InAnyJob?.ToString() ?? "Unavailable", r.Job.Status.ToString(), r.Job.Detail);
            Add("Privileges", t.Privileges.Count > 0 ? $"{t.Privileges.Count} reported" : t.PrivilegesStatus == InspectionStatus.Available ? "None reported" : "Unavailable", t.PrivilegesStatus.ToString(), t.Detail);
            Add("Mitigations", r.Mitigations.Policies.Count > 0 ? $"{r.Mitigations.Policies.Count} policies reported" : r.Mitigations.Status == InspectionStatus.Available ? "None reported" : "Unavailable", r.Mitigations.Status.ToString(), r.Mitigations.Detail);
            Add("Runtime (.NET)", r.Runtime.Modules.Count > 0 ? $"{r.Runtime.Modules.Count} modules reported" : r.Runtime.Status == InspectionStatus.Available ? "None reported" : "Unavailable", r.Runtime.Status.ToString(), $"{r.Runtime.Detail}\n{r.Runtime.Source}");
            foreach (var p in t.Privileges)
                Add($"Privilege {p.Name}", p.Enabled ? "Enabled" : p.Removed ? "Removed" : "Present, disabled", t.PrivilegesStatus.ToString(), $"Enabled by default: {p.EnabledByDefault}. Nothing is enabled or changed.");
            foreach (var m in r.Mitigations.Policies)
                Add($"Mitigation {m.Kind}", m.Flags.Count == 0 ? "No flags reported" : string.Join(", ", m.Flags.Where(f => f.Value).Select(f => f.Name).DefaultIfEmpty("All off")), m.Status.ToString(), $"{m.Detail} Raw 0x{m.RawFlags:X}");
            foreach (var m in r.Runtime.Modules)
                Add($"Runtime {m.RuntimeFamily}", $"{m.ModuleName} {m.FileVersion}", r.Runtime.Status.ToString(), $"{m.Path}\n{r.Runtime.Source}");
            if (r.Image.Provenance is { } i)
                Add("Image signature", $"{i.Signature}{(i.SignerSubject is { } s ? $" · {s}" : "")}", i.Status.ToString(), $"{i.Path}\n{i.CompanyName} · {i.ProductName} · {i.FileVersion}\nCatalog: {i.CatalogPath ?? "—"}\n{i.Detail}");
            else Add("Image signature", "Unavailable", r.Image.Status.ToString(), r.Image.Detail);
            var commandText = r.CommandLine.Text;
            if (commandText is not null) rows[0] = rows[0] with { Value = "reveal" };
            view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.Detail}", rows, "Property", "Status", "", "", action: commandText is null ? null : "Show command line");
            view.ActionRequested += row =>
            {
                if (commandText is null || row.Value is not "reveal") return;
                view.ShowRows("Command lines can contain secrets. Shown on request only; not logged or retained after this dialog closes.",
                    [new InventoryRow("Command line", commandText, FullText: commandText)], "Command line", "", "", "", back: true);
            };
            view.BackRequested += () => view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.Detail}", rows, "Property", "Status", "", "", action: commandText is null ? null : "Show command line");
        });
    }

    private async void HandlesClicked(object sender, RoutedEventArgs args)
    {
        if (selectedIdentity is not { IsStable: true } identity) return;
        await ShowInventoryAsync($"Handles · PID {identity.Pid}", async (view, token) =>
        {
            var r = await handleInventory.CaptureAsync(identity, token);
            view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.ProcessHandleCount?.ToString("N0") ?? "—"} handles in this process, {r.SystemHandleCount?.ToString("N0") ?? "—"} system-wide · {r.Detail}",
                r.ProcessCountsByType.Select(c => new InventoryRow(c.TypeName ?? $"Type {c.TypeIndex}", $"Type index {c.TypeIndex}",
                    c.HandleCount.ToString("N0"), r.SystemCountsByType.FirstOrDefault(s => s.TypeIndex == c.TypeIndex)?.HandleCount.ToString("N0") ?? "—", "",
                    c.HandleCount, r.SystemCountsByType.FirstOrDefault(s => s.TypeIndex == c.TypeIndex)?.HandleCount, null)),
                "Object type", "This process", "System-wide", "", initialSort: 1, reverse: true);
        });
    }

    private async void ActivityClicked(object sender, RoutedEventArgs args)
    {
        if (selectedIdentity is not { IsStable: true } identity) return;
        await ShowInventoryAsync($"Thread activity · PID {identity.Pid}", async (view, token) =>
        {
            var r = await threadActivity.CaptureAsync(identity, token);
            view.ShowRows($"{r.Status} · {r.CapturedAt.LocalDateTime:T} · {r.SampleInterval.TotalMilliseconds:F0} ms sample · process cycles +{r.ProcessCycleDelta?.ToString("N0") ?? "—"} · {r.Detail} Cycle counts are raw CPU cycles, not time.",
                r.Threads.Select(t => new InventoryRow($"Thread {t.Identity.ThreadId}", $"{ObjectInventoryNames.ThreadStateName(t.ThreadState) ?? "State unavailable"} · {ObjectInventoryNames.WaitReasonName(t.WaitReason) ?? "—"} · priority {t.CurrentPriority}/{t.BasePriority}",
                    t.ContextSwitchesPerSecond is { } cs ? $"{cs:F0}/s" : "—", t.CycleDelta?.ToString("N0") ?? "—", t.ContextSwitches?.ToString("N0") ?? "—",
                    t.ContextSwitchesPerSecond is { } c ? (decimal)c : null, t.CycleDelta, t.ContextSwitches,
                    $"TID {t.Identity.ThreadId} · creation {t.Identity.CreationTimeFileTime}\n{t.Status}: {t.Detail}\nContext switches: {t.ContextSwitches} (+{t.ContextSwitchDelta})\nCycles: {t.CycleTime} (+{t.CycleDelta})")),
                "Thread", "Switches/s", "Cycles in sample", "Total switches", initialSort: 1, reverse: true);
        });
    }
}
