using SystemExplorer.Core;

internal static class HiddenTaskAvailabilityTests
{
    internal static void Run()
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Task unavailable-view regression failed."); }
        var snapshot = new TaskCrossViewSnapshot(DateTimeOffset.UnixEpoch, InspectionStatus.Available, InspectionStatus.Available,
            InspectionStatus.Available, InspectionStatus.Available, "COM: Available; Task files: Available", true,
            new([], 4096), new([new("\\fixture", "id", true)], 4096), new([new("id", "\\fixture")], 4096), new([], 4096));
        var readable = ConfigurationCrossView.CompareTasks(snapshot);
        Require(readable.Items.Count == 2 && readable.Items.Any(d => d.Kind == ConfigurationDiscrepancyKind.RegistryNotCom)
            && readable.Items.Any(d => d.Kind == ConfigurationDiscrepancyKind.RegistryNotFile));
        foreach (var comStatus in Enum.GetValues<InspectionStatus>())
        foreach (var filesStatus in Enum.GetValues<InspectionStatus>())
        {
            var observed = snapshot with { ComStatus = comStatus, FilesStatus = filesStatus,
                Detail = $"COM: {comStatus}; Task files: {filesStatus}" };
            var diff = ConfigurationCrossView.CompareTasks(observed);
            Require(diff.Items.Any(d => d.Kind == ConfigurationDiscrepancyKind.RegistryNotCom) == (comStatus == InspectionStatus.Available));
            Require(diff.Items.Any(d => d.Kind == ConfigurationDiscrepancyKind.RegistryNotFile) == (filesStatus == InspectionStatus.Available));
            Require(observed.ComStatus == comStatus && observed.FilesStatus == filesStatus && observed.Detail.Contains($"COM: {comStatus}"));
        }
        var truncated = snapshot with { Com = new([], 4096, true), Files = new([], 4096, true) };
        Require(ConfigurationCrossView.CompareTasks(truncated).Items.Count == 0 && ConfigurationCrossView.CompareTasks(truncated).Truncated);
        var unavailable = snapshot with { ComStatus = InspectionStatus.Failed, FilesStatus = InspectionStatus.AccessDenied };
        Require(ConfigurationCrossView.CompareTasks(unavailable).Items.Count == 0);
        var missingSd = unavailable with { Tree = new([new("\\fixture", "id", false)], 4096) };
        Require(ConfigurationCrossView.CompareTasks(missingSd).Items.Single().Kind == ConfigurationDiscrepancyKind.MissingSecurityDescriptor);
        var empty = snapshot with { Tree = new([], 4096), Cache = new([], 4096) };
        Require(ConfigurationCrossView.CompareTasks(empty).Items.Count == 0);
    }
}
