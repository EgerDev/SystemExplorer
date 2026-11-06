namespace SystemExplorer.Core;

/// <summary>Unexpanded local SCM configuration, not a process-lifetime or ownership observation.
/// BinaryPath and Account can contain sensitive local metadata; no credentials are collected.</summary>
public sealed record ServiceInspection(string Name, DateTimeOffset CapturedAt, InspectionStatus Status,
    string Detail, string? BinaryPath, string? Account, string? LoadOrderGroup,
    uint? ServiceType, uint? StartType, uint? ErrorControl, IReadOnlyList<string> Dependencies);

public interface IServiceInspectionProvider
{
    Task<ServiceInspection> CaptureAsync(string serviceName, CancellationToken cancellationToken);
}
