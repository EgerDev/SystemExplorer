namespace SystemExplorer.Core;

/// <summary>One inventory row: dependencies as reported by SCM ("+Name" entries are load-order groups).</summary>
public sealed record ServiceGraphInput(string Name, IReadOnlyList<string> Dependencies, string? LoadOrderGroup);

public enum ServiceGraphNodeKind { Service, Group }

/// <summary>InInventory=false means the name was referenced but not present in the supplied inventory.
/// Group nodes are leaves; GroupMembers lists inventory services whose load-order group matches.</summary>
public sealed record ServiceGraphNode(string Name, ServiceGraphNodeKind Kind, bool InInventory, IReadOnlyList<string> GroupMembers);

/// <summary>From depends on To.</summary>
public sealed record ServiceGraphEdge(string From, string To, ServiceGraphNodeKind ToKind);

/// <summary>An observed dependency cycle in configuration (first node repeated at the end); not a runtime deadlock claim.</summary>
public sealed record ServiceGraphCycle(IReadOnlyList<string> Path);

public sealed record ServiceDependencyGraph(string Root, InspectionStatus Status, string Detail,
    IReadOnlyList<ServiceGraphNode> Nodes, IReadOnlyList<ServiceGraphEdge> DependsOn,
    IReadOnlyList<ServiceGraphEdge> Dependents, IReadOnlyList<ServiceGraphCycle> ObservedCycles, int MaximumDepthReached);

public static class ServiceGraph
{
    public const int DefaultMaximumDepth = 16;
    public const int DefaultMaximumNodes = 512;
    public const int MaximumCycles = 32;

