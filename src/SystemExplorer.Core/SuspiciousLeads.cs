namespace SystemExplorer.Core;

public enum LeadSeverity { Info, Low, Medium, High }
public enum LeadCategory { CrossView, Signature, UnusualLocation, Masquerading, ParentMismatch, EndpointOwner, Persistence, NoVisibleWindow }
public sealed record SuspiciousLead(LeadCategory Category, LeadSeverity Severity, string Title, string Reason,
    string Evidence, string BenignNote, ProcessIdentity? ProcessIdentity = null, string? Path = null);
public enum UnusualLocation { None, Temp, AppData, Downloads, ProgramDataRoot, RecycleBin, AlternateDataStream, Public }
public sealed record PersistenceTarget(string Name, string? ParsedPath, InspectionStatus Status, bool? Exists,
    ImageProvenance? Provenance, bool IsReference = false);
public sealed record ProcessLeadInput(ProcessSnapshot Process, ProcessSnapshot? Parent, bool ParentIdentityVerified,
    bool? HasVisibleWindow, ImageProvenance? Provenance);
public sealed record LeadSweepInput(BoundedItems<ProcessLeadInput> Processes, BoundedItems<ConnectionSnapshot> Endpoints,
    BoundedItems<PersistenceTarget> Persistence, bool ProcessInventoryComplete);
public sealed record PersistencePathResult(string? Path, InspectionStatus Status, string Detail);

