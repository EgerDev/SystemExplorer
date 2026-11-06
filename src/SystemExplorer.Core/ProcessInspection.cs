namespace SystemExplorer.Core;

public enum ProcessInspectionKind { Modules, MemoryRegions }
public enum InspectionStatus { Available, Partial, Truncated, TimedOut, AccessDenied, Unsupported, Exited, IdentityMismatch, Failed }

public sealed record ModuleObservation(string Name, string Path, ulong BaseAddress, ulong SizeBytes);
public sealed record MemoryRegionObservation(ulong BaseAddress, ulong SizeBytes, ulong? AllocationBase,
    uint State, uint? Protection, uint? Type);

public sealed record ProcessInspection(ProcessIdentity Identity, ProcessInspectionKind Kind, DateTimeOffset CapturedAt,
    InspectionStatus Status, string Detail, IReadOnlyList<ModuleObservation> Modules, IReadOnlyList<MemoryRegionObservation> Regions);

public interface IProcessInspectionProvider
{
    Task<ProcessInspection> CaptureAsync(ProcessIdentity identity, ProcessInspectionKind kind, CancellationToken cancellationToken);
}
