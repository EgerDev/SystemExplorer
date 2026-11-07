using System.Collections.ObjectModel;
using System.ComponentModel;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed class ProcessRow : INotifyPropertyChanged
{
    private ProcessSnapshot process;
    private ProcessGroup? group;
    private string? description;
    private bool selected;
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
    public double RowHeight => IsChild ? 32 : 44;
    public bool IsExpanded { get; private set; }
    public bool HasChildren => group is { Members.Count: > 1 };
    public string Chevron => HasChildren ? IsExpanded ? "\uE70D" : "\uE76C" : "";
    public string Name => !IsChild && description is not null ? description : process.Name;
    public string RawName => process.Name;
    public string Count => group?.Members.Count.ToString() ?? "";
    public Microsoft.UI.Xaml.Visibility SelectionVisibility => selected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility DescriptionVisibility => !IsChild && description is not null && description != process.Name ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string IdentityHelp => $"{process.Name} · PID {process.Identity.Pid}\n{(description is null ? "Executable name from the process snapshot." : $"Local file description: {description}. File metadata is not verified publisher or application identity.")}";
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

    private static readonly string[] DisplayProperties =
    [
        nameof(Chevron), nameof(Name), nameof(Count), nameof(DescriptionVisibility), nameof(IdentityHelp),
        nameof(Pid), nameof(Memory), nameof(Cpu), nameof(Threads), nameof(Io), nameof(User), nameof(Status),
        nameof(RowWeight), nameof(GroupVisibility), nameof(ChevronVisibility), nameof(AutomationName), nameof(GroupingReason)
    ];
    private object[] DisplayValues() =>
    [
        Chevron, Name, Count, DescriptionVisibility, IdentityHelp, Pid, Memory, Cpu, Threads, Io, User, Status,
        RowWeight, GroupVisibility, ChevronVisibility, AutomationName, GroupingReason
    ];
    private void NotifyChanges(object[] before)
    {
        var after = DisplayValues();
        for (var i = 0; i < after.Length; i++)
            if (!Equals(before[i], after[i])) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(DisplayProperties[i]));
    }
    public void Update(ProcessSnapshot nextProcess, ProcessGroup? nextGroup, bool expanded, string? nextDescription)
    {
        var before = DisplayValues();
        process = nextProcess;
        group = nextGroup;
        IsExpanded = expanded;
        description = nextDescription;
        NotifyChanges(before);
    }
    public void SetDescription(string? value)
    {
        if (description == value) return;
        var before = DisplayValues();
        description = value;
        NotifyChanges(before);
    }
    public void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionVisibility)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly HashSet<ProcessIdentity> expanded = new();
    private MonitorSnapshot? snapshot;
    private IReadOnlyList<ProcessGroup> groups = [];
    private readonly record struct DesiredRow(ProcessSnapshot Process, ProcessGroup? Group, bool Child, bool Expanded)
    {
        public string Key => $"{(Child ? "child" : Group is null ? "process" : "group")}:{Process.Identity}";
    }
    private static readonly string[] SummaryProperties =
        [nameof(ProcessCount), nameof(GroupCount), nameof(MemoryUsed), nameof(CpuUsed), nameof(EmptyVisibility), nameof(EmptyMessage)];
    private object[] summary;
    public ShellViewModel() => summary = SummaryValues();
    private object[] SummaryValues() => [ProcessCount, GroupCount, MemoryUsed, CpuUsed, EmptyVisibility, EmptyMessage];
    private void NotifySummary()
    {
        var next = SummaryValues();
        for (var i = 0; i < next.Length; i++)
            if (!Equals(summary[i], next[i])) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(SummaryProperties[i]));
        summary = next;
    }
    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public DiagnosticsHistory History { get; } = new();
    public ProcessDescriptionIndex Descriptions { get; } = new();
    public bool IsRebuilding { get; private set; }
    public bool IsLiveRebuild { get; private set; }
    public event EventHandler? RowsRebuilding;
    public event EventHandler? RowsRebuilt;
    public MonitorSnapshot? Snapshot => snapshot;
    public RelationshipSnapshot? Relationships { get; set; }
    public string Search { get; set; } = "";
    public string Sort { get; set; } = "Private memory";
    public bool Grouped { get; set; } = true;
    public bool Descending { get; set; } = true;
    public string ProcessCount => snapshot?.Processes.Count.ToString() ?? "—";
    public string GroupCount => snapshot is null ? "—" : groups.Count.ToString();
    public string MemoryUsed => Display.Bytes(snapshot?.Memory.PhysicalUsedBytes);
    public string CpuUsed => Display.Percent(snapshot?.TotalCpuPercent);
    public Microsoft.UI.Xaml.Visibility EmptyVisibility => Rows.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string EmptyMessage => snapshot is null ? "Waiting for the first live sample…" : string.IsNullOrWhiteSpace(Search) ? "No process rows are available in the latest sample." : "No processes match this search. Clear the filter to show all processes.";

    public void Apply(MonitorSnapshot next)
    {
        snapshot = next;
        groups = ProcessGrouping.Create(next.Processes);
        History.Add(next);
        Descriptions.UpdateProcesses(next.Processes);
        var live = next.Processes.Select(p => p.Identity).ToHashSet();
        expanded.RemoveWhere(id => !live.Contains(id));
        Rebuild(live: true);
    }

    public void Toggle(ProcessRow row)
    {
        if (!row.HasChildren) return;
        if (!expanded.Add(row.Identity)) expanded.Remove(row.Identity);
        Rebuild();
    }

    public void Rebuild(bool live = false)
    {
        if (snapshot is null) return;
        bool Matches(ProcessSnapshot p) => string.IsNullOrWhiteSpace(Search) ||
            $"{p.Name} {p.Identity.Pid} {p.ExecutablePath} {p.UserName} {Descriptions.GetCached(p)}".Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
        var desired = new List<DesiredRow>();
        var positions = Rows.Select((row, index) => (row.RowKey, index)).ToDictionary(pair => pair.RowKey, pair => pair.index);
        int Position(string key) => positions.GetValueOrDefault(key, int.MaxValue);
        IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, ProcessSortKey> key) =>
            items.OrderBy(key, Comparer<ProcessSortKey>.Create((left, right) => StickyProcessSort.Compare(left, right, Sort, Descending)));
        IEnumerable<ProcessSnapshot> SortProcesses(IEnumerable<ProcessSnapshot> processes, bool child) =>
            Order(processes, p => new(p.Name, p.CpuPercent, p.PrivateBytes, p.Identity.Pid, Position($"{(child ? "child" : "process")}:{p.Identity}")));
        if (Grouped)
        {
            IEnumerable<ProcessGroup> groups = this.groups.Where(g => g.Members.Any(Matches));
            groups = Order(groups, g => new(g.Name, g.CpuPercent, g.PrivateBytes, g.Key.Pid, Position($"group:{g.Key}")));
            foreach (var group in groups)
            {
                var representative = group.Members.First(p => p.Identity == group.Key);
                desired.Add(new(representative, group, false, expanded.Contains(group.Key)));
                if (group.Members.Count > 1 && expanded.Contains(group.Key))
                    desired.AddRange(SortProcesses(group.Members, true).Select(p => new DesiredRow(p, null, true, false)));
            }
        }
        else desired.AddRange(SortProcesses(snapshot.Processes.Where(Matches), false).Select(p => new DesiredRow(p, null, false, false)));

        IsLiveRebuild = live;
        RowsRebuilding?.Invoke(this, EventArgs.Empty);
        IsRebuilding = true;
        try
        {
            MinimalKeyedRows.Reconcile(Rows, desired, row => row.RowKey, row => row.Key,
                row =>
                {
                    var created = new ProcessRow(row.Process, row.Group, row.Child, row.Expanded);
                    created.SetDescription(Descriptions.GetCached(row.Process));
                    return created;
                },
                (current, row) => current.Update(row.Process, row.Group, row.Expanded, Descriptions.GetCached(row.Process)));
        }
        finally { IsRebuilding = false; }
        RowsRebuilt?.Invoke(this, EventArgs.Empty);
        IsLiveRebuild = false;
        NotifySummary();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Display
{
    public static string Bytes(ulong? bytes) => MetricFormatting.Bytes(bytes);
    public static string Percent(double? value) => value is { } number ? $"{number:F1}%" : "—";
    public static string Rate(double value) => MetricFormatting.Rate(value);
}
