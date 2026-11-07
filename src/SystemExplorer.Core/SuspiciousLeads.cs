namespace SystemExplorer.Core;

public enum LeadSeverity { Info, Low, Medium, High }
public enum LeadCategory { CrossView, Signature, UnusualLocation, Masquerading, ParentMismatch, EndpointOwner, Persistence, NoVisibleWindow, AlternateDataStream }
public sealed record SuspiciousLead(LeadCategory Category, LeadSeverity Severity, string Title, string Reason,
    string Evidence, string BenignNote, ProcessIdentity? ProcessIdentity = null, string? Path = null,
    BoundedItems<ProcessIdentity>? ProcessIdentities = null, BoundedItems<ProcessSightingSource>? SourcesSeen = null,
    BoundedItems<ProcessSightingSource>? SourcesMissing = null, string? Status = null, string? StatusDetail = null);
public enum UnusualLocation { None, Temp, AppData, Downloads, ProgramDataRoot, RecycleBin, AlternateDataStream, Public }
public sealed record PersistenceTarget(string Name, string? ParsedPath, InspectionStatus Status, bool? Exists,
    ImageProvenance? Provenance, bool IsReference = false);
public sealed record ProcessLeadInput(ProcessSnapshot Process, ProcessSnapshot? Parent, bool ParentIdentityVerified,
    bool? HasVisibleWindow, ImageProvenance? Provenance, string? DeveloperContext = null);
public sealed record LeadSweepInput(BoundedItems<ProcessLeadInput> Processes, BoundedItems<ConnectionSnapshot> Endpoints,
    BoundedItems<PersistenceTarget> Persistence, bool ProcessInventoryComplete);
public sealed record PersistencePathResult(string? Path, InspectionStatus Status, string Detail);

public static class SuspiciousLeadRules
{
    private static readonly string[] SystemNames = ["lsass", "csrss", "smss", "wininit", "winlogon", "services", "svchost", "lsm", "explorer", "spoolsv", "taskhostw", "dwm", "conhost", "rundll32", "dllhost"];

