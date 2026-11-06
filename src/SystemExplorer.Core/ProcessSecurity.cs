using System.Text;

namespace SystemExplorer.Core;

/// <summary>Kernel-supplied command line. May contain secrets: never log, persist, cache or format it; ToString is redacted.</summary>
public sealed record CommandLineSection(InspectionStatus Status, string Detail, string? Text, int? LengthCharacters)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Status = {Status}, Detail = {Detail}, Text = <redacted>, LengthCharacters = {LengthCharacters}");
        return true;
    }
}

public enum IntegrityLevel { Untrusted, Low, Medium, MediumPlus, High, System, Protected }
public enum TokenElevationKind { Default, Full, Limited }

public sealed record TokenPrivilegeObservation(string Name, uint LuidLow, int LuidHigh, uint RawAttributes,
    bool Enabled, bool EnabledByDefault, bool Removed);

public sealed record TokenSection(InspectionStatus Status, string Detail,
    string? UserSid, string? UserAccount,
    IntegrityLevel? Integrity, uint? IntegrityRid,
    bool? IsElevated, TokenElevationKind? ElevationType,
    bool? VirtualizationAllowed, bool? VirtualizationEnabled,
    bool? IsAppContainer, bool? HasPackageIdentity,
    uint? SessionId,
    IReadOnlyList<TokenPrivilegeObservation> Privileges, InspectionStatus PrivilegesStatus,
    int? GroupCount);

public sealed record ProtectionSection(InspectionStatus Status, string Detail, uint? RawLevel, string? LevelName);

public enum MitigationPolicyKind
{
    Dep, Aslr, DynamicCode, StrictHandleCheck, SystemCallDisable, ExtensionPointDisable, ControlFlowGuard,
    BinarySignature, FontDisable, ImageLoad, SideChannelIsolation, UserShadowStack, ChildProcess, RedirectionTrust
}

public sealed record MitigationFlag(string Name, bool Value);

public sealed record MitigationPolicyObservation(MitigationPolicyKind Kind, InspectionStatus Status, string Detail,
    uint? RawFlags, IReadOnlyList<MitigationFlag> Flags);

public sealed record MitigationSection(InspectionStatus Status, string Detail, IReadOnlyList<MitigationPolicyObservation> Policies);

public sealed record JobSection(InspectionStatus Status, string Detail, bool? InAnyJob);

public sealed record RuntimeModuleObservation(string ModuleName, string Path, string RuntimeFamily, string? FileVersion, string? ProductVersion);

/// <summary>.NET runtime presence detected from loaded modules only; no assembly lists.</summary>
public sealed record RuntimeSection(InspectionStatus Status, string Detail, string Source, IReadOnlyList<RuntimeModuleObservation> Modules);

public enum SignatureState { EmbeddedTrusted, CatalogTrusted, Unsigned, NotTrusted, Unreadable, NotChecked }

/// <summary>Local-only provenance. Trust means local chain validation at VerifiedAt; revocation is never checked online.</summary>
public sealed record ImageProvenance(string Path, InspectionStatus Status, string Detail,
    long? SizeBytes, DateTimeOffset? LastWriteTime,
    string? CompanyName, string? ProductName, string? FileDescription, string? FileVersion, string? ProductVersion,
    SignatureState Signature, int? SignatureHResult, string? SignerSubject, string? CatalogPath, DateTimeOffset VerifiedAt);

public sealed record ImageSection(InspectionStatus Status, string Detail, string? Path, ImageProvenance? Provenance);

public sealed record ProcessSecurityInspection(ProcessIdentity Identity, DateTimeOffset CapturedAt, InspectionStatus Status, string Detail,
    CommandLineSection CommandLine, TokenSection Token, ProtectionSection Protection, MitigationSection Mitigations,
    JobSection Job, RuntimeSection Runtime, ImageSection Image);

public interface IProcessSecurityProvider
{
    Task<ProcessSecurityInspection> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken);
}

public interface IImageSignatureVerifier
{
    Task<ImageProvenance> InspectAsync(string path, CancellationToken cancellationToken);
}
