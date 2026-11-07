using System.Buffers.Binary;

namespace SystemExplorer.Core;

public enum RamReadingStatus { Available, Unavailable, Denied, Truncated, TimedOut, RequiresAdministrator, IdentityMismatch }
public enum RamCategoryKind { Measured, Derived, Remainder }

public sealed record ProcessResidentBreakdown(ProcessIdentity Identity, string Name, RamReadingStatus Status, string Detail,
    long PagesScanned, ulong PrivateResidentBytes, ulong ShareableResidentBytes, ulong ActuallySharedBytes,
    ulong ShareableSoleBytes, double ProportionalEstimateBytes, bool Truncated, ulong? SharedCommitBytes,
    ulong? PrivateCommitBytes, TimeSpan Duration);

public interface IProcessResidentProvider
{
    Task<ProcessResidentBreakdown> CaptureAsync(ProcessIdentity identity, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProcessResidentBreakdown>> CaptureAllAsync(IReadOnlyList<ProcessSnapshot> processes,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken);
}

public sealed record RamCategory(string Name, long? Bytes, string Source, RamReadingStatus Status, string Detail, RamCategoryKind Kind);
public sealed record RamBreakdown(ulong? InstalledBytes, ulong? UsableBytes, long? HardwareReservedBytes, IReadOnlyList<RamCategory> Categories)
{
    public string Detail { get; init; } = "";
}
public sealed record RamMachineInputs(ulong? InstalledBytes, ulong? UsableBytes, ulong? ModifiedBytes,
    IReadOnlyList<ulong?> StandbyBytes, ulong? FreeZeroBytes, ulong? SystemCacheBytes, ulong? PagedPoolResidentBytes,
    ulong? NonPagedPoolBytes, ulong? DriverImagesBytes, ulong? KernelCodeBytes, IReadOnlyList<ProcessSnapshot> Processes);
public interface IRamBreakdownProvider
{
    Task<RamBreakdown> CaptureAsync(MonitorSnapshot snapshot, CancellationToken cancellationToken);
}

public static class RamAttribution
{
    public const string ProportionalDetail = "Proportional upper-bound estimate: ShareCount saturates at 7; zero share counts use 1. Not a unique physical-page total.";

    public static ProcessResidentBreakdown AggregateWorkingSet(ProcessIdentity identity, string name, ReadOnlySpan<ulong> entries,
        uint pageSize, bool truncated = false, CancellationToken cancellationToken = default)
    {
        if (pageSize == 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ulong privateBytes = 0, shareable = 0, shared = 0, sole = 0;
        double proportional = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            if ((entry & 256) == 0) { privateBytes = checked(privateBytes + pageSize); proportional += pageSize; }
            else
            {
                var count = (int)((entry >> 5) & 7);
                shareable = checked(shareable + pageSize);
                if (count > 1) shared = checked(shared + pageSize); else sole = checked(sole + pageSize);
                proportional += (double)pageSize / Math.Max(1, count);
            }
        }
        return new(identity, name, truncated ? RamReadingStatus.Truncated : RamReadingStatus.Available,
            ProportionalDetail + (truncated ? " Truncated working set: all byte counts are lower bounds." : ""),
            entries.Length, privateBytes, shareable, shared, sole, proportional, truncated, null, null, TimeSpan.Zero);
    }

