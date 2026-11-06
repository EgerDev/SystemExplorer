namespace SystemExplorer.Core;

/// <summary>Why a counter value is or is not present. A null value always carries a non-Measured state and a reason.</summary>
public enum CounterValueState
{
    Measured,
    /// <summary>First sample for this counter; a rate or delta needs a previous sample.</summary>
    Warmup,
    /// <summary>The interval had no qualifying activity (e.g. disk latency when no I/O completed); not zero, not failure.</summary>
    NoActivityInInterval,
    Unavailable
}

public readonly record struct CounterValue(double? Value, CounterValueState State, string? Reason)
{
    public static CounterValue Measured(double value) => new(value, CounterValueState.Measured, null);
    public static CounterValue Warmup(string reason) => new(null, CounterValueState.Warmup, reason);
    public static CounterValue NoActivity(string reason) => new(null, CounterValueState.NoActivityInInterval, reason);
    public static CounterValue Unavailable(string reason) => new(null, CounterValueState.Unavailable, reason);
}

/// <summary>Physical disk counters. Aggregate (_Total) values describe the system, never any process.</summary>
public sealed record DiskCounterRow(string Instance, bool IsAggregate,
    CounterValue ReadLatencyMilliseconds, CounterValue WriteLatencyMilliseconds,
    CounterValue ReadsPerSecond, CounterValue WritesPerSecond,
    CounterValue CurrentQueueLength, CounterValue ReadBytesPerSecond, CounterValue WriteBytesPerSecond,
    CounterValue IdleTimePercent);

public sealed record DiskCounterSection(InspectionStatus Status, string Detail, DiskCounterRow? Total,
    IReadOnlyList<DiskCounterRow> Disks);

public enum NetworkInterfaceKind { Hardware, Loopback, Virtual, Filter, Tunnel, Other }

public sealed record NetworkAdapterRow(uint InterfaceIndex, ulong InterfaceLuid, string Alias, string Description,
    uint InterfaceType, string OperationalStatus, NetworkInterfaceKind Kind,
    ulong? ReceiveLinkSpeedBitsPerSecond, ulong? TransmitLinkSpeedBitsPerSecond,
    CounterValue ReceiveBytesPerSecond, CounterValue TransmitBytesPerSecond, bool IncludedInTotal);

/// <summary>Totals sum only rows with IncludedInTotal (hardware, non-filter, non-loopback) so stacked interfaces are not double counted.</summary>
public sealed record NetworkCounterSection(InspectionStatus Status, string Detail, IReadOnlyList<NetworkAdapterRow> Adapters,
    CounterValue TotalReceiveBytesPerSecond, CounterValue TotalTransmitBytesPerSecond, TimeSpan? MeasuredInterval);

/// <summary>% Processor Utility can exceed 100 when the processor runs above nominal frequency; it is shown as measured.</summary>
public sealed record LogicalProcessorRow(string Instance, int? Group, int? Number,
    CounterValue ProcessorUtilityPercent, CounterValue PrivilegedTimePercent);

public sealed record CpuCounterSection(InspectionStatus Status, string Detail,
    CounterValue TotalProcessorUtilityPercent, CounterValue TotalPrivilegedTimePercent,
    IReadOnlyList<LogicalProcessorRow> LogicalProcessors,
    CounterValue ContextSwitchesPerSecond, CounterValue ProcessorQueueLength);

/// <summary>Compression value is the Memory Compression store's resident working set, not bytes saved or a compression ratio.</summary>
public sealed record MemoryListSection(InspectionStatus Status, string Detail,
    CounterValue StandbyCacheReserveBytes, CounterValue StandbyCacheNormalPriorityBytes, CounterValue StandbyCacheCoreBytes,
    CounterValue ModifiedPageListBytes, CounterValue FreeAndZeroPageListBytes,
    CounterValue PageFaultsPerSecond, CounterValue PageReadsPerSecond, CounterValue PagesInputPerSecond,
    InspectionStatus CompressionStatus, string CompressionDetail, CounterValue CompressionStoreResidentWorkingSetBytes);

public sealed record GpuEngineUtilization(string EngineType, CounterValue UtilizationPercent);

/// <summary>Headline utilization is the maximum over engine types, never their sum. Shared usage is not extra physical RAM.</summary>
public sealed record GpuAdapterRow(ulong Luid, string? Description,
    ulong? DedicatedVideoMemoryBytes, ulong? SharedSystemMemoryBytes,
    CounterValue DedicatedUsageBytes, CounterValue SharedUsageBytes,
    IReadOnlyList<GpuEngineUtilization> Engines, CounterValue HeadlineUtilizationPercent, string? HeadlineEngineType);

/// <summary>Identity is non-null only when the PID's creation time was read and matched at both samples.</summary>
public sealed record GpuProcessRow(int Pid, ProcessIdentity? Identity, InspectionStatus IdentityStatus, string IdentityDetail,
    ulong AdapterLuid, IReadOnlyList<GpuEngineUtilization> Engines, CounterValue MaxUtilizationPercent, string? MaxEngineType,
    CounterValue DedicatedUsageBytes, CounterValue SharedUsageBytes);

public sealed record GpuCounterSection(InspectionStatus Status, string Detail,
    IReadOnlyList<GpuAdapterRow> Adapters, IReadOnlyList<GpuProcessRow> Processes,
    int MalformedInstanceCount, string? MalformedDetail,
    InspectionStatus InventoryStatus, string InventoryDetail);

public sealed record SystemCounterSnapshot(DateTimeOffset CapturedAt, bool IsWarmup, TimeSpan? Interval,
    InspectionStatus Status, string Detail,
    DiskCounterSection Disks, NetworkCounterSection Network, CpuCounterSection Cpu,
    MemoryListSection Memory, GpuCounterSection Gpu);

/// <summary>Stateful: keeps counter queries and previous samples between calls. The first call is a warmup.</summary>
public interface ISystemCounterProvider : IDisposable
{
    Task<SystemCounterSnapshot> CaptureAsync(CancellationToken cancellationToken);
}