public static class SuspiciousLeadRules
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    { "svchost.exe", "lsass.exe", "csrss.exe", "smss.exe", "wininit.exe", "winlogon.exe", "services.exe", "taskhostw.exe", "dllhost.exe", "conhost.exe", "spoolsv.exe", "lsm.exe", "RuntimeBroker.exe" };
    private static readonly HashSet<string> Lookalikes = new(StringComparer.OrdinalIgnoreCase) { "scvhost.exe", "svch0st.exe", "lsas.exe" };
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string FileName(string path) => Normalize(path).Split('\\')[^1];
    private static bool Under(string path, string root) => path.StartsWith(Normalize(root) + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool Absolute(string path) => path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        || path.StartsWith("\\\\", StringComparison.Ordinal) && path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    private static bool SafePath(string path) => Absolute(path) && !path.Contains('%') && !path.Contains('"') && !path.Contains('\0')
        && !path.Split('\\').Any(s => s is "." or "..") && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) && !path.StartsWith("\\\\.\\", StringComparison.Ordinal);

    public static UnusualLocation ClassifyLocation(string? path, string? tempDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return UnusualLocation.None;
        var p = Normalize(path);
        if (!SafePath(p)) return UnusualLocation.None;
        if (p.IndexOf(':', p.Length > 1 && p[1] == ':' ? 2 : 0) >= 0) return UnusualLocation.AlternateDataStream;
        if ((tempDirectory is not null && Under(p, tempDirectory)) || p.Contains("\\AppData\\Local\\Temp\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\Windows\\Temp\\", StringComparison.OrdinalIgnoreCase)) return UnusualLocation.Temp;
        if (p.Contains("\\AppData\\Local\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\AppData\\Roaming\\", StringComparison.OrdinalIgnoreCase)) return UnusualLocation.AppData;
        if (p.Contains("\\Downloads\\", StringComparison.OrdinalIgnoreCase)) return UnusualLocation.Downloads;
        if (p.Contains("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase)) return UnusualLocation.RecycleBin;
        if (p.Contains("\\Users\\Public\\", StringComparison.OrdinalIgnoreCase)) return UnusualLocation.Public;
        if (p.Length > 3 && Same(p[3..].Split('\\')[0], "ProgramData") && p[3..].Count(c => c == '\\') == 1) return UnusualLocation.ProgramDataRoot;
        return UnusualLocation.None;
    }

    public static PersistencePathResult ParsePersistenceTarget(string? rawValue)
    {
        PersistencePathResult Unknown() => new(null, InspectionStatus.Unsupported, "Target unavailable: ambiguous or unsupported raw configuration value; not executed or expanded.");
        if (string.IsNullOrWhiteSpace(rawValue) || rawValue.Any(c => c is '\r' or '\n' or '\0')) return Unknown();
        var value = rawValue.Trim();
        string path;
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            if (end < 0 || end + 1 < value.Length && !char.IsWhiteSpace(value[end + 1])) return Unknown();
            path = value[1..end];
        }
        else
        {
            var end = value.IndexOfAny([' ', '\t']);
            path = end < 0 ? value : value[..end];
        }
        path = Normalize(path);
        if (!SafePath(path) || path.IndexOf(':', path[1] == ':' ? 2 : 0) >= 0 || path.IndexOfAny(['*', '?', '|', '<', '>', ',']) >= 0) return Unknown();
        var name = FileName(path);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) return Unknown();
        if (new[] { "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "regsvr32.exe", "mshta.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return Unknown();
        return new(path, InspectionStatus.Available, "Conservatively parsed literal absolute target; arguments discarded.");
    }

    public static SuspiciousLead? SignatureLead(ImageProvenance provenance, ProcessIdentity? identity = null)
    {
        if (provenance.Status != InspectionStatus.Available || provenance.Signature is not (SignatureState.Unsigned or SignatureState.NotTrusted)) return null;
        return new(LeadCategory.Signature, LeadSeverity.Medium, provenance.Signature == SignatureState.Unsigned ? "Unsigned image" : "Signature not trusted", "Local signature verification reports " + provenance.Signature,
            provenance.Path, "Unsigned software may be legitimate; local trust failure does not establish compromise. No online revocation was checked.", identity, provenance.Path);
    }

    public static BoundedItems<SuspiciousLead> Evaluate(LeadSweepInput input, string windowsDirectory = @"C:\Windows",
        string? tempDirectory = null, int cap = 2000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<SuspiciousLead> Rows()
        {
            foreach (var item in input.Processes.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = item.Process;
                var path = p.ExecutablePath;
                if (path is null || !SafePath(Normalize(path))) continue;
                var name = FileName(path);
                SuspiciousLead Lead(LeadCategory category, LeadSeverity severity, string title, string reason, string benign) => new(category, severity, title, reason, path, benign, p.Identity, path);
                var location = ClassifyLocation(path, tempDirectory);
                if (location != UnusualLocation.None) yield return Lead(LeadCategory.UnusualLocation, LeadSeverity.Low, "Unusual image location", location.ToString(), "Installers, portable apps and developer tools often use these locations.");
                if (item.HasVisibleWindow == false && location is UnusualLocation.Temp or UnusualLocation.AppData or UnusualLocation.Downloads or UnusualLocation.Public)
                    yield return Lead(LeadCategory.NoVisibleWindow, LeadSeverity.Low, "No visible top-level window", "Image is in a commonly user-writable location and no visible top-level window was observed.", "Background helpers and tray apps commonly have no visible window; permissions were not inferred from the path.");
                var directory = Normalize(path)[..Normalize(path).LastIndexOf('\\')];
                bool expectedDirectory = Same(directory, Normalize(windowsDirectory) + "\\System32") || Same(directory, Normalize(windowsDirectory) + "\\SysWOW64");
                if ((SystemNames.Contains(name) && !expectedDirectory) || Same(name, "explorer.exe") && !Same(directory, Normalize(windowsDirectory)))
                    yield return Lead(LeadCategory.Masquerading, LeadSeverity.Medium, "System name outside expected directory", name, "A copied diagnostic binary or alternative installation may be legitimate.");
                if (Lookalikes.Contains(name)) yield return Lead(LeadCategory.Masquerading, LeadSeverity.Low, "Lookalike system name", name, "Similar names can be coincidental.");
                var expectedParent = name.ToLowerInvariant() switch { "svchost.exe" => "services.exe", "lsass.exe" or "services.exe" => "wininit.exe", _ => null };
                if (expectedParent is not null && item.ParentIdentityVerified && item.Parent is { } parent && parent.Identity.IsStable && p.Identity.IsStable
                    && p.ParentPid == parent.Identity.Pid && parent.Identity.CreationTimeFileTime < p.Identity.CreationTimeFileTime && !Same(FileName(parent.ExecutablePath ?? parent.Name), expectedParent))
                    yield return Lead(LeadCategory.ParentMismatch, LeadSeverity.Medium, "Unexpected verified parent", $"Expected {expectedParent}; observed {FileName(parent.ExecutablePath ?? parent.Name)}.", "Debuggers, test environments or alternate launch arrangements may explain the parent.");
                if (item.Provenance is { } provenance && Same(provenance.Path, path) && SignatureLead(provenance, p.Identity) is { } signature) yield return signature;
            }
            foreach (var endpoint in input.Endpoints.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!(Same(endpoint.Protocol, "UDP") || Same(endpoint.Protocol, "TCP") && Same(endpoint.State, "Listen"))) continue;
                bool resolved = input.Processes.Items.Any(p => p.Process.Identity.Pid == endpoint.OwningPid &&
                    (endpoint.OwnerIdentity is null || endpoint.OwnerIdentity == p.Process.Identity));
                if (!resolved) yield return new(LeadCategory.EndpointOwner, LeadSeverity.Low, "Endpoint owner unresolved",
                    input.ProcessInventoryComplete && !input.Processes.Truncated ? "Owner PID no longer maps to the observed process inventory." : "Owner could not be resolved in incomplete process coverage.",
                    $"{endpoint.Protocol} {endpoint.LocalAddress}:{endpoint.LocalPort}; PID {endpoint.OwningPid}", "Short-lived processes, PID reuse and access limitations may explain stale owners.");
            }
            foreach (var target in input.Persistence.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.IsReference) continue;
                if (target.ParsedPath is null)
                {
                    yield return new(LeadCategory.Persistence, LeadSeverity.Info, "Persistence target unavailable", "Raw configuration target could not be conservatively parsed.", target.Name, "Indirect launchers and environment variables are common; no missing-file or signature claim is made.");
                    continue;
                }
                if (target.Status != InspectionStatus.Available) continue;
                if (target.Exists == false) yield return new(LeadCategory.Persistence, LeadSeverity.Low, "Persistence target missing", "Target was reported missing.", target.Name, "Uninstalled software or an offline volume may leave stale entries.", Path: target.ParsedPath);
                else if (target.Exists == true && target.Provenance is { } provenance && Same(provenance.Path, target.ParsedPath) && SignatureLead(provenance) is { } signature)
                    yield return signature with { Category = LeadCategory.Persistence, Title = "Persistence: " + signature.Title, Evidence = target.Name };
            }
        }
        return new(Rows().DistinctBy(l => (l.Category, l.Title, l.ProcessIdentity, l.Path, l.Evidence)), cap,
            input.Processes.Truncated || input.Endpoints.Truncated || input.Persistence.Truncated);
    }

    public static BoundedItems<SuspiciousLead> Aggregate(BoundedItems<SuspiciousLead> leads,
        IReadOnlySet<ProcessIdentity> runningIdentities, int detailCap = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(detailCap);
        var rows = leads.Items.GroupBy(l => (l.Category, l.Title, l.Severity, Path: l.Path?.Replace('/', '\\').ToUpperInvariant(),
            Unkeyed: l.Path is null && l.Category != LeadCategory.Persistence ? l.Evidence : "")).Select(group =>
        {
            var first = group.First();
            var identities = group.Where(l => l.ProcessIdentity is not null).Select(l => l.ProcessIdentity!.Value).Distinct().ToArray();
            var evidence = group.Select(l => l.Evidence).Distinct().ToArray();
            string instances = identities.Length == 0 ? "" : $"{identities.Length} running instances observed at capture.\nPIDs: {string.Join(", ", identities.Take(detailCap).Select(i => i.Pid))}" +
                (identities.Length > detailCap ? $" (first {detailCap}; remaining PIDs omitted)" : "") + "\n";
            bool coverage = first.Category == LeadCategory.Persistence && first.Path is null && first.Severity == LeadSeverity.Info;
            return first with
            {
                Title = coverage ? "Persistence targets not assessed" : first.Title,
                Evidence = instances + (coverage ? $"{group.Count()} configuration observations; coverage limitation, not a suspicious finding.\n" : "") +
                    string.Join("\n", evidence.Take(detailCap)) + (evidence.Length > detailCap ? $"\nFirst {detailCap} evidence entries shown." : ""),
                ProcessIdentity = identities.Where(runningIdentities.Contains).Select(i => (ProcessIdentity?)i).FirstOrDefault()
            };
        });
        return new(rows, leads.Items.Count, leads.Truncated);
    }
}
