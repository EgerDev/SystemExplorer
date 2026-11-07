namespace SystemExplorer.Core;

public sealed class BoundedItems<T>
{
    public IReadOnlyList<T> Items { get; }
    public bool Truncated { get; }
    public BoundedItems(IEnumerable<T> items, int cap, bool truncated = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        var rows = items.Take(checked(cap + 1)).ToArray();
        Truncated = truncated || rows.Length > cap;
        Items = Array.AsReadOnly(rows.Take(cap).ToArray());
    }
}

public enum ProcessSightingSource { Toolhelp, NtSystemInformation, NtGetNextProcess, PidProbe, EtwOwner, TcpOwner, UdpOwner, ServiceOwner }
public sealed record ProcessSighting(ProcessIdentity Identity, ProcessSightingSource Source,
    string? ImageName = null, string? ImagePath = null, bool Exited = false, InspectionStatus Status = InspectionStatus.Available,
    uint? ExitCode = null, string? ImageDetail = null);
public sealed record ProcessSourceStatus(ProcessSightingSource Source, InspectionStatus Status, string Detail, double ElapsedMilliseconds = 0);
public sealed record ProcessCrossViewSnapshot(DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    bool IsElevated, BoundedItems<ProcessSourceStatus> Sources, BoundedItems<ProcessSighting> Sightings);
public enum ProcessDiscrepancyKind { Consistent, StartedDuringScan, ExitedDuringScan, PidReused, Zombie, ProtectedDenied, StaleOwner, Unknown, Unavailable }
public sealed record ProcessCrossViewResult(ProcessIdentity Identity, ProcessDiscrepancyKind Classification,
    string Reason, BoundedItems<ProcessSighting> Evidence);

public static class ProcessCrossView
{
    private static bool Owner(ProcessSighting s) => s.Source is ProcessSightingSource.EtwOwner or ProcessSightingSource.TcpOwner or ProcessSightingSource.UdpOwner or ProcessSightingSource.ServiceOwner;
    private static bool Complete(ProcessCrossViewSnapshot snapshot, ProcessSightingSource source) =>
        !snapshot.Sightings.Truncated && !snapshot.Sources.Truncated && snapshot.Sources.Items.Any(s => s.Source == source && s.Status == InspectionStatus.Available);
    public static BoundedItems<ProcessCrossViewResult> Compare(ProcessCrossViewSnapshot first,
        ProcessCrossViewSnapshot recheck, int cap = 4096, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<ProcessCrossViewResult> Rows()
        {
            foreach (var group in first.Sightings.Items.GroupBy(s => s.Identity.Pid))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = group.ToArray();
                var after = recheck.Sightings.Items.Where(s => s.Identity.Pid == group.Key).ToArray();
                var identity = before.FirstOrDefault(s => s.Identity.IsStable)?.Identity ?? before[0].Identity;
                bool Had(ProcessSightingSource source) => before.Any(s => s.Source == source);
                bool Has(ProcessSightingSource source) => after.Any(s => s.Source == source);
                var kind = ProcessDiscrepancyKind.Unknown;
                if (before.Concat(after).Where(s => s.Identity.IsStable).Select(s => s.Identity.CreationTimeFileTime).Distinct().Skip(1).Any()) kind = ProcessDiscrepancyKind.PidReused;
                else if (after.Any(s => s.Exited)) kind = ProcessDiscrepancyKind.Zombie;
                else if (Had(ProcessSightingSource.Toolhelp) && Had(ProcessSightingSource.NtSystemInformation)) kind = ProcessDiscrepancyKind.Consistent;
                else if (Has(ProcessSightingSource.Toolhelp) && (!Had(ProcessSightingSource.Toolhelp) || Has(ProcessSightingSource.NtSystemInformation))) kind = ProcessDiscrepancyKind.StartedDuringScan;
                else if (after.Any(s => s.Status == InspectionStatus.AccessDenied) && !Has(ProcessSightingSource.Toolhelp) && !Has(ProcessSightingSource.NtSystemInformation)) kind = ProcessDiscrepancyKind.ProtectedDenied;
                else if (!Has(ProcessSightingSource.Toolhelp) && !Has(ProcessSightingSource.NtSystemInformation) && Complete(recheck, ProcessSightingSource.Toolhelp) && Complete(recheck, ProcessSightingSource.NtSystemInformation)
                    && after.Any(s => s.Source == ProcessSightingSource.PidProbe && s.Status == InspectionStatus.Exited))
                    kind = before.All(Owner) ? ProcessDiscrepancyKind.StaleOwner : ProcessDiscrepancyKind.ExitedDuringScan;
                else if (!Complete(first, ProcessSightingSource.Toolhelp) || !Complete(first, ProcessSightingSource.NtSystemInformation)
                    || !Complete(recheck, ProcessSightingSource.Toolhelp) || !Complete(recheck, ProcessSightingSource.NtSystemInformation)
                    || !after.Any(s => s.Source == ProcessSightingSource.PidProbe && s.Status == InspectionStatus.Available)) kind = ProcessDiscrepancyKind.Unavailable;
                yield return new(identity, kind, kind == ProcessDiscrepancyKind.Unknown
                    ? "Persistent unexplained cross-view discrepancy; a lead, not proof of compromise."
                    : $"Cross-view observation: {kind}; scan timing and access limitations may explain differences.", new(before.Concat(after), 32));
            }
        }
        return new(Rows(), cap, first.Sightings.Truncated || recheck.Sightings.Truncated);
    }

    public static SuspiciousLead? ToLead(ProcessCrossViewResult result) => result.Classification switch
    {
        ProcessDiscrepancyKind.Unknown => new(LeadCategory.CrossView, LeadSeverity.High, "Unexplained process cross-view discrepancy", result.Reason,
            $"PID {result.Identity.Pid}", "Enumeration limitations or transient system activity can still explain this; not proof of compromise.", result.Identity),
        ProcessDiscrepancyKind.ProtectedDenied => new(LeadCategory.CrossView, LeadSeverity.Low, "Process access denied outside lists", result.Reason,
            $"PID {result.Identity.Pid}", "Protected processes and security software can legitimately deny access.", result.Identity),
        _ => null
    };
}