    private static string ConfusableName(string name) => string.Concat(name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormKC).Select(c => c switch
    {
        '0' or 'ο' or 'о' => 'o', '1' or 'i' or 'і' or 'ι' or 'ӏ' => 'l', '5' or 'ѕ' => 's',
        'а' or 'α' => 'a', 'с' or 'ϲ' => 'c', 'е' or 'ε' => 'e', 'р' or 'ρ' => 'p',
        'х' or 'χ' => 'x', 'у' => 'y', 'т' or 'τ' => 't', 'ν' => 'v', 'һ' or 'հ' => 'h',
        'ԁ' => 'd', 'ɡ' => 'g', 'м' => 'm', 'ո' => 'n', 'օ' => 'o', 'ս' or 'υ' => 'u', 'ԝ' => 'w', _ => c
    }));

    private static bool OneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0;
        while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
        if (i == Math.Min(a.Length, b.Length)) return a.Length != b.Length;
        if (a.Length == b.Length)
            return a[(i + 1)..] == b[(i + 1)..] || i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a[(i + 2)..] == b[(i + 2)..];
        return a.Length > b.Length ? a[(i + 1)..] == b[i..] : a[i..] == b[(i + 1)..];
    }

    private static (string Name, bool Exact)? CriticalName(string fileName)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var stem = fileName[..^4].ToLowerInvariant();
        foreach (var name in SystemNames) if (stem == name) return (name, true);
        if (stem.Length is < 5 or > 10 || stem.Any(char.IsSurrogate)) return null;
        foreach (var name in SystemNames.Where(n => n.Length >= 5))
            if (ConfusableName(stem) == ConfusableName(name) || OneEdit(stem, name)) return (name, false);
        return null;
    }

    public static string? DeveloperPathContext(string path)
    {
        var segments = Normalize(path).Split('\\').SkipLast(1).ToArray();
        var segment = segments.FirstOrDefault(s => new[] { "bin", "obj", "target", "dist" }.Contains(s, StringComparer.OrdinalIgnoreCase));
        if (segment is not null) return $"Build output directory: {segment}";
        if (segments.Any(s => new[] { "Microsoft Visual Studio", "Visual Studio", "WSL", "wsl$", "wsl.localhost", "lxss", "Docker", ".docker" }.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return "Visual Studio / WSL / Docker directory";
        return null;
    }

    private static IEnumerable<SuspiciousLead> MasqueradingLeads(ProcessLeadInput item, string windowsDirectory, string? tempDirectory)
    {
        var process = item.Process;
        var path = Normalize(process.ExecutablePath!);
        if (CriticalName(FileName(path)) is not { } match) yield break;
        var directory = path[..path.LastIndexOf('\\')];
        bool expected = match.Name == "explorer" ? Same(directory, Normalize(windowsDirectory))
            : Same(directory, Normalize(windowsDirectory) + "\\System32") || Same(directory, Normalize(windowsDirectory) + "\\SysWOW64");
        bool unusualMicrosoft = new[] { "WinSxS", "servicing", "Temp" }.Any(folder => Under(path, Normalize(windowsDirectory) + "\\" + folder));
        var provenance = item.Provenance is { } value && Same(value.Path, process.ExecutablePath) ? value : null;
        bool trusted = provenance?.Status == InspectionStatus.Available && provenance.Signature is SignatureState.EmbeddedTrusted or SignatureState.CatalogTrusted;
        var signer = provenance?.SignerSubject;
        bool microsoft = trusted && new[] { "Microsoft Corporation", "Microsoft Windows", "Microsoft Windows Publisher", "Microsoft Windows Hardware Compatibility Publisher" }.Any(s => Same(s, signer));
        string signerEvidence = provenance?.Signature == SignatureState.Unsigned ? "unsigned" : string.IsNullOrWhiteSpace(signer) ? "signer unknown; treated as not Microsoft-signed"
            : microsoft ? "Microsoft (" + signer + ")" : signer + (trusted ? "" : "; signature not locally trusted; treated as not Microsoft-signed");
        string signatureState = provenance?.Signature switch
        {
            SignatureState.EmbeddedTrusted => "Embedded trusted", SignatureState.CatalogTrusted => "Catalog trusted",
            SignatureState.NotTrusted => "Not trusted", SignatureState.NotChecked => "Not checked",
            SignatureState.Unsigned => "Unsigned", SignatureState.Unreadable => "Unreadable", _ => "Not checked (provenance unavailable)"
        };
        string signatureEvidence = signatureState + (trusted ? "; locally trusted" : "; treated as unsigned for tiering");
        if (provenance is { Status: not InspectionStatus.Available }) signatureEvidence += "; local verification unavailable: " + provenance.Detail;
        var parentName = match.Name switch { "svchost" => "services.exe", "lsass" or "services" => "wininit.exe", "csrss" or "wininit" => "smss.exe", "smss" => "System", _ => null };
        bool verified = parentName is not null && item.ParentIdentityVerified && item.Parent is { } parent && parent.Identity.IsStable && process.Identity.IsStable
            && process.ParentPid == parent.Identity.Pid && parent.Identity.CreationTimeFileTime < process.Identity.CreationTimeFileTime;
        bool unexpected = verified && !Same(FileName(item.Parent!.ExecutablePath ?? item.Parent.Name), parentName);
        var dev = item.DeveloperContext ?? DeveloperPathContext(path);
        string pathClass = expected ? "expected directory" : unusualMicrosoft ? "expected-but-unusual Microsoft location" : ClassifyLocation(path, tempDirectory) is { } location && location != UnusualLocation.None
            ? "user-writable / unusual location: " + location : "outside expected directory";
        string evidence = $"{path}\nName match: {(match.Exact ? "exact" : "lookalike")} of {match.Name}.exe\nPath class: {pathClass}\nSigner: {signerEvidence}\nSignature state: {signatureEvidence}\nParent: {(verified ? unexpected ? "verified unexpected" : "verified expected" : "unverified")}";
        if (verified) evidence += $"; expected {parentName}; observed {FileName(item.Parent!.ExecutablePath ?? item.Parent.Name)}";
        if (dev is not null) evidence += "\nDev context: " + dev;
        SuspiciousLead Lead(LeadCategory category, LeadSeverity severity, string title, string reason) => new(category, severity, title, reason, evidence,
            "A copied diagnostic binary, developer build, installer or alternate launch arrangement may be legitimate. These factors are leads, not proof of compromise.", process.Identity, process.ExecutablePath);
        if (!expected)
        {
            var severity = match.Exact && unusualMicrosoft && microsoft ? LeadSeverity.Low : microsoft || dev is not null ? LeadSeverity.Medium : LeadSeverity.High;
            yield return Lead(LeadCategory.Masquerading, severity, match.Exact ? "System name outside expected directory" : "Lookalike system name",
                "Critical OS name and location evaluated with local signer and developer-context evidence.");
        }
        if (unexpected && match.Exact)
        {
            bool singleton = match.Name is "lsass" or "csrss" or "smss" or "wininit" or "services";
            yield return Lead(LeadCategory.ParentMismatch, singleton ? LeadSeverity.High : LeadSeverity.Medium, "Unexpected verified parent", $"Expected {parentName}; observed {FileName(item.Parent!.ExecutablePath ?? item.Parent.Name)}.");
        }
    }
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
                SuspiciousLead Lead(LeadCategory category, LeadSeverity severity, string title, string reason, string benign) => new(category, severity, title, reason, path, benign, p.Identity, path);
                var location = ClassifyLocation(path, tempDirectory);
                if (location != UnusualLocation.None) yield return Lead(LeadCategory.UnusualLocation, LeadSeverity.Low, "Unusual image location", location.ToString(), "Installers, portable apps and developer tools often use these locations.");
                if (item.HasVisibleWindow == false && location is UnusualLocation.Temp or UnusualLocation.AppData or UnusualLocation.Downloads or UnusualLocation.Public)
                    yield return Lead(LeadCategory.NoVisibleWindow, LeadSeverity.Low, "No visible top-level window", "Image is in a commonly user-writable location and no visible top-level window was observed.", "Background helpers and tray apps commonly have no visible window; permissions were not inferred from the path.");
                foreach (var masquerading in MasqueradingLeads(item, windowsDirectory, tempDirectory)) yield return masquerading;
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
                ProcessIdentity = identities.Where(runningIdentities.Contains).Select(i => (ProcessIdentity?)i).FirstOrDefault(),
                ProcessIdentities = new(identities, detailCap)
            };
        });
        return new(rows, leads.Items.Count, leads.Truncated);
    }
}
