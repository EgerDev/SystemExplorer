namespace SystemExplorer.Core;

public enum PersistenceSourceKind
{
    WmiEventFilter, WmiEventConsumer, WmiFilterToConsumerBinding,
    WinlogonShell, WinlogonUserinit, WinlogonNotify,
    ImageFileExecutionOptionsDebugger, ImageFileExecutionOptionsGlobalFlag, SilentProcessExitMonitor,
    AppInitDlls, LoadAppInitDlls, ActiveSetupStubPath, KnownDlls,
    LsaAuthenticationPackages, LsaSecurityPackages, LsaNotificationPackages,
    PrintMonitors, NetshHelpers
}

public enum PersistenceScope { LocalMachine64, LocalMachine32, CurrentUser64, CurrentUser32, WmiLocalNamespace }

/// <summary>One observed configuration value. Value is raw and unexpanded (REG_DWORD as 0x hex text);
/// REG_MULTI_SZ yields one row per item. IsReference marks rows shown for context (KnownDLLs), not as suspicious.
/// Properties holds only the documented extra fields (e.g. WMI QueryLanguage, ConsumerClass); never script text.</summary>
public sealed record PersistenceEntry(PersistenceSourceKind Source, string Location, string Name, string? Value,
    PersistenceScope Scope, InspectionStatus Status, string Detail, bool IsReference,
    IReadOnlyDictionary<string, string?> Properties);

/// <summary>Per source and scope. A missing key/namespace is an Available observation with zero rows.</summary>
public sealed record PersistenceSourceStatus(PersistenceSourceKind Source, PersistenceScope Scope, string Location,
    InspectionStatus Status, string Detail, int RowCount);

public sealed record PersistenceInventory(DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    IReadOnlyList<PersistenceSourceStatus> Sources, IReadOnlyList<PersistenceEntry> Entries);

public interface IPersistenceInventoryProvider
{
    Task<PersistenceInventory> CaptureAsync(CancellationToken cancellationToken);
}
