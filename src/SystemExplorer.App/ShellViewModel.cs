using System.Collections.ObjectModel;
using System.ComponentModel;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed class ProcessRow : INotifyPropertyChanged
{
    private ProcessSnapshot process;
    private ProcessGroup? group;
    public ProcessRow(ProcessSnapshot process, ProcessGroup? group, bool child, bool expanded)
    {
        this.process = process;
        this.group = group;
        IsChild = child;
        IsExpanded = expanded;
    }
    public string RowKey => $"{(IsChild ? "child" : group is null ? "process" : "group")}:{process.Identity}";
    public ProcessIdentity Identity => process.Identity;
    public ProcessSnapshot Process => process;
    public bool IsChild { get; }
    public bool IsExpanded { get; private set; }
    public bool HasChildren => group is { Members.Count: > 1 };
    public string Chevron => HasChildren ? IsExpanded ? "\uE70D" : "\uE76C" : "";
    public string Name => group is { Members.Count: > 1 } ? $"{group.Name} ({group.Members.Count})" : process.Name;
    public string Pid => group is { Members.Count: > 1 } ? "Group" : process.Identity.Pid.ToString();
    public string Memory => Display.Bytes(group is null ? process.PrivateBytes : group.PrivateBytes);
    public string Cpu => Display.Percent(group is null ? process.CpuPercent : group.CpuPercent);
    public string Threads => group is null ? process.ThreadCount?.ToString() ?? "—" : group.Members.All(p => p.ThreadCount.HasValue) ? group.Members.Sum(p => p.ThreadCount!.Value).ToString() : "—";
    public string Io => !HasChildren && process.ReadBytesPerSecond is { } read && process.WriteBytesPerSecond is { } write ? Display.Rate(read + write) : "—";
    public string User => process.UserName ?? "Unavailable";
    public string Status => HasChildren ? "Observed" : process.Status;
    public global::Windows.UI.Text.FontWeight RowWeight => HasChildren ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    public Microsoft.UI.Xaml.Visibility GroupVisibility => HasChildren ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ChevronVisibility => GroupVisibility;
    public string AutomationName => $"{process.Name}, PID {process.Identity.Pid}, {(IsChild ? "group member" : group is null ? "individual process" : "application entry")}";
    public Microsoft.UI.Xaml.GridLength Indent => new(IsChild ? 25 : 0);
    public string GroupingReason => group is null ? "Individual observed process" : "Inferred group: same executable, owner and observed parent chain. Private commit is not physical RAM.";

    public void Update(ProcessRow other)
    {
        process = other.process;
        group = other.group;
        IsExpanded = other.IsExpanded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly HashSet<ProcessIdentity> expanded = new();
    private MonitorSnapshot? snapshot;
    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public DiagnosticsHistory History { get; } = new();
    public MonitorSnapshot? Snapshot => snapshot;
    public RelationshipSnapshot? Relationships { get; set; }
    public string Search { get; set; } = "";
    public string Sort { get; set; } = "Private memory";
    public bool Grouped { get; set; } = true;
    public bool Descending { get; set; } = true;
    public string ProcessCount => snapshot?.Processes.Count.ToString() ?? "—";
    public string GroupCount => snapshot is null ? "—" : ProcessGrouping.Create(snapshot.Processes).Count.ToString();
    public string MemoryUsed => Display.Bytes(snapshot?.Memory.PhysicalUsedBytes);
    public string CpuUsed => Display.Percent(snapshot?.TotalCpuPercent);
    public Microsoft.UI.Xaml.Visibility EmptyVisibility => Rows.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string EmptyMessage => snapshot is null ? "Waiting for the first live sample…" : string.IsNullOrWhiteSpace(Search) ? "No process rows are available in the latest sample." : "No processes match this search. Clear the filter to show all processes.";

    public void Apply(MonitorSnapshot next)
    {
        snapshot = next;
        History.Add(next);
        var live = next.Processes.Select(p => p.Identity).ToHashSet();
        expanded.RemoveWhere(id => !live.Contains(id));
        Rebuild();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Toggle(ProcessRow row)
    {
        if (!row.HasChildren) return;
        if (!expanded.Add(row.Identity)) expanded.Remove(row.Identity);
        Rebuild();
    }

    public void Rebuild()
    {
        if (snapshot is null) return;
        bool Matches(ProcessSnapshot p) => string.IsNullOrWhiteSpace(Search) ||
            $"{p.Name} {p.Identity.Pid} {p.ExecutablePath} {p.UserName}".Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
        var desired = new List<ProcessRow>();
        if (Grouped)
        {
            IEnumerable<ProcessGroup> groups = ProcessGrouping.Create(snapshot.Processes).Where(g => g.Members.Any(Matches));
            groups = Sort switch
            {
                "Name" => Descending ? groups.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase) : groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
                "CPU" => Descending ? groups.OrderByDescending(g => g.CpuPercent ?? -1) : groups.OrderBy(g => g.CpuPercent ?? double.MaxValue),
                "PID" => Descending ? groups.OrderByDescending(g => g.Key.Pid) : groups.OrderBy(g => g.Key.Pid),
                _ => Descending ? groups.OrderByDescending(g => g.PrivateBytes ?? 0) : groups.OrderBy(g => g.PrivateBytes ?? ulong.MaxValue)
            };
            foreach (var group in groups)
            {
                var representative = group.Members.First(p => p.Identity == group.Key);
                desired.Add(new(representative, group, false, expanded.Contains(group.Key)));
                if (group.Members.Count > 1 && expanded.Contains(group.Key))
                    desired.AddRange(SortProcesses(group.Members).Select(p => new ProcessRow(p, null, true, false)));
            }
        }
        else desired.AddRange(SortProcesses(snapshot.Processes.Where(Matches)).Select(p => new ProcessRow(p, null, false, false)));

        var wanted = desired.Select(r => r.RowKey).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--) if (!wanted.Contains(Rows[i].RowKey)) Rows.RemoveAt(i);
        var existing = Rows.ToDictionary(r => r.RowKey);
        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            if (existing.TryGetValue(row.RowKey, out var current))
            {
                current.Update(row);
                var old = Rows.IndexOf(current);
                if (old != i) Rows.Move(old, i);
            }
            else Rows.Insert(i, row);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyMessage)));
    }

    private IEnumerable<ProcessSnapshot> SortProcesses(IEnumerable<ProcessSnapshot> processes) => Sort switch
    {
        "Name" => Descending ? processes.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase) : processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        "CPU" => Descending ? processes.OrderByDescending(p => p.CpuPercent ?? -1) : processes.OrderBy(p => p.CpuPercent ?? double.MaxValue),
        "PID" => Descending ? processes.OrderByDescending(p => p.Identity.Pid) : processes.OrderBy(p => p.Identity.Pid),
        _ => Descending ? processes.OrderByDescending(p => p.PrivateBytes ?? 0) : processes.OrderBy(p => p.PrivateBytes ?? ulong.MaxValue)
    };
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Display
{
    public static string Bytes(ulong? bytes) => bytes is { } value ? value >= 1024UL * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):F2} GiB" : $"{value / (1024d * 1024):F1} MiB" : "—";
    public static string Percent(double? value) => value is { } number ? $"{number:F1}%" : "—";
    public static string Rate(double value) => value >= 1024 * 1024 ? $"{value / (1024 * 1024):F1} MiB/s" : $"{value / 1024:F0} KiB/s";
}
