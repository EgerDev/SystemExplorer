namespace SystemExplorer.Core;

public sealed record HandleTypeCount(int TypeIndex, string? TypeName, long HandleCount);

public sealed record HandleObservation(ulong HandleValue, int TypeIndex, string? TypeName, uint GrantedAccess, uint Attributes);

public sealed record HandleInventory(ProcessIdentity Identity, DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    long? ProcessHandleCount, IReadOnlyList<HandleTypeCount> ProcessCountsByType, IReadOnlyList<HandleObservation> Handles,
    long? SystemHandleCount, IReadOnlyList<HandleTypeCount> SystemCountsByType);

public interface IHandleInventoryProvider
{
    Task<HandleInventory> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken);
}

public sealed record PoolTagObservation(string Tag, uint RawTag, ulong PagedAllocations, ulong PagedFrees, ulong PagedUsedBytes,
    ulong NonPagedAllocations, ulong NonPagedFrees, ulong NonPagedUsedBytes);

public sealed record PoolTagInventory(DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    IReadOnlyList<PoolTagObservation> Tags);

public interface IPoolTagProvider
{
    Task<PoolTagInventory> CaptureAsync(CancellationToken cancellationToken);
}

public sealed record ThreadActivityObservation(ThreadIdentity Identity, InspectionStatus Status, string Detail,
    uint? ContextSwitches, long? ContextSwitchDelta, double? ContextSwitchesPerSecond,
    int? ThreadState, int? WaitReason, int? BasePriority, int? CurrentPriority,
    ulong? CycleTime, ulong? CycleDelta);

public sealed record ThreadActivityInspection(ProcessIdentity Identity, DateTimeOffset CapturedAt, TimeSpan SampleInterval,
    InspectionStatus Status, string Detail, ulong? ProcessCycleTime, ulong? ProcessCycleDelta,
    IReadOnlyList<ThreadActivityObservation> Threads);

public interface IThreadActivityProvider
{
    Task<ThreadActivityInspection> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken);
}

public enum FileUserAppType { Unknown, MainWindow, OtherWindow, Service, Explorer, Console, Critical }

public sealed record FileUserObservation(ProcessIdentity Identity, string? AppName, string? ServiceShortName,
    FileUserAppType AppType, int RawAppType, bool? Restartable);

public sealed record FileUsersInspection(string Path, DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    IReadOnlyList<FileUserObservation> Users);

public sealed record ModuleUserObservation(ProcessIdentity Identity, string ModuleName, string ModulePath, ulong BaseAddress);

public sealed record ModuleUsersInspection(string Query, DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    int ProcessesConsidered, int ProcessesMatched, int ProcessesSearched, int ProcessesDenied, int ProcessesExited,
    int ProcessesFailed, int ProcessesNotSearched, IReadOnlyList<ModuleUserObservation> Matches);

public interface IFileUsageProvider
{
    Task<FileUsersInspection> FindFileUsersAsync(string path, CancellationToken cancellationToken);
    Task<ModuleUsersInspection> FindModuleUsersAsync(string query, CancellationToken cancellationToken);
}

public static class ObjectInventoryNames
{
    private static readonly string[] ThreadStates =
        ["Initialized", "Ready", "Running", "Standby", "Terminated", "Waiting", "Transition", "DeferredReady", "GateWaitObsolete", "WaitingForProcessInSwap"];

    private static readonly string[] WaitReasons =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest",
        "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation", "WrDelayExecution", "WrSuspended", "WrUserRequest",
        "WrEventPair", "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut", "WrRendezvous",
        "WrKeyedEvent", "WrTerminated", "WrProcessInSwap", "WrCpuRateControl", "WrCalloutStack", "WrKernel", "WrResource",
        "WrPushLock", "WrMutex", "WrQuantumEnd", "WrDispatchInt", "WrPreempted", "WrYieldExecution", "WrFastMutex",
        "WrGuardedMutex", "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault", "WrIoRing", "WrMdlCache", "WrRcu"
    ];

    public static string? ThreadStateName(int? state) => state is >= 0 && state < ThreadStates.Length ? ThreadStates[state.Value] : null;
    public static string? WaitReasonName(int? reason) => reason is >= 0 && reason < WaitReasons.Length ? WaitReasons[reason.Value] : null;
}
