namespace SystemExplorer.Core;

public sealed record ScheduledTaskObservation(string Path, string Name, string State, bool? Enabled,
    string Actions, string Triggers, InspectionStatus Status, string Detail);

public sealed record ScheduledTaskInspection(DateTimeOffset CapturedAt, InspectionStatus Status,
    string Detail, IReadOnlyList<ScheduledTaskObservation> Tasks);

public interface IScheduledTaskProvider
{
    Task<ScheduledTaskInspection> CaptureAsync(CancellationToken cancellationToken);
}
