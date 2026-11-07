namespace SystemExplorer.Core;

public sealed record StorageReading(string? Value, string? UnavailableReason)
{
    public string Display => Value ?? $"Unavailable ({UnavailableReason ?? "not reported"})";
}

public sealed record StorageVolume(string Root, string Label, string FileSystem, string DriveType,
    long? TotalBytes, long? FreeBytes, long? AvailableToUserBytes, long? ClusterBytes,
    StorageReading Media, StorageReading Bus, StorageReading Product, StorageReading BitLocker, StorageReading Compression, string? Error)
{
    public long? UsedBytes => TotalBytes is >= 0 && FreeBytes is >= 0 && FreeBytes <= TotalBytes ? TotalBytes - FreeBytes : null;
    public double? UsedFraction => TotalBytes is > 0 && UsedBytes is { } used ? (double)used / TotalBytes.Value : null;
}

public static class StorageBusTypes
{
    public static string Name(uint busType) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual",
        15 => "FileBackedVirtual", 16 => "Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => $"Unknown ({busType})"
    };
    public static StorageReading Media(bool? incursSeekPenalty, uint? busType) => busType is 14 or 15
        ? new($"Virtual disk (seek penalty reported: {incursSeekPenalty?.ToString() ?? "unavailable"})", null)
        : busType == 17 || incursSeekPenalty == false ? new("SSD", null)
        : incursSeekPenalty == true ? new("HDD", null) : new(null, "seek penalty and media type not reported");
}

public readonly record struct StorageEntry(string Name, bool IsDirectory, long LogicalBytes, long AllocatedBytes,
    FileAttributes Attributes, uint ReparseTag, long FileId, DateTime LastWriteUtc, DateTime CreationUtc);
public static class StorageFileSystems
{
    public static bool HasStableFileIds(string? fileSystem) => fileSystem is not null &&
        (fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
}

public sealed record StorageScanLimits(int DirectoryNodes = 250_000, int HardLinkIds = 1_000_000,
    int LargestFiles = 1000, int Extensions = 4096, int Problems = 500);
public enum StorageScanState { Completed, Cancelled, Failed, Partial }
[Flags]
public enum StorageMarkers
{
    None = 0, Git = 1, Worktree = 2, Node = 4, DotNet = 8, Solution = 16, Rust = 32,
    Python = 64, Go = 128, Maven = 256, Gradle = 512, Terraform = 1024, PythonEnvironment = 2048, AI = 4096
}
public sealed record StorageFile(string Path, long LogicalBytes, long AllocatedBytes, DateTime LastWriteUtc);
public sealed record StorageProblem(string Path, string Reason);
public sealed record StorageStatistic(string Name, long Count, long LogicalBytes, long AllocatedBytes);
public sealed record StorageProgress(long Files, long Directories, long LogicalBytes, long AllocatedBytes, long Denied, string CurrentPath);
public sealed record StorageNode(int Parent, string Name, long DirectLogicalBytes, long DirectAllocatedBytes, long DirectFiles,
    DateTime? DirectLatestWrite, DateTime? MinCreationUtc, DateTime? MaxCreationUtc, StorageMarkers Markers, long Denied,
    IReadOnlyList<int> Children, long LogicalBytes, long AllocatedBytes, long Files, long Directories, DateTime? LatestWrite);
public sealed record StorageScanResult(string Root, string Method, StorageScanState State, DateTimeOffset StartedAt, TimeSpan Elapsed,
    IReadOnlyList<StorageNode> Nodes, IReadOnlyList<StorageFile> LargestFiles, IReadOnlyList<StorageStatistic> Extensions,
    IReadOnlyList<StorageStatistic> AgeBuckets, IReadOnlyList<StorageProblem> DeniedPaths, long DeniedTotal,
    IReadOnlyList<StorageProblem> ReparsePoints, long ReparseTotal, long HardLinkDuplicates, long HardLinkLogicalBytes,
    long HardLinkAllocatedBytes, int HardLinkIdsTracked, bool HardLinkCapReached, bool DirectoryCapReached, IReadOnlyList<string> Notes)
{
    public bool IsPartial => State != StorageScanState.Completed;
    public StorageNode Totals => Nodes[0];
    public string FullPath(int node)
    {
        var parts = new Stack<string>();
        while (node > 0) { parts.Push(Nodes[node].Name); node = Nodes[node].Parent; }
        return parts.Count == 0 ? StoragePaths.Native(Root) : StoragePaths.Join(Root, string.Join("/", parts));
    }
}

public static class StoragePaths
{
    public static string Native(string path) => path.Replace('/', '\\');
    public static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
    public static string Join(string root, string tail) => Native(Normalize(root) + "/" + Normalize(tail).TrimStart('/'));
    public static bool Contains(string parent, string path) => string.Equals(Normalize(parent), Normalize(path), StringComparison.OrdinalIgnoreCase)
        || Normalize(path).StartsWith(Normalize(parent) + "/", StringComparison.OrdinalIgnoreCase);
}