    public static RamBreakdown Reconcile(RamMachineInputs input)
    {
        var rows = new List<RamCategory>();
        void Add(string name, ulong? bytes, string source, RamCategoryKind kind = RamCategoryKind.Measured, string detail = "") =>
            rows.Add(new(name, bytes.HasValue ? checked((long)bytes.Value) : null, source,
                bytes.HasValue ? RamReadingStatus.Available : RamReadingStatus.Unavailable,
                bytes.HasValue ? detail : "Input unavailable; not inferred. " + detail, kind));
        var ordinary = input.Processes.Where(p => !IsCompression(p.Name)).ToArray();
        var compression = input.Processes.Where(p => IsCompression(p.Name)).ToArray();
        Add("Process private working sets (excluding compression)", ordinary.All(p => p.PrivateWorkingSetBytes.HasValue)
            ? ordinary.Aggregate(0UL, (sum, p) => checked(sum + p.PrivateWorkingSetBytes!.Value)) : null, "SystemProcessInformation", RamCategoryKind.Derived);
        Add("Compression store", compression.Length == 0 ? 0UL : compression.All(p => p.WorkingSetBytes.HasValue)
            ? compression.Aggregate(0UL, (sum, p) => checked(sum + p.WorkingSetBytes!.Value)) : null,
            "Memory Compression process working set", detail: compression.Length == 0 ? "Off / no compression store on this system." : "Compression process working set.");
        Add("System cache resident", input.SystemCacheBytes, "SYSTEM_PERFORMANCE_INFORMATION.ResidentSystemCachePage");
        Add("Paged pool resident", input.PagedPoolResidentBytes, "SYSTEM_PERFORMANCE_INFORMATION.ResidentPagedPoolPage");
        Add("Nonpaged pool", input.NonPagedPoolBytes, "SYSTEM_PERFORMANCE_INFORMATION.NonPagedPoolPages");
        Add("Driver images resident", input.DriverImagesBytes, "SYSTEM_PERFORMANCE_INFORMATION.ResidentSystemDriverPage");
        Add("Kernel code resident", input.KernelCodeBytes, "SYSTEM_PERFORMANCE_INFORMATION.ResidentSystemCodePage");
        Add("Modified (including no-write)", input.ModifiedBytes, "SystemMemoryListInformation");
        for (var i = 0; i < input.StandbyBytes.Count; i++) Add($"Standby priority {i}", input.StandbyBytes[i], "SystemMemoryListInformation.PageCountByPriority");
        Add("Free / zero", input.FreeZeroBytes, "SystemMemoryListInformation");
        long? remainder = input.UsableBytes.HasValue && rows.All(r => r.Bytes.HasValue)
            ? checked((long)input.UsableBytes.Value - rows.Sum(r => r.Bytes!.Value)) : null;
        rows.Add(new(remainder < 0 ? "Overlap between counters (sources overlap)" : "Other in-use: shared/image/mapped process pages, page tables, kernel stacks and unattributed",
            remainder, "Usable minus observed categories", remainder.HasValue ? RamReadingStatus.Available : RamReadingStatus.Unavailable,
            remainder < 0 ? "Negative amount retained: counters overlap or changed between observations; not clamped."
                : remainder.HasValue ? "Remainder, not an attribution; observations are not atomic." : "Missing inputs prevent reconciliation; no residual was guessed.", RamCategoryKind.Remainder));
        return new(input.InstalledBytes, input.UsableBytes, input.InstalledBytes.HasValue && input.UsableBytes.HasValue
            ? checked((long)input.InstalledBytes.Value - (long)input.UsableBytes.Value) : null, rows);
    }

    public static bool IsCompression(string name) => name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MemCompression", StringComparison.OrdinalIgnoreCase);
}

public sealed record PoolTagDriverLead(uint Tag, IReadOnlyList<string> CandidateFiles, int TotalMatches, string Detail);
public sealed record PoolTagDriverInspection(RamReadingStatus Status, string Detail, int FilesScanned, int UnreadableFiles,
    IReadOnlyList<PoolTagDriverLead> Leads);
public interface IPoolTagDriverProvider
{
    Task<PoolTagDriverInspection> CaptureAsync(IReadOnlyCollection<uint> tags, IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken);
}
public static class PoolTagDriverMatcher
{
    public const string LeadDetail = "String-match lead only, not proof of allocation ownership. Microsoft's pooltag.txt is not bundled (not redistributable).";
    public static IReadOnlySet<uint> Match(ReadOnlySpan<byte> bytes, IReadOnlySet<uint> tags, CancellationToken cancellationToken = default)
    {
        var normalized = tags.GroupBy(t => t & 0x7fffffffU).ToDictionary(g => g.Key, g => g.ToArray());
        var found = new HashSet<uint>();
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]) & 0x7fffffffU;
            if (normalized.TryGetValue(value, out var originals)) foreach (var tag in originals) found.Add(tag);
        }
        return found;
    }
    public static PoolTagDriverLead Lead(uint tag, IEnumerable<string> files, int maximumNames = 8)
    {
        if (maximumNames < 0) throw new ArgumentOutOfRangeException(nameof(maximumNames));
        var all = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(tag, all.Take(maximumNames).ToArray(), all.Length,
            (all.Length == 0 ? "Unknown (no local driver file contains this tag). " : "") + LeadDetail);
    }
}

public enum PhysicalPageUse { ProcessPrivate, File, PageFileMapped, PageTable, PagedPool, NonPagedPool, SystemPTE, SessionPrivate, Metafile, AWEPage, DriverLockedPage, KernelStack, Unknown12, Unknown13, Unknown14, Unknown15, Unused }
public enum PhysicalPageList { Zero, Free, Standby, Modified, ModifiedNoWrite, Bad, Active, Transition }
public readonly record struct PhysicalPageIdentity(PhysicalPageUse Use, PhysicalPageList List, int Priority, bool Cold, bool Pinned, bool NonTradeable, ulong PageFrameIndex, ulong FileObject);
public sealed record MemoryListCounts(ulong Zero, ulong Free, ulong Modified, ulong ModifiedNoWrite, ulong Bad,
    IReadOnlyList<ulong> StandbyByPriority, IReadOnlyList<ulong> RepurposedByPriority, ulong ModifiedPageFile);
