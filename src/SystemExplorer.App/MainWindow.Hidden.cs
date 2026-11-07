using System.Diagnostics;
using Microsoft.UI.Xaml;
using SystemExplorer.App.Views;
using SystemExplorer.Core;
using SystemExplorer.Relationships;
using SystemExplorer.Windows;
using Windows.Storage.Pickers;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly WindowsHiddenProcessProvider hiddenProcesses = new();
    private readonly WindowsHiddenConfigurationProvider hiddenConfiguration = new();
    private readonly WindowsAdsScanProvider hiddenAds = new();
    private CancellationTokenSource? hiddenCancellation;
    private bool hiddenInitialized;

    private void HiddenLoaded(object sender, RoutedEventArgs args)
    {
        if (hiddenInitialized) return;
        hiddenInitialized = true;
        var elevation = WindowsHiddenEnvironment.GetElevation();
        HiddenPanel.SetElevation(elevation.IsElevated == true ? "Running as administrator · Read-only; local checks only"
            : "Requires administrator for full coverage: some process probes, ETW registrations, TaskCache keys and task files may be denied. " + elevation.Detail);
        HiddenPanel.ScanRequested += RunHiddenScan;
        HiddenPanel.CancelRequested += () => hiddenCancellation?.Cancel();
        HiddenPanel.InspectRequested += InspectOverviewProcess;
    }

    private async void RunHiddenScan(int scan)
    {
        if (hiddenCancellation is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        hiddenCancellation = cancellation;
        HiddenPanel.SetBusy(true);
        HiddenPanel.Progress("Collecting bounded, read-only observations…");
        string? notice = null;
        try
        {
            var token = cancellation.Token;
            HiddenScanResult? result = scan switch
            {
                0 => await ScanHiddenProcesses(token),
                1 => await ScanHiddenServices(token),
                2 => await ScanHiddenTasks(token),
                3 => await ScanSuspiciousLeads(token),
                4 => await ScanHiddenAds(token),
                _ => null
            };
            token.ThrowIfCancellationRequested();
            if (result is not null) HiddenPanel.Complete(scan, result);
            else notice = "Folder selection cancelled — previous result retained.";
        }
        catch (OperationCanceledException) { notice = "Cancelled — previous result retained."; }
        catch (Exception ex) { notice = $"Scan unavailable — {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}). Previous result retained; no absence or compromise claim is made."; }
        finally
        {
            hiddenCancellation = null;
            if (!shutdown.IsCancellationRequested)
            {
                HiddenPanel.SetBusy(false);
                if (notice is not null) HiddenPanel.Progress(notice);
            }
        }
    }

    private static string HiddenStatus(InspectionStatus status) => status switch
    {
        InspectionStatus.AccessDenied => "Denied",
        InspectionStatus.Available => "Available",
        _ => $"Unavailable / {status}"
    };

    private static string ConfigurationEvidence(ConfigurationDiscrepancyKind kind) => kind switch
    {
        ConfigurationDiscrepancyKind.RegistryOnly => "In registry only; not listed by Service Control Manager",
        ConfigurationDiscrepancyKind.ScmOnly => "Listed by Service Control Manager; no registry entry",
        ConfigurationDiscrepancyKind.MissingSecurityDescriptor => "TaskCache Tree security descriptor is missing",
        ConfigurationDiscrepancyKind.TreeIdNotInTasks => "No TaskCache Tasks entry for the Tree ID",
        ConfigurationDiscrepancyKind.RegistryNotCom => "Not visible to Task Scheduler (including hidden tasks)",
        ConfigurationDiscrepancyKind.ComNotRegistry => "Visible to Task Scheduler; no task registry entry",
        ConfigurationDiscrepancyKind.FileNotRegistry => "Task file exists; no task registry entry",
        ConfigurationDiscrepancyKind.RegistryNotFile => "No task file",
        _ => "Configuration difference"
    };

    private async Task<HiddenScanResult> ScanHiddenProcesses(CancellationToken token)
    {
        var first = await hiddenProcesses.CaptureAsync(token);
        var candidates = first.Sightings.Items.GroupBy(s => s.Identity.Pid)
            .Where(g => !(g.Any(s => s.Source == ProcessSightingSource.Toolhelp) && g.Any(s => s.Source == ProcessSightingSource.NtSystemInformation)))
            .Select(g => g.FirstOrDefault(s => s.Identity.IsStable)?.Identity ?? g.First().Identity);
        HiddenPanel.Progress("Rechecking discrepancies after 300 ms…");
        await Task.Delay(300, token);
        var second = await hiddenProcesses.RecheckAsync(new(candidates, 8192), token);
        var comparison = ProcessCrossView.Compare(first, second, cancellationToken: token);
        var leads = comparison.Items.Where(r => r.Classification != ProcessDiscrepancyKind.Consistent).Select(result =>
        {
            var seen = result.Evidence.Items.Select(e => e.Source).Distinct().ToArray();
            var missing = first.Sources.Items.Select(s => s.Source).Except(seen);
            var observed = result.Evidence.Items.FirstOrDefault(s => s.ImagePath is not null) ?? result.Evidence.Items.FirstOrDefault(s => s.ImageName is not null);
            var exited = result.Evidence.Items.FirstOrDefault(s => s.Exited && s.ExitCode is not null);
            var lead = ProcessCrossView.ToLead(result) ?? new(LeadCategory.CrossView,
                result.Classification is ProcessDiscrepancyKind.Zombie or ProcessDiscrepancyKind.StaleOwner ? LeadSeverity.Low : LeadSeverity.Info,
                result.Classification.ToString(), result.Reason, "", "Scan races, exited objects with references still held, stale owners or PID reuse are commonly benign.", result.Identity);
            return lead with { Title = $"{observed?.ImageName ?? "PID " + result.Identity.Pid} · {result.Classification}", Path = observed?.ImagePath,
                Reason = observed?.ImagePath is null && result.Classification == ProcessDiscrepancyKind.Zombie
                    ? (result.Evidence.Items.FirstOrDefault(s => s.ImageDetail is not null)?.ImageDetail ?? "Image unavailable: no limited-query image observation") + ". " + lead.Reason : lead.Reason,
                BenignNote = result.Classification == ProcessDiscrepancyKind.Zombie ? "A handle held by another process keeps the exited process object alive, commonly a parent or monitoring tool. This is not evidence of compromise." : lead.BenignNote,
                Evidence = $"{observed?.ImagePath ?? result.Evidence.Items.FirstOrDefault(s => s.ImageDetail is not null)?.ImageDetail ?? "Image unavailable: no limited-query image observation"}\nCreation time (FILETIME): {result.Identity.CreationTimeFileTime?.ToString() ?? "unavailable"}\nExit code: {exited?.ExitCode?.ToString() ?? "unavailable / not exited"}\nSaw: {string.Join(", ", seen)}\nNot observed (not proof of absence): {string.Join(", ", missing)}\nRecheck: {string.Join(", ", second.Sources.Items.Select(s => s.Source + " " + HiddenStatus(s.Status)))}" };
        }).ToArray();
        var sources = string.Join("  |  ", first.Sources.Items.Select(s =>
        {
            int denied = first.Sightings.Items.Count(r => r.Source == s.Source && r.Status == InspectionStatus.AccessDenied);
            return $"{s.Source}: {first.Sightings.Items.Count(r => r.Source == s.Source)} · {HiddenStatus(s.Status)} · {s.ElapsedMilliseconds:F1} ms"
                + (s.Status == InspectionStatus.Available ? "" : $" · {s.Detail}")
                + (denied == 0 ? "" : $" · Denied: {denied} process queries (Windows error 5); creation time/image unavailable for these PIDs");
        }));
        return new(first.CapturedAt, $"{comparison.Items.Count(r => r.Classification == ProcessDiscrepancyKind.Consistent)} processes consistent across 2 baseline lists; {first.Sources.Items.Count} sources checked. {leads.Length} other observations.",
            sources + "\n" + first.Detail, leads, comparison.Truncated);
    }

    private async Task<HiddenScanResult> ScanHiddenServices(CancellationToken token)
    {
        var snapshot = await hiddenConfiguration.CaptureServicesAsync(token);
        var diff = ConfigurationCrossView.CompareServices(snapshot);
        var leads = diff.Items.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(group => new SuspiciousLead(LeadCategory.CrossView, LeadSeverity.Low,
            group.Key, string.Join(" ", group.Select(d => d.Reason)), string.Join(" · ", group.Select(d => ConfigurationEvidence(d.Kind))),
            "Pending deletion, per-user templates/instances, reboot requirements or stale installation state may explain this; not proof of hiding.")).ToArray();
        return new(snapshot.CapturedAt, $"{leads.Length} service names differ; {snapshot.IgnoredWithoutType} registry keys without Type ignored.", snapshot.Detail, leads, diff.Truncated);
    }

    private async Task<HiddenScanResult> ScanHiddenTasks(CancellationToken token)
    {
        var snapshot = await hiddenConfiguration.CaptureTasksAsync(token);
        var diff = ConfigurationCrossView.CompareTasks(snapshot);
        var leads = diff.Items.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var kinds = group.Select(d => d.Kind).ToArray();
            bool orphan = kinds.Contains(ConfigurationDiscrepancyKind.TreeIdNotInTasks) && kinds.Contains(ConfigurationDiscrepancyKind.RegistryNotCom)
                && kinds.Contains(ConfigurationDiscrepancyKind.RegistryNotFile) && snapshot.Tree.Items.Any(t => t.HasSecurityDescriptor && t.Path.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            var severity = kinds.Contains(ConfigurationDiscrepancyKind.MissingSecurityDescriptor) ? LeadSeverity.High : orphan ? LeadSeverity.Low : LeadSeverity.Medium;
            return new SuspiciousLead(LeadCategory.CrossView, severity, group.Key, string.Join(" ", group.Select(d => d.Reason)),
                (orphan ? "In TaskCache Tree only · " : "") + string.Join(" · ", kinds.Select(ConfigurationEvidence)), orphan ? "Stale leftover registration from OS image/servicing is possible: this Tree entry retains SD but has no Tasks, COM or file entry."
                : "Servicing, concurrent task changes, configuration damage or access restrictions can explain cross-view differences; no proof of compromise.", Path: group.Key);
        }).ToArray();
        return new(snapshot.CapturedAt, $"{leads.Length} task paths differ (one row per task). Missing security descriptors have highest severity.", snapshot.Detail, leads, diff.Truncated);
    }

    private async Task<HiddenScanResult?> ScanHiddenAds(CancellationToken token)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        token.ThrowIfCancellationRequested();
        if (folder is null) return null;
        var watch = Stopwatch.StartNew();
        var snapshot = await hiddenAds.ScanAsync(new(folder.Path), token);
        var leads = snapshot.Streams.Items.Select(stream => new SuspiciousLead(LeadCategory.AlternateDataStream, stream.Severity,
            Path.GetFileName(stream.Path) + stream.StreamName.Replace(":$DATA", "", StringComparison.OrdinalIgnoreCase), $"{MetricFormatting.Bytes(stream.SizeBytes >= 0 ? (ulong)stream.SizeBytes : null)} · contents not read",
            $"{stream.Path} · {stream.StreamName} · {stream.SizeBytes} bytes", stream.IsDownloadMarker ? "Zone.Identifier is a normally benign download-origin marker."
            : "Applications legitimately store metadata in named streams; presence alone is not suspicious.", Path: stream.Path)).ToArray();
        return new(snapshot.CapturedAt, $"{snapshot.FilesVisited} objects scanned · {snapshot.DeniedCount} denied · {snapshot.ReparsePointsSkipped} reparse points skipped",
            $"ADS: {leads.Length} · {HiddenStatus(snapshot.Status)} · {watch.Elapsed.TotalMilliseconds:F1} ms · {snapshot.Detail}", leads, snapshot.Streams.Truncated);
    }

    private async Task<HiddenScanResult> ScanSuspiciousLeads(CancellationToken token)
    {
        const int processCap = 8192, targetCap = 4096, signatureCap = 300;
        var at = DateTimeOffset.UtcNow;
        var processes = new BoundedItems<ProcessSnapshot>(Model.Snapshot?.Processes ?? [], processCap);
        var endpoints = new BoundedItems<ConnectionSnapshot>(Model.Relationships?.Connections ?? [], 16384);
        var watch = Stopwatch.StartNew();
        var windows = await Task.Run(() => WindowsHiddenEnvironment.GetVisibleWindowOwners(token), token);
        var configured = await persistence.CaptureAsync(token);
        var rawTargets = startup.Take(targetCap).Select(s => (Name: s.Name, Raw: (string?)s.Command, Status: InspectionStatus.Available, Reference: false))
            .Concat(configured.Entries.Where(e => e.Source is PersistenceSourceKind.WinlogonShell or PersistenceSourceKind.WinlogonUserinit or PersistenceSourceKind.WinlogonNotify
                or PersistenceSourceKind.ImageFileExecutionOptionsDebugger or PersistenceSourceKind.SilentProcessExitMonitor or PersistenceSourceKind.AppInitDlls
                or PersistenceSourceKind.ActiveSetupStubPath or PersistenceSourceKind.PrintMonitors or PersistenceSourceKind.NetshHelpers
                or PersistenceSourceKind.WmiEventConsumer or PersistenceSourceKind.LsaAuthenticationPackages or PersistenceSourceKind.LsaSecurityPackages or PersistenceSourceKind.LsaNotificationPackages)
                .Select(e => (Name: e.Name, Raw: e.Value, Status: e.Status, Reference: e.IsReference)));
        var targets = new BoundedItems<(string Name, PersistencePathResult Parsed, InspectionStatus Status, bool Reference)>(
            rawTargets.Select(t => (t.Name, SuspiciousLeadRules.ParsePersistenceTarget(t.Raw), t.Status, t.Reference)), targetCap,
            startup.Count > targetCap || configured.Status == InspectionStatus.Truncated);
        var localRoots = DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)
            .Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool LocalPath(string? path) => path is { Length: > 3 } && path[1] == ':' && localRoots.Contains(Path.GetPathRoot(path) ?? "");
        var paths = processes.Items.Select(p => p.ExecutablePath).Concat(targets.Items.Select(t => t.Parsed.Path))
            .Where(LocalPath).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(signatureCap + 1).ToArray();
        var provenance = new Dictionary<string, ImageProvenance>(StringComparer.OrdinalIgnoreCase);
        var verifier = new ImageSignatureVerifier();
        foreach (string path in paths.Take(signatureCap))
        {
            token.ThrowIfCancellationRequested();
            HiddenPanel.Progress($"Verifying signatures {provenance.Count + 1}/{Math.Min(paths.Length, signatureCap)} (local only)…");
            provenance[path] = await verifier.InspectAsync(path, token);
        }
        using var currentProcess = Process.GetCurrentProcess();
        int currentSession = currentProcess.SessionId;
        var processInputs = processes.Items.Select(p =>
        {
            var parent = processes.Items.FirstOrDefault(candidate => candidate.Identity.Pid == p.ParentPid);
            bool verified = parent is not null && parent.Identity.IsStable && p.Identity.IsStable && parent.Identity.CreationTimeFileTime < p.Identity.CreationTimeFileTime;
            bool? visible = windows.Pids.Items.Contains(p.Identity.Pid) ? true : windows.Status == InspectionStatus.Available && p.SessionId == currentSession ? false : null;
            return new ProcessLeadInput(p, parent, verified, visible, p.ExecutablePath is { } path ? provenance.GetValueOrDefault(path) : null);
        }).ToArray();
        var persistenceTargets = await Task.Run(() => targets.Items.Select(t =>
        {
            token.ThrowIfCancellationRequested();
            var status = t.Status;
            if (t.Parsed.Path is { } candidate && !LocalPath(candidate)) status = InspectionStatus.Unsupported;
            bool? exists = null;
            if (t.Parsed.Path is { } path && status == InspectionStatus.Available)
            {
                try { _ = File.GetAttributes(path); exists = true; }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
                catch (UnauthorizedAccessException) { status = InspectionStatus.AccessDenied; }
                catch (IOException) { status = InspectionStatus.Failed; }
            }
            return new PersistenceTarget(t.Name, t.Parsed.Path, status, exists, t.Parsed.Path is { } parsed ? provenance.GetValueOrDefault(parsed) : null, t.Reference);
        }).ToArray(), token);
        var input = new LeadSweepInput(new(processInputs, processCap, processes.Truncated), endpoints, new(persistenceTargets, targetCap, targets.Truncated), Model.Snapshot is not null && !processes.Truncated);
        var leads = await Task.Run(() => SuspiciousLeadRules.Evaluate(input, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.GetTempPath(), cancellationToken: token), token);
        leads = SuspiciousLeadRules.Aggregate(leads, (Model.Snapshot?.Processes ?? []).Select(p => p.Identity).Where(i => i.IsStable).ToHashSet());
        int unverified = provenance.Values.Count(p => p.Status != InspectionStatus.Available || p.Signature is SignatureState.NotChecked or SignatureState.Unreadable);
        return new(at, $"{leads.Items.Count} local leads · {processes.Items.Count} process observations · {targets.Items.Count} persistence targets",
            $"Visible-window coverage: {HiddenStatus(windows.Status)} · {windows.Detail}\nPersistence: {HiddenStatus(configured.Status)}; {startupError ?? "startup inventory from existing snapshot"}.\nSignatures: {provenance.Count}/{Math.Min(paths.Length, signatureCap)} checked, {unverified} unavailable; unique-image cap {signatureCap}; no online revocation. Endpoints: {endpoints.Items.Count}, {(Model.Relationships is null ? "Unavailable" : "existing observation")}. Total {watch.Elapsed.TotalMilliseconds:F1} ms. Unknown paths/signatures are not treated as missing/unsigned. Raw values and arguments are not displayed.",
            leads.Items, leads.Truncated || paths.Length > signatureCap);
    }
}
