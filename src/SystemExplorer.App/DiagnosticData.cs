using System.Collections.ObjectModel;
using System.ComponentModel;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed record DiagnosticItem(string Key, ProcessIdentity? Identity, string Name, string Detail,
    int? Pid, string First, string Second, string Third,
    decimal? Number1 = null, decimal? Number2 = null, decimal? Number3 = null);

public sealed class DiagnosticRow : INotifyPropertyChanged
{
    public DiagnosticItem Item { get; private set; }
    public string Key => Item.Key;
    public string Name => Item.Name;
    public string Detail => Item.Detail;
    public string Pid => Item.Pid?.ToString() ?? "—";
    public string First => Item.First;
    public string Second => Item.Second;
    public string Third => Item.Third;
    public bool CanInspect => Item.Identity is { IsStable: true };
    public string Description => $"{Name}\n{Detail}\nPID {Pid} · {First} · {Second} · {Third}";
    public DiagnosticRow(DiagnosticItem item) => Item = item;
    public void Update(DiagnosticItem item)
    {
        if (Item == item) return;
        Item = item;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DiagnosticRows
{
    public ObservableCollection<DiagnosticRow> Rows { get; } = new();

    public void Apply(IEnumerable<DiagnosticItem> items, bool reorder)
    {
        var desired = items.ToArray();
        var keys = desired.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keys.Contains(Rows[i].Key)) Rows.RemoveAt(i);
        var existing = Rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        for (var i = 0; i < desired.Length; i++)
        {
            var item = desired[i];
            if (existing.TryGetValue(item.Key, out var row))
            {
                row.Update(item);
                if (reorder && Rows.IndexOf(row) != i) Rows.Move(Rows.IndexOf(row), i);
            }
            else if (reorder) Rows.Insert(i, new DiagnosticRow(item));
            else Rows.Add(new DiagnosticRow(item));
        }
    }
}

public static class DiagnosticData
{
    public static ResourceTrend BuildTrend(IReadOnlyList<HistoryPoint> history, Func<HistoryPoint, double?> value, double maximum)
    {
        var segments = new List<IReadOnlyList<TrendCoordinate>>();
        List<TrendCoordinate>? segment = null;
        var observed = new List<double>();
        if (history.Count == 0 || !double.IsFinite(maximum) || maximum <= 0) return new(segments, maximum, 0, null, null);
        var span = (history[^1].At - history[0].At).TotalSeconds;
        foreach (var point in history)
        {
            var reading = value(point);
            if (point.BreakBefore) segment = null;
            if (reading is not { } number || !double.IsFinite(number) || number < 0) { segment = null; continue; }
            if (segment is null) { segment = []; segments.Add(segment); }
            observed.Add(number);
            segment.Add(new(span <= 0 ? 1 : (point.At - history[0].At).TotalSeconds / span, Math.Clamp(number / maximum, 0, 1)));
        }
        return new(segments, maximum, observed.Count, observed.Count == 0 ? null : observed.Min(), observed.Count == 0 ? null : observed.Max());
    }

    public static bool IsTable(string section) => section is "Memory" or "CPU" or "Disks" or "Network" or "Services" or "Startup";
    public static string[] Headers(string section) => section switch
    {
        "Memory" => ["Process", "Private commit", "Working set", "Private WS"],
        "CPU" => ["Process", "CPU", "Threads", "Handles"],
        "Disks" => ["Process", "I/O read", "I/O write", "I/O total"],
        "Network" => ["Local endpoint", "Remote endpoint", "State", "Owner"],
        "Services" => ["Service", "State", "Start type", "Owner"],
        "Startup" => ["Entry", "Scope", "Inventory", "Target"],
        _ => ["Observation", "", "", ""]
    };

