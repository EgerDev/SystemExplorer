namespace SystemExplorer.Core;

public sealed record ProcessGroup(ProcessIdentity Key, string Name, IReadOnlyList<ProcessSnapshot> Members)
{
    public ulong? PrivateBytes => Members.All(p => p.PrivateBytes.HasValue) ? Members.Aggregate(0UL, (sum, p) => checked(sum + p.PrivateBytes!.Value)) : null;
    public double? CpuPercent => Members.All(p => p.CpuPercent.HasValue) ? Members.Sum(p => p.CpuPercent!.Value) : null;
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