public sealed record PhysicalPageCount(PhysicalPageUse Use, PhysicalPageList List, ulong Pages);
public sealed record FileObjectPageCount(string Name, IReadOnlyList<ulong> PagesByList);
public sealed record PhysicalMemoryInspection(RamReadingStatus Status, string Detail, ulong PagesScanned,
    IReadOnlyList<PhysicalPageCount> Counts, MemoryListCounts? MemoryLists, IReadOnlyList<FileObjectPageCount> FileObjects);
public interface IPhysicalMemoryProvider
{
    Task<PhysicalMemoryInspection> CaptureAsync(IProgress<(ulong Done, ulong Total)>? progress, CancellationToken cancellationToken);
}
public static class PhysicalMemoryParser
{
    public const string FileNamesDetail = "File names unavailable: resolving kernel file objects requires a driver or DuplicateHandle; both are excluded. File object ordinals are opaque, scoped to this capture.";
    public static PhysicalPageIdentity ParseIdentity(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24) throw new ArgumentException("MMPFN_IDENTITY requires 24 bytes.", nameof(bytes));
        var frame = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return new((PhysicalPageUse)(frame & 15), (PhysicalPageList)((frame >> 4) & 7), (int)((frame >> 57) & 7),
            (frame & 128) != 0, (frame & 256) != 0, (frame & (1UL << 60)) != 0,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
    }
    public static MemoryListCounts ParseMemoryLists(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 176) throw new ArgumentException("SYSTEM_MEMORY_LIST_INFORMATION requires 176 bytes on x64.", nameof(bytes));
        var values = new ulong[22];
        for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * 8)..]);
        return new(values[0], values[1], values[2], values[3], values[4], values[5..13], values[13..21], values[21]);
    }
    public static PhysicalMemoryInspection NotAdministrator() => new(RamReadingStatus.RequiresAdministrator,
        "Requires administrator. No privilege adjustment or physical-page query was attempted.", 0, [], null, []);
}

public sealed class PhysicalMemoryAccumulator
{
    private readonly ulong[,] counts = new ulong[17, 8];
    private readonly Dictionary<ulong, ulong[]> files = [];
    private readonly ulong[] other = new ulong[8];
    public ulong PagesScanned { get; private set; }
    public void Add(ReadOnlySpan<byte> identities, CancellationToken cancellationToken = default)
    {
        if (identities.Length % 24 != 0) throw new ArgumentException("Incomplete PFN record.", nameof(identities));
        for (var offset = 0; offset < identities.Length; offset += 24)
        {
            if (offset % (24 * 4096) == 0) cancellationToken.ThrowIfCancellationRequested();
            var p = PhysicalMemoryParser.ParseIdentity(identities[offset..]);
            var use = p.List is PhysicalPageList.Zero or PhysicalPageList.Free or PhysicalPageList.Bad ? PhysicalPageUse.Unused : p.Use;
            counts[(int)use, (int)p.List]++;
            PagesScanned++;
            if (use is not (PhysicalPageUse.File or PhysicalPageUse.Metafile)) continue;
            if (!files.TryGetValue(p.FileObject, out var lists))
            {
                if (files.Count >= 256) { other[(int)p.List]++; continue; }
                lists = new ulong[8]; files.Add(p.FileObject, lists);
            }
            lists[(int)p.List]++;
        }
    }
    public PhysicalMemoryInspection Build(RamReadingStatus status, string detail, MemoryListCounts? memoryLists)
    {
        var rows = new List<PhysicalPageCount>();
        for (var use = 0; use < 17; use++) for (var list = 0; list < 8; list++)
            if (counts[use, list] != 0) rows.Add(new((PhysicalPageUse)use, (PhysicalPageList)list, counts[use, list]));
        var fileRows = files.Values.Select((v, i) => new FileObjectPageCount($"File object #{i + 1}", v.ToArray())).ToList();
        if (other.Any(v => v != 0)) fileRows.Add(new("Other file objects (row cap)", other.ToArray()));
        return new(status, detail + " " + PhysicalMemoryParser.FileNamesDetail, PagesScanned, rows, memoryLists, fileRows);
    }
}