    public static IReadOnlyList<DiagnosticItem> Create(string section, MonitorSnapshot snapshot,
        RelationshipSnapshot? relationships, IReadOnlyList<StartupItem> startup,
        DiagnosticsHistory history, Func<ProcessSnapshot, string?> descriptions)
    {
        var result = new List<DiagnosticItem>();
        if (section is "Memory" or "CPU" or "Disks")
        {
            foreach (var process in snapshot.Processes)
            {
                var identity = process.Identity;
                // Unknown creation times must never keep another observation's row identity.
                var key = identity.IsStable ? identity.ToString() : $"{identity}:{snapshot.CapturedAt.UtcTicks}";
                var detail = $"{process.Name} · {process.UserName ?? "Owner unavailable"} · {process.ExecutablePath ?? "Path unavailable"}";
                var name = descriptions(process) ?? process.Name;
                DiagnosticItem row = section switch
                {
                    "Memory" => new(key, identity, name, detail, identity.Pid,
                        Bytes(process.PrivateBytes), Bytes(process.WorkingSetBytes), Bytes(process.PrivateWorkingSetBytes),
                        process.PrivateBytes, process.WorkingSetBytes, process.PrivateWorkingSetBytes),
                    "CPU" => new(key, identity, name, detail, identity.Pid,
                        Percent(process.CpuPercent), Count(process.ThreadCount), Count(process.HandleCount),
                        Number(process.CpuPercent), process.ThreadCount, process.HandleCount),
                    _ => new(key, identity, name, detail, identity.Pid,
                        Rate(process.ReadBytesPerSecond), Rate(process.WriteBytesPerSecond), Rate(process.ReadBytesPerSecond + process.WriteBytesPerSecond),
                        Number(process.ReadBytesPerSecond), Number(process.WriteBytesPerSecond), Number(process.ReadBytesPerSecond + process.WriteBytesPerSecond))
                };
                result.Add(row);
            }
        }
        else if (section == "Network" && relationships is not null)
        {
            foreach (var connection in relationships.Connections)
            {
                var owner = CurrentOwner(connection.OwnerIdentity, snapshot, relationships.CapturedAt);
                var local = Endpoint(connection.LocalAddress, connection.LocalPort);
                var remote = connection.RemoteAddress is null ? "—" : Endpoint(connection.RemoteAddress, connection.RemotePort);
                var key = $"{connection.Protocol}\0{local}\0{remote}\0{connection.OwningPid}\0{connection.OwnerIdentity}";
                result.Add(new(key, owner, local, $"{connection.Protocol} · {(owner is null ? "Owner unverified or stale" : "Owner verified")}", connection.OwningPid,
                    remote, connection.State ?? "UDP endpoint", owner is null ? "Owner unavailable" : OwnerName(owner, snapshot)));
            }
        }
        else if (section == "Services" && relationships is not null)
        {
            foreach (var service in relationships.Services)
            {
                var owner = CurrentOwner(service.OwnerIdentity, snapshot, relationships.CapturedAt);
                result.Add(new(service.Name, owner, service.DisplayName, $"{service.Name} · {(owner is null ? "Owner unverified or stale" : "Owner verified")}", service.ProcessId,
                    service.State, service.StartType ?? "Unavailable", owner is null ? "Owner unavailable" : OwnerName(owner, snapshot)));
            }
        }
        else if (section == "Startup")
        {
            foreach (var item in startup)
                result.Add(new($"{item.Source}\0{item.Name}\0{item.Command}", null, item.Name,
                    $"{item.Command}\n{item.Source}", null, item.Scope,
                    item.Source.StartsWith("Startup folder", StringComparison.Ordinal) && item.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ? "Folder metadata" : "Observed entry", "Not executed"));
        }
        else if (section == "Insights")
        {
            foreach (var insight in history.Explain(snapshot, relationships))
                result.Add(new(insight.Key ?? insight.Title, null, insight.Title, insight.Evidence, null, insight.Kind, "", ""));
        }
        else if (section == "GPU")
        {
            foreach (var adapter in snapshot.GpuAdapters)
            {
                var value = adapter.HeadlineUtilizationPercent;
                var measured = value.State == CounterValueState.Measured && value.Value is { } utilization && double.IsFinite(utilization) && utilization is >= 0 and <= 100;
                var name = adapter.Description ?? $"Adapter 0x{adapter.Luid:X}";
                if (adapter.Description is null || (!adapter.HasCounterInstances && !name.EndsWith("(software adapter)", StringComparison.OrdinalIgnoreCase)))
                    name += " (adapter type unavailable)";
                result.Add(new($"gpu-{adapter.Luid:X}", null, name,
                    $"GPU utilization: {(measured ? Percent(value.Value!.Value) : "unavailable")}. Maximum over engine types, not a sum. Busiest engine: {adapter.HeadlineEngineType ?? "Unavailable"}. CPU and process I/O are not substituted.",
                    null, measured ? Percent(value.Value!.Value) : "Utilization unavailable", measured ? "Observed" : value.Reason ?? value.State.ToString(), ""));
            }
            if (result.Count == 0 && snapshot.GpuPercent is { } gpu && double.IsFinite(gpu) && gpu is >= 0 and <= 100)
                result.Add(new("gpu", null, $"GPU utilization: {Percent(gpu)}", "Observed provider GPU utilization; no CPU or process-I/O estimate is substituted.", null, "Observed", "", ""));
            else if (result.Count == 0)
                result.Add(new("gpu", null, "GPU utilization unavailable", "No valid GPU utilization reading is available. CPU and process I/O are not substituted.", null, "Unsupported", "", ""));
        }
        else
            result.Add(new("help", null, "Read-only Windows diagnostics",
                "Private commit is not physical RAM. Working sets include shared pages; do not sum them into physical usage. Unknown values are not zero. Sort a column to rank current readings; rows retain their order during live updates. Inspect uses PID plus creation time, never PID alone. No process control, driver installation or automatic elevation.", null, "", "", ""));
        // Windows can report duplicate endpoint tuples. They are one displayed observation, not a bandwidth count.
        return result.DistinctBy(item => item.Key).ToArray();
    }

