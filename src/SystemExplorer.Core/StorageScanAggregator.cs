namespace SystemExplorer.Core;

public sealed class StorageScanAggregator
{
    private sealed class Node(int parent, string name)
    {
        public int Parent = parent;
        public string Name = name;
        public long Logical, Allocated, Files, Dirs, Denied;
        public DateTime? Latest, MinCreation, MaxCreation;
        public StorageMarkers Markers;
        public List<int>? Children;
    }
    private readonly record struct Counts(long Count, long Logical, long Allocated)
    {
        public Counts Add(long logical, long allocated) => new(Count + 1, Logical + logical, Allocated + allocated);
        public StorageStatistic Statistic(string name) => new(name, Count, Logical, Allocated);
    }
    private readonly object gate = new();
    private readonly string root;
    private readonly StorageScanLimits limits;
    private readonly bool trackHardLinks;
    private readonly DateTimeOffset started;
    private readonly List<Node> nodes = [new(-1, "")];
    private readonly HashSet<long> ids = [];
    private readonly PriorityQueue<StorageFile, long> largest = new();
    private readonly Dictionary<string, Counts> extensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] AgeNames = ["<30 days", "30–90 days", "90 days–1 year", "1–2 years", ">2 years", "Unknown"];
    private readonly Counts[] ages = new Counts[6];
    private readonly List<StorageProblem> denied = [], reparse = [];
    private long deniedTotal, reparseTotal, duplicates, duplicateLogical, duplicateAllocated;
    private bool idCap, dirCap, completed;
    private StorageProgress progress = new(0, 0, 0, 0, 0, "");
    public StorageProgress Progress => Volatile.Read(ref progress);

    public StorageScanAggregator(string rootPath, StorageScanLimits limits, bool trackHardLinks, DateTimeOffset? startedAt = null)
    {
        if (limits.DirectoryNodes < 1 || limits.HardLinkIds < 0 || limits.LargestFiles < 0 || limits.Extensions < 0 || limits.Problems < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        root = rootPath; this.limits = limits; this.trackHardLinks = trackHardLinks; started = startedAt ?? DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<(int Node, string Path)> AddListing(int dirNode, string dirPath, IReadOnlyList<StorageEntry> entries)
    {
        lock (gate)
        {
            EnsureOpen();
            var node = nodes[dirNode];
            var descend = new List<(int, string)>();
            var p = progress;
            var files = p.Files; var directories = p.Directories; var logicalTotal = p.LogicalBytes; var allocatedTotal = p.AllocatedBytes;
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                node.Markers |= Markers(e.Name, e.IsDirectory);
                if (e.IsDirectory)
                {
                    var path = StoragePaths.Join(dirPath, e.Name);
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && !IsCloud(e))
                    {
                        reparseTotal++;
                        if (reparse.Count < limits.Problems) reparse.Add(new(path, e.ReparseTag switch { 0xA0000003 => "Mount point / junction", 0xA000000C => "Symbolic link", _ => $"Reparse tag 0x{e.ReparseTag:X8}" }));
                        continue;
                    }
                    node.Dirs++;
                    directories++;
                    if (nodes.Count < limits.DirectoryNodes)
                    {
                        var index = nodes.Count;
                        (node.Children ??= []).Add(index); nodes.Add(new(dirNode, e.Name)); descend.Add((index, path));
                    }
                    else { dirCap = true; descend.Add((dirNode, path)); }
                    continue;
                }
                var logical = Math.Max(0, e.LogicalBytes);
                var allocated = Math.Max(0, e.AllocatedBytes);
                if (trackHardLinks && e.FileId != 0 && !idCap)
                {
                    if (ids.Contains(e.FileId)) { duplicates++; duplicateLogical += logical; duplicateAllocated += allocated; continue; }
                    if (ids.Count < limits.HardLinkIds) ids.Add(e.FileId);
                    else idCap = true;
                }
                node.Files++; node.Logical += logical; node.Allocated += allocated;
                if (ValidDate(e.LastWriteUtc)) node.Latest = Max(node.Latest, e.LastWriteUtc);
                if (ValidDate(e.CreationUtc))
                {
                    node.MinCreation = node.MinCreation is { } min && min < e.CreationUtc ? min : e.CreationUtc;
                    node.MaxCreation = Max(node.MaxCreation, e.CreationUtc);
                }
                else { node.MinCreation = DateTime.MinValue; }
                if (limits.LargestFiles > 0 && (largest.Count < limits.LargestFiles || allocated > largest.Peek().AllocatedBytes))
                {
                    largest.Enqueue(new(StoragePaths.Join(dirPath, e.Name), logical, allocated, e.LastWriteUtc), allocated);
                    if (largest.Count > limits.LargestFiles) largest.Dequeue();
                }
                var dot = e.Name.LastIndexOf('.');
                var ext = dot < 0 ? "(none)".AsSpan() : e.Name.AsSpan(dot);
                var lookup = extensions.GetAlternateLookup<ReadOnlySpan<char>>();
                if (lookup.TryGetValue(ext, out var actualKey, out var stat)) extensions[actualKey] = stat.Add(logical, allocated);
                else
                {
                    var key = extensions.Count >= limits.Extensions ? "(other)" : ext.ToString().ToLowerInvariant();
                    extensions[key] = extensions.GetValueOrDefault(key).Add(logical, allocated);
                }
                var days = (started.UtcDateTime - e.LastWriteUtc).TotalDays;
                var age = !ValidDate(e.LastWriteUtc) || days < 0 ? 5 : days < 30 ? 0 : days < 90 ? 1 : days < 365 ? 2 : days < 730 ? 3 : 4;
                ages[age] = ages[age].Add(logical, allocated);
                files++; logicalTotal += logical; allocatedTotal += allocated;
            }
            Volatile.Write(ref progress, new(files, directories, logicalTotal, allocatedTotal, deniedTotal, dirPath));
            return descend;
        }
    }

    public void AddDenied(int parentNode, string path, string reason)
    {
        lock (gate)
        {
            EnsureOpen(); nodes[parentNode].Denied++; deniedTotal++;
            if (denied.Count < limits.Problems) denied.Add(new(path, reason));
            Volatile.Write(ref progress, progress with { Denied = deniedTotal, CurrentPath = path });
        }
    }

    public StorageScanResult Complete(StorageScanState state, TimeSpan elapsed, string method, IReadOnlyList<string> notes)
    {
        lock (gate)
        {
            EnsureOpen(); completed = true;
            var result = new StorageNode[nodes.Count];
            for (var i = nodes.Count - 1; i >= 0; i--)
            {
                var n = nodes[i];
                var logical = n.Logical; var allocated = n.Allocated; var files = n.Files; var dirs = n.Dirs; var latest = n.Latest;
                foreach (var c in n.Children ?? Enumerable.Empty<int>())
                {
                    var child = result[c]; logical += child.LogicalBytes; allocated += child.AllocatedBytes;
                    files += child.Files; dirs += child.Directories; latest = Max(latest, child.LatestWrite);
                }
                result[i] = new(n.Parent, n.Name, n.Logical, n.Allocated, n.Files, n.Latest, n.MinCreation, n.MaxCreation, n.Markers, n.Denied,
                    n.Children is null ? Array.Empty<int>() : Array.AsReadOnly(n.Children.OrderByDescending(c => result[c].AllocatedBytes).ToArray()), logical, allocated, files, dirs, latest);
            }
            var snapshot = new StorageScanResult(root, method, state, started, elapsed, Array.AsReadOnly(result), Array.AsReadOnly(largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.AllocatedBytes).ToArray()),
                Array.AsReadOnly(extensions.Select(x => x.Value.Statistic(x.Key)).OrderByDescending(x => x.AllocatedBytes).ToArray()), Array.AsReadOnly(ages.Select((x, i) => x.Statistic(AgeNames[i])).ToArray()),
                Array.AsReadOnly(denied.ToArray()), deniedTotal, Array.AsReadOnly(reparse.ToArray()), reparseTotal, duplicates, duplicateLogical, duplicateAllocated, ids.Count, idCap, dirCap,
                Array.AsReadOnly(notes.Concat(dirCap ? ["Directory node cap reached: omitted directories are rolled into kept ancestors; project and marker attribution is incomplete."] : Array.Empty<string>())
                    .Concat(idCap ? [$"Hard-link de-duplication covered first {ids.Count} file IDs; later files may include duplicates."] : Array.Empty<string>()).ToArray()));
            nodes.Clear(); nodes.TrimExcess(); ids.Clear(); ids.TrimExcess(); largest.Clear(); largest.TrimExcess();
            extensions.Clear(); extensions.TrimExcess(); denied.Clear(); denied.TrimExcess(); reparse.Clear(); reparse.TrimExcess();
            return snapshot;
        }
    }
    private void EnsureOpen() { if (completed) throw new InvalidOperationException("Scan already completed."); }
    private static bool ValidDate(DateTime date) => date != default && date != DateTime.MaxValue;
    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null || a > b ? a : b;
    private static bool IsCloud(StorageEntry e) => (e.ReparseTag & 0xFFFF0FFF) == 0x9000001A || ((uint)e.Attributes & (0x400000 | 0x1000 | 0x80000 | 0x100000)) != 0;
    private static StorageMarkers Markers(string name, bool directory)
    {
        bool Is(string text) => name.Equals(text, StringComparison.OrdinalIgnoreCase);
        bool Ends(string text) => name.EndsWith(text, StringComparison.OrdinalIgnoreCase);
        if (Is(".git")) return directory ? StorageMarkers.Git : StorageMarkers.Worktree;
        if (Is("AGENTS.md") || Is("CLAUDE.md") || Is("GEMINI.md") || Is(".cursorrules") || Is(".windsurfrules") || Is(".clinerules") || Is(".roo") || name.StartsWith(".aider", StringComparison.OrdinalIgnoreCase)
            || directory && (Is(".claude") || Is(".cursor") || Is(".gemini") || Is(".codex"))) return StorageMarkers.AI;
        if (directory) return StorageMarkers.None;
        if (Is("package.json")) return StorageMarkers.Node;
        if (Is("Cargo.toml")) return StorageMarkers.Rust;
        if (Is("pyproject.toml") || Is("setup.py") || Is("requirements.txt")) return StorageMarkers.Python;
        if (Is("go.mod")) return StorageMarkers.Go;
        if (Is("pom.xml")) return StorageMarkers.Maven;
        if (Is("build.gradle") || Is("build.gradle.kts") || Is("settings.gradle") || Is("settings.gradle.kts")) return StorageMarkers.Gradle;
        if (Is("pyvenv.cfg")) return StorageMarkers.PythonEnvironment;
        if (Ends(".csproj") || Ends(".fsproj") || Ends(".vbproj")) return StorageMarkers.DotNet;
        if (Ends(".sln") || Ends(".slnx")) return StorageMarkers.Solution;
        return Ends(".tf") ? StorageMarkers.Terraform : StorageMarkers.None;
    }
}