public sealed record ServiceRegistryEntry(string Name, uint? Type, uint? Start, bool DeleteFlag, string? RawImagePath);
public sealed record ServiceCrossViewSnapshot(DateTimeOffset CapturedAt, InspectionStatus RegistryStatus, InspectionStatus ScmStatus,
    string Detail, int IgnoredWithoutType, BoundedItems<ServiceRegistryEntry> Registry, BoundedItems<ServiceSnapshot> Scm);
public enum ConfigurationDiscrepancyKind { RegistryOnly, ScmOnly, MissingSecurityDescriptor, RegistryNotCom, ComNotRegistry, FileNotRegistry, RegistryNotFile, TreeIdNotInTasks }
public sealed record ConfigurationDiscrepancy(string Name, ConfigurationDiscrepancyKind Kind, string Reason);
public sealed record TaskTreeEntry(string Path, string Id, bool HasSecurityDescriptor);
public sealed record TaskCacheEntry(string Id, string Path);
public sealed record TaskCrossViewSnapshot(DateTimeOffset CapturedAt, InspectionStatus ComStatus, InspectionStatus TreeStatus,
    InspectionStatus CacheStatus, InspectionStatus FilesStatus, string Detail, bool IsElevated,
    BoundedItems<ScheduledTaskObservation> Com, BoundedItems<TaskTreeEntry> Tree,
    BoundedItems<TaskCacheEntry> Cache, BoundedItems<string> Files);

