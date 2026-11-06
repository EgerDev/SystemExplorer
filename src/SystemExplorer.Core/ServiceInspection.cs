namespace SystemExplorer.Core;

/// <summary>Unexpanded local SCM configuration, not a process-lifetime or ownership observation.
/// BinaryPath and Account can contain sensitive local metadata; no credentials are collected.</summary>
public sealed record ServiceInspection(string Name, DateTimeOffset CapturedAt, InspectionStatus Status,
    string Detail, string? BinaryPath, string? Account, string? LoadOrderGroup,
    uint? ServiceType, uint? StartType, uint? ErrorControl, IReadOnlyList<string> Dependencies)
{
    /// <summary>QueryServiceConfig2W observations; null when the base configuration was not read.</summary>
    public ServiceConfigExtensions? Extensions { get; init; }
    /// <summary>EnumDependentServicesW observation; null when the service could not be opened.</summary>
    public ServiceDependentsObservation? Dependents { get; init; }
}

/// <summary>Each extension is statused separately; a failed query leaves its value null (unavailable), never a default.</summary>
public sealed record ServiceConfigExtensions(
    ServiceDelayedAutoStartObservation DelayedAutoStart,
    ServiceTriggerObservation Triggers,
    ServiceDescriptionObservation Description,
    ServiceSidObservation ServiceSid);

public sealed record ServiceDelayedAutoStartObservation(InspectionStatus Status, string Detail, bool? IsDelayed);

/// <summary>Trigger metadata only; trigger data blobs are counted, never decoded.</summary>
public sealed record ServiceTrigger(uint TriggerType, string TriggerTypeName, uint Action, string ActionName,
    Guid? Subtype, string? SubtypeName, int DataItemCount);

public sealed record ServiceTriggerObservation(InspectionStatus Status, string Detail, IReadOnlyList<ServiceTrigger> Triggers);

public sealed record ServiceDescriptionObservation(InspectionStatus Status, string Detail, string? Description);

public sealed record ServiceSidObservation(InspectionStatus Status, string Detail, uint? SidType, string? SidTypeName);

public sealed record ServiceDependent(string Name, string? DisplayName, uint ServiceType, uint CurrentState);

public sealed record ServiceDependentsObservation(InspectionStatus Status, string Detail, IReadOnlyList<ServiceDependent> Services);

public interface IServiceInspectionProvider
{
    Task<ServiceInspection> CaptureAsync(string serviceName, CancellationToken cancellationToken);
}

/// <summary>Driver service rows from EnumServicesStatusExW(SERVICE_DRIVER). Config fields come from a separate
/// bounded QueryServiceConfigW per driver and stay null (ConfigStatus explains) when unavailable. ImagePath is unexpanded.</summary>
public sealed record DriverServiceObservation(string Name, string? DisplayName, uint ServiceType, uint CurrentState,
    uint? StartType, uint? ErrorControl, string? ImagePath, string? LoadOrderGroup,
    InspectionStatus ConfigStatus, string ConfigDetail);

public sealed record DriverServiceInventory(DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    IReadOnlyList<DriverServiceObservation> Drivers);

public interface IDriverServiceProvider
{
    Task<DriverServiceInventory> CaptureAsync(CancellationToken cancellationToken);
}
