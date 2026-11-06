namespace SystemExplorer.Core;

public sealed record PagefileObservation(string Name, ulong TotalBytes, ulong InUseBytes, ulong PeakUsageBytes);

public sealed record PagefileInspection(DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    IReadOnlyList<PagefileObservation> Pagefiles);

public interface IPagefileProvider
{
    Task<PagefileInspection> CaptureAsync(CancellationToken cancellationToken);
}
