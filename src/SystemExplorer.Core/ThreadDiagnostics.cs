namespace SystemExplorer.Core;

public readonly record struct ThreadIdentity(ProcessIdentity Process, int ThreadId, long CreationTimeFileTime)
{
    public bool IsStable => Process.Pid > 0 && Process.IsStable && ThreadId > 0 && CreationTimeFileTime > 0;
}

public sealed record ThreadObservation(ThreadIdentity Identity, long? KernelTimeTicks, long? UserTimeTicks,
    double? CpuPercent, InspectionStatus Status, string Detail);

public sealed record ThreadInspection(ProcessIdentity Identity, DateTimeOffset CapturedAt, TimeSpan SampleInterval,
    InspectionStatus Status, string Detail, IReadOnlyList<ThreadObservation> Threads);

public sealed record WaitChainNode(int Ordinal, uint ObjectType, uint ObjectStatus, string? ObjectName,
    uint? ProcessId, uint? ThreadId);

public sealed record WaitChainInspection(ThreadIdentity Identity, DateTimeOffset CapturedAt, InspectionStatus Status,
    string Detail, bool? CycleDetected, IReadOnlyList<WaitChainNode> Nodes);

public interface IThreadDiagnosticsProvider
{
    Task<ThreadInspection> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    Task<WaitChainInspection> CaptureWaitChainAsync(ThreadIdentity identity, CancellationToken cancellationToken);
}
