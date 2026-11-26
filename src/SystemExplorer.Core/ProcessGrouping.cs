namespace SystemExplorer.Core;

public sealed record ProcessGroup
{
    private IReadOnlyList<ProcessSnapshot> members = Array.Empty<ProcessSnapshot>();

    public ProcessGroup(ProcessIdentity key, string name, IReadOnlyList<ProcessSnapshot> members)
    {
        Key = key;
        Name = name;
        Members = members;
    }

    public ProcessIdentity Key { get; init; }
    public string Name { get; init; }
    public IReadOnlyList<ProcessSnapshot> Members
    {
        get => members;
        init
        {
            members = Array.AsReadOnly(value.ToArray());
            PrivateBytes = members.All(p => p.PrivateBytes.HasValue) ? members.Aggregate(0UL, (sum, p) => checked(sum + p.PrivateBytes!.Value)) : null;
            CpuPercent = members.All(p => p.CpuPercent.HasValue) ? members.Sum(p => p.CpuPercent!.Value) : null;
        }
    }

    public ulong? PrivateBytes { get; private init; }
    public double? CpuPercent { get; private init; }
}

public static class ProcessGrouping
{
    public static ProcessSnapshot? VerifiedParent(ProcessSnapshot child, IReadOnlyDictionary<int, ProcessSnapshot> byPid)
    {
        if (!child.Identity.IsStable || child.ParentPid is not { } parentPid ||
            !byPid.TryGetValue(parentPid, out var parent) || !parent.Identity.IsStable ||
            parent.Identity.Pid == child.Identity.Pid ||
            parent.Identity.CreationTimeFileTime >= child.Identity.CreationTimeFileTime)
            return null;
        return parent;
    }

    public static IReadOnlyList<ProcessGroup> Create(IReadOnlyList<ProcessSnapshot> processes)
    {
        var byPid = processes.GroupBy(p => p.Identity.Pid).ToDictionary(g => g.Key, g => g.Last());
        var groups = new Dictionary<ProcessIdentity, List<ProcessSnapshot>>();
        foreach (var process in processes)
        {
            var root = process;
            if (process.Identity.IsStable && process.SessionId.HasValue &&
                !string.IsNullOrEmpty(process.ExecutablePath) && !string.IsNullOrEmpty(process.UserSid))
            {
                var seen = new HashSet<ProcessIdentity> { process.Identity };
                while (VerifiedParent(root, byPid) is { } parent &&
                    string.Equals(parent.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parent.UserSid, process.UserSid, StringComparison.Ordinal) &&
                    parent.SessionId == process.SessionId && seen.Add(parent.Identity))
                    root = parent;
            }
            if (!groups.TryGetValue(root.Identity, out var members)) groups[root.Identity] = members = [];
            members.Add(process);
        }
        return groups.Select(g => new ProcessGroup(g.Key, byPid[g.Key.Pid].Name,
            g.Value.OrderBy(p => p.Identity.CreationTimeFileTime).ThenBy(p => p.Identity.Pid).ToArray()))
            .OrderByDescending(g => g.PrivateBytes ?? 0).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