    public static ServiceDependencyGraph Build(string root, IReadOnlyList<ServiceGraphInput> inventory,
        int maximumDepth = DefaultMaximumDepth, int maximumNodes = DefaultMaximumNodes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(root) || root.Length > 256 || root.IndexOfAny(['\0', '/', '\\']) >= 0)
            return new(root, InspectionStatus.Unsupported, "Invalid local service root.", [], [], [], [], 0);
        ArgumentNullException.ThrowIfNull(inventory);
        maximumDepth = Math.Clamp(maximumDepth, 0, 512);
        maximumNodes = Math.Clamp(maximumNodes, 1, 4096);
        var comparer = StringComparer.OrdinalIgnoreCase;
        var rows = new Dictionary<string, ServiceGraphInput>(comparer);
        foreach (var row in inventory.OrderBy(r => r.Name, comparer).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(row.Name)) rows.TryAdd(row.Name, row);
        }
        string Canonical(string name) => rows.TryGetValue(name, out var row) ? row.Name : name;
        root = Canonical(root);
        string Key(string name, ServiceGraphNodeKind kind) => ((int)kind) + ":" + name;
        var nodes = new Dictionary<string, ServiceGraphNode>(comparer);
        var forward = new Dictionary<string, ServiceGraphEdge>(comparer);
        var reverse = new Dictionary<string, ServiceGraphEdge>(comparer);
        bool truncated = false;
        int reached = 0;
        bool Add(string name, ServiceGraphNodeKind kind)
        {
            var key = Key(name, kind);
            if (nodes.ContainsKey(key)) return true;
            if (nodes.Count >= maximumNodes) { truncated = true; return false; }
            var members = kind == ServiceGraphNodeKind.Group
                ? rows.Values.Where(r => comparer.Equals(r.LoadOrderGroup, name)).Select(r => r.Name).OrderBy(n => n, comparer).ToArray() : [];
            if (members.Length > 256) truncated = true;
            nodes.Add(key, new(name, kind, kind == ServiceGraphNodeKind.Group ? members.Length > 0 : rows.ContainsKey(name), members.Take(256).ToArray()));
            return true;
        }
        Add(root, ServiceGraphNodeKind.Service);
        void Walk(bool backwards)
        {
            var queue = new Queue<(string Name, ServiceGraphNodeKind Kind, int Depth)>();
            var visited = new HashSet<string>(comparer);
            queue.Enqueue((root, ServiceGraphNodeKind.Service, 0));
            if (backwards && maximumDepth > 0 && rows.TryGetValue(root, out var rootRow) &&
                !string.IsNullOrWhiteSpace(rootRow.LoadOrderGroup) && Add(rootRow.LoadOrderGroup, ServiceGraphNodeKind.Group))
                queue.Enqueue((rootRow.LoadOrderGroup, ServiceGraphNodeKind.Group, 1));
            while (queue.TryDequeue(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(Key(item.Name, item.Kind))) continue;
                reached = Math.Max(reached, item.Depth);
                var links = new List<(string Name, ServiceGraphNodeKind Kind, ServiceGraphEdge Edge)>();
                if (backwards)
                {
                    string dependency = item.Kind == ServiceGraphNodeKind.Group ? "+" + item.Name : item.Name;
                    foreach (var row in rows.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (row.Dependencies.Contains(dependency, comparer))
                            links.Add((row.Name, ServiceGraphNodeKind.Service, new(row.Name, item.Name, item.Kind)));
                    }
                }
                else if (item.Kind == ServiceGraphNodeKind.Service && rows.TryGetValue(item.Name, out var row))
                    foreach (string dep in row.Dependencies.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(comparer))
                    {
                        bool group = dep.StartsWith('+');
                        string name = group ? dep[1..] : Canonical(dep);
                        var kind = group ? ServiceGraphNodeKind.Group : ServiceGraphNodeKind.Service;
                        links.Add((name, kind, new(item.Name, name, kind)));
                    }
                if (item.Depth >= maximumDepth) { if (links.Count > 0) truncated = true; continue; }
                foreach (var link in links.OrderBy(l => l.Name, comparer).ThenBy(l => l.Kind))
                {
                    if (!Add(link.Name, link.Kind)) continue;
                    var edges = backwards ? reverse : forward;
                    edges.TryAdd(link.Edge.From + "\0" + Key(link.Edge.To, link.Edge.ToKind), link.Edge);
                    queue.Enqueue((link.Name, link.Kind, item.Depth + 1));
                }
            }
        }
        Walk(false);
        Walk(true);
        var cycles = new List<ServiceGraphCycle>();
        var cycleKeys = new HashSet<string>(comparer);
        var state = new Dictionary<string, int>(comparer);
        var path = new List<string>();
        void Visit(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cycles.Count >= MaximumCycles) { truncated = true; return; }
            state[name] = 1;
            path.Add(name);
            foreach (var edge in forward.Values.Where(e => e.ToKind == ServiceGraphNodeKind.Service && comparer.Equals(e.From, name)).OrderBy(e => e.To, comparer))
            {
                if (cycles.Count >= MaximumCycles) break;
                if (!state.TryGetValue(edge.To, out int value)) Visit(edge.To);
                else if (value == 1)
                {
                    int start = path.FindIndex(n => comparer.Equals(n, edge.To));
                    var cycle = path.Skip(start).ToArray();
                    int first = Enumerable.Range(0, cycle.Length).OrderBy(i => cycle[i], comparer).First();
                    var canonical = cycle.Skip(first).Concat(cycle.Take(first)).ToArray();
                    if (cycleKeys.Add(string.Join("\0", canonical)))
                    {
                        if (cycles.Count < MaximumCycles) cycles.Add(new(canonical.Append(canonical[0]).ToArray()));
                        else truncated = true;
                        if (cycles.Count == MaximumCycles) truncated = true;
                    }
                }
            }
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
        foreach (var node in nodes.Values.Where(n => n.Kind == ServiceGraphNodeKind.Service).OrderBy(n => n.Name, comparer))
            if (!state.ContainsKey(node.Name)) Visit(node.Name);
        ServiceGraphEdge[] Ordered(Dictionary<string, ServiceGraphEdge> edges) => edges.Values.OrderBy(e => e.From, comparer).ThenBy(e => e.To, comparer).ThenBy(e => e.ToKind).ToArray();
        string detail = "Observed configuration dependency graph; cycles are observed configuration cycles only.";
        detail += " Group dependents depend on the root's load-order group, not on the root by name.";
        if (!rows.ContainsKey(root)) detail += " Root is not in the supplied inventory.";
        if (truncated) detail += " Depth, node, group-member or cycle bound reached.";
        return new(root, truncated ? InspectionStatus.Truncated : InspectionStatus.Available, detail,
            nodes.Values.OrderBy(n => n.Name, comparer).ThenBy(n => n.Kind).ToArray(), Ordered(forward), Ordered(reverse), cycles, reached);
    }
}
