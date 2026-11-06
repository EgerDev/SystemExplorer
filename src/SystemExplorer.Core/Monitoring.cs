namespace SystemExplorer.Core;

public readonly record struct ProcessIdentity(int Pid, long? CreationTimeFileTime)
{
    public bool IsStable => CreationTimeFileTime is > 0;
    public override string ToString() => $"{Pid}:{CreationTimeFileTime?.ToString() ?? "unknown"}";
}

public enum DataAvailability { Available, Partial, AccessDenied, Unsupported, Error }

public sealed record ProcessSnapshot
{
    public required ProcessIdentity Identity { get; init; }
    public required string Name { get; init; }
    public int? ParentPid { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public string? UserName { get; init; }
    public string? UserSid { get; init; }
    public int? SessionId { get; init; }
    public int? ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public ulong? PrivateBytes { get; init; }
    public ulong? WorkingSetBytes { get; init; }
    public ulong? PrivateWorkingSetBytes { get; init; }
    public ulong? PeakWorkingSetBytes { get; init; }
    public uint? PageFaultCount { get; init; }
    public ulong? KernelCpuTimeTicks { get; init; }
    public ulong? UserCpuTimeTicks { get; init; }
    public ulong? ReadOperationCount { get; init; }
    public ulong? WriteOperationCount { get; init; }
    public ulong? OtherOperationCount { get; init; }
    public ulong? OtherTransferBytes { get; init; }
    public double? CpuPercent { get; init; }
    public double? ReadBytesPerSecond { get; init; }
    public double? WriteBytesPerSecond { get; init; }
    public string Status { get; init; } = "Observed";
    public DataAvailability Availability { get; init; } = DataAvailability.Available;
    public string? UnavailableReason { get; init; }
}

public sealed record MemorySnapshot
{
    public ulong? PhysicalTotalBytes { get; init; }
    public ulong? PhysicalAvailableBytes { get; init; }
    public ulong? CommitTotalBytes { get; init; }
    public ulong? CommitLimitBytes { get; init; }
    public ulong? CommitPeakBytes { get; init; }
    public uint? ProcessCount { get; init; }
    public uint? ThreadCount { get; init; }
    public uint? HandleCount { get; init; }
    public ulong? SystemCacheBytes { get; init; }
    public ulong? StandbyBytes { get; init; }
    public ulong? KernelPagedBytes { get; init; }
    public ulong? KernelNonpagedBytes { get; init; }
    public ulong? PhysicalUsedBytes => PhysicalTotalBytes is { } total && PhysicalAvailableBytes is { } available && available <= total ? total - available : null;
}

public sealed record MonitorSnapshot
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyList<ProcessSnapshot> Processes { get; init; }
    public MemorySnapshot Memory { get; init; } = new();
    public double? TotalCpuPercent { get; init; }
    public double? DiskReadBytesPerSecond { get; init; }
    public double? DiskWriteBytesPerSecond { get; init; }
    public double? GpuPercent { get; init; }
    public IReadOnlyList<GpuAdapterRow> GpuAdapters { get; init; } = Array.Empty<GpuAdapterRow>();
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

public sealed record ConnectionSnapshot
{
    public required string Protocol { get; init; }
    public required string LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public string? RemoteAddress { get; init; }
    public int? RemotePort { get; init; }
    public required int OwningPid { get; init; }
    public ProcessIdentity? OwnerIdentity { get; init; }
    public string? State { get; init; }
}

public sealed record ServiceSnapshot
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string State { get; init; }
    public int? ProcessId { get; init; }
    public ProcessIdentity? OwnerIdentity { get; init; }
    public string? StartType { get; init; }
}

public sealed record RelationshipSnapshot
{
    public required DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<ConnectionSnapshot> Connections { get; init; } = Array.Empty<ConnectionSnapshot>();
    public IReadOnlyList<ServiceSnapshot> Services { get; init; } = Array.Empty<ServiceSnapshot>();
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

public sealed record StartupItem(string Name, string Command, string Source, string Scope);
public sealed record PublisherInfo(string? Publisher, string VerificationStatus);

public interface IMonitorProvider : IDisposable
{
    Task<MonitorSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public interface IRelationshipProvider : IDisposable
{
    Task<RelationshipSnapshot> CaptureAsync(IReadOnlyList<ProcessSnapshot> processes, CancellationToken cancellationToken);
    Task<PublisherInfo> GetPublisherAsync(string executablePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken);
}