public static class ConfigurationCrossView
{
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string TaskPath(string path) => "\\" + path.Replace('/', '\\').Trim('\\');
    public static BoundedItems<ConfigurationDiscrepancy> CompareServices(ServiceCrossViewSnapshot snapshot, int cap = 4096)
    {
        var registry = snapshot.Registry.Items.Where(r => r.Type.HasValue).ToArray();
        IEnumerable<ConfigurationDiscrepancy> Rows()
        {
            if (snapshot.ScmStatus == InspectionStatus.Available && !snapshot.Scm.Truncated)
                foreach (var entry in registry.Where(r => !snapshot.Scm.Items.Any(s => Same(s.Name, r.Name))))
                    yield return new(entry.Name, ConfigurationDiscrepancyKind.RegistryOnly, entry.DeleteFlag
                        ? "Pending deletion (DeleteFlag=1); not proof of hiding."
                        : "Registry only: SCM may not have loaded it, reboot may be pending, or this may be a per-user template; deliberate hiding is also possible.");
            if (snapshot.RegistryStatus == InspectionStatus.Available && !snapshot.Registry.Truncated)
                foreach (var entry in snapshot.Scm.Items.Where(s => !registry.Any(r => Same(s.Name, r.Name))))
                    yield return new(entry.Name, ConfigurationDiscrepancyKind.ScmOnly, "SCM only; per-user service instances or concurrent configuration changes may explain this.");
        }
        return new(Rows(), cap, snapshot.Registry.Truncated || snapshot.Scm.Truncated);
    }
    public static BoundedItems<ConfigurationDiscrepancy> CompareTasks(TaskCrossViewSnapshot snapshot, int cap = 4096)
    {
        bool Has(IEnumerable<string> paths, string path) => paths.Any(p => Same(TaskPath(p), TaskPath(path)));
        var registry = snapshot.Tree.Items.Select(t => t.Path).Concat(snapshot.Cache.Items.Select(t => t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool registryComplete = snapshot.TreeStatus == InspectionStatus.Available && snapshot.CacheStatus == InspectionStatus.Available && !snapshot.Tree.Truncated && !snapshot.Cache.Truncated;
        IEnumerable<ConfigurationDiscrepancy> Rows()
        {
            foreach (var entry in snapshot.Tree.Items)
            {
                if (!entry.HasSecurityDescriptor) yield return new(entry.Path, ConfigurationDiscrepancyKind.MissingSecurityDescriptor, "Tree SD value is missing; a known hiding technique, but configuration damage is also possible.");
                if (snapshot.CacheStatus == InspectionStatus.Available && !snapshot.Cache.Truncated && !snapshot.Cache.Items.Any(c => Same(c.Id, entry.Id)))
                    yield return new(entry.Path, ConfigurationDiscrepancyKind.TreeIdNotInTasks, "Tree Id not in TaskCache Tasks; stale or damaged configuration is possible.");
            }
            foreach (var path in registry)
            {
                if (snapshot.ComStatus == InspectionStatus.Available && !snapshot.Com.Truncated && !Has(snapshot.Com.Items.Select(t => t.Path), path))
                    yield return new(path, ConfigurationDiscrepancyKind.RegistryNotCom, "Registry entry not visible through COM including hidden tasks; access, races or damage may explain this.");
                if (snapshot.FilesStatus == InspectionStatus.Available && !snapshot.Files.Truncated && !Has(snapshot.Files.Items, path))
                    yield return new(path, ConfigurationDiscrepancyKind.RegistryNotFile, "Registry entry has no task file; concurrent removal or damage may explain this.");
            }
            if (registryComplete)
            {
                foreach (var task in snapshot.Com.Items.Where(t => !Has(registry, t.Path))) yield return new(task.Path, ConfigurationDiscrepancyKind.ComNotRegistry, "COM task without registry entry; concurrent changes may explain this.");
                foreach (var path in snapshot.Files.Items.Where(p => !Has(registry, p))) yield return new(path, ConfigurationDiscrepancyKind.FileNotRegistry, "Task file without registry entry; an orphaned file may be benign.");
            }
        }
        return new(Rows(), cap, snapshot.Com.Truncated || snapshot.Tree.Truncated || snapshot.Cache.Truncated || snapshot.Files.Truncated);
    }
}

public sealed record AdsScanOptions(string Folder, int MaxFiles = 50000, int MaxDepth = 32, int MaxResults = 5000);
public sealed record AdsObservation(string Path, string StreamName, long SizeBytes)
{
    public bool IsDownloadMarker => StreamName.Equals(":Zone.Identifier:$DATA", StringComparison.OrdinalIgnoreCase);
    public LeadSeverity Severity => IsDownloadMarker ? LeadSeverity.Info : LeadSeverity.Low;
}
public sealed record AdsScanResult(DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    int FilesVisited, int DeniedCount, int ReparsePointsSkipped, BoundedItems<AdsObservation> Streams);
public interface IProcessCrossViewProvider
{
    Task<ProcessCrossViewSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task<ProcessCrossViewSnapshot> RecheckAsync(BoundedItems<ProcessIdentity> identities, CancellationToken cancellationToken);
}
public interface IServiceCrossViewProvider { Task<ServiceCrossViewSnapshot> CaptureAsync(CancellationToken cancellationToken); }
public interface ITaskCrossViewProvider { Task<TaskCrossViewSnapshot> CaptureAsync(CancellationToken cancellationToken); }
public interface IAdsScanProvider { Task<AdsScanResult> ScanAsync(AdsScanOptions options, CancellationToken cancellationToken); }