    public static IReadOnlyList<DiagnosticItem> FilterAndSort(IEnumerable<DiagnosticItem> items, string query, int column, bool descending, bool numeric)
    {
        query = query.Trim();
        var filtered = items.Where(item => query.Length == 0 || $"{item.Name}\n{item.Detail}\n{item.Pid}\n{item.First}\n{item.Second}\n{item.Third}".Contains(query, StringComparison.OrdinalIgnoreCase));
        decimal? Value(DiagnosticItem item) => column switch { 1 => item.Pid, 2 => item.Number1, 3 => item.Number2, _ => item.Number3 };
        string Text(DiagnosticItem item) => column switch { 2 => item.First, 3 => item.Second, 4 => item.Third, _ => item.Name };
        if (numeric)
        {
            var availableFirst = filtered.OrderBy(item => Value(item) is null);
            return (descending ? availableFirst.ThenByDescending(Value) : availableFirst.ThenBy(Value))
                .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        }
        return (descending ? filtered.OrderByDescending(Text, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(Text, StringComparer.OrdinalIgnoreCase))
            .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
    }

    private static ProcessIdentity? CurrentOwner(ProcessIdentity? owner, MonitorSnapshot snapshot, DateTimeOffset captured) =>
        owner is { IsStable: true } && Math.Abs((snapshot.CapturedAt - captured).TotalSeconds) <= 10 && snapshot.Processes.Any(p => p.Identity == owner) ? owner : null;
    private static string OwnerName(ProcessIdentity? identity, MonitorSnapshot snapshot) => snapshot.Processes.FirstOrDefault(p => p.Identity == identity)?.Name ?? "Owner unavailable";
    private static string Endpoint(string address, int? port) => $"{(address.Contains(':') ? $"[{address}]" : address)}:{port?.ToString() ?? "—"}";
    private static decimal? Number(double? number) => number is { } value && double.IsFinite(value) && value >= 0 && value <= (double)decimal.MaxValue ? (decimal)value : null;
    private static string Count(int? count) => count?.ToString() ?? "—";
    public static string Bytes(ulong? bytes) => MetricFormatting.Bytes(bytes);
    public static string Percent(double? value) => value is { } number ? $"{number:F1}%" : "—";
    public static string Rate(double? value) => MetricFormatting.Rate(value);
}
