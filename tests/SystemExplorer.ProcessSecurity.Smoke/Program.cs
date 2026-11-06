using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using SystemExplorer.Windows;
using static SystemExplorer.Windows.WindowsProcessSecurityProvider;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Cancelled(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation.");
}
static byte[] Unicode(string text, int pointerSize = 8)
{
    var header = pointerSize == 8 ? 16 : 8;
    var bytes = new byte[header + Encoding.Unicode.GetByteCount(text)];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(bytes.Length - header));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)(bytes.Length - header));
    if (pointerSize == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)(0x1000 + header));
    else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(0x1000 + header));
    Encoding.Unicode.GetBytes(text).CopyTo(bytes, header);
    return bytes;
}
static void Discarded(ProcessSecurityInspection value, InspectionStatus status)
{
    Check(value.Status == status && value.CommandLine.Status == status && value.Token.Status == status &&
        value.Protection.Status == status && value.Mitigations.Status == status && value.Job.Status == status &&
        value.Runtime.Status == status && value.Image.Status == status, "Every section must carry the discard status.");
    Check(value.CommandLine.Text is null && value.CommandLine.LengthCharacters is null && value.Token.UserSid is null &&
        value.Token.UserAccount is null && value.Token.Integrity is null && value.Token.IntegrityRid is null &&
        value.Token.IsElevated is null && value.Token.ElevationType is null && value.Token.VirtualizationAllowed is null &&
        value.Token.VirtualizationEnabled is null && value.Token.IsAppContainer is null && value.Token.HasPackageIdentity is null &&
        value.Token.SessionId is null && value.Token.GroupCount is null && value.Token.Privileges.Count == 0 &&
        value.Token.PrivilegesStatus == status && value.Protection.RawLevel is null && value.Protection.LevelName is null &&
        value.Mitigations.Policies.Count == 0 && value.Job.InAnyJob is null && value.Runtime.Modules.Count == 0 &&
        value.Image.Path is null && value.Image.Provenance is null, "Discard must clear every observed value.");
}
static Task<ProcessSecurityInspection> Capture(FakeNative native, FakeModules? modules = null, ProcessIdentity? identity = null, CancellationToken ct = default) =>
    new WindowsProcessSecurityProvider(native, modules ?? new(), new FakeImage()).CaptureAsync(identity ?? new(123, 456), ct);

const string secret = "planted-secret-do-not-print";
foreach (var pointerSize in new[] { 4, 8 })
{
    var parsed = ParseCommandLine(Unicode(secret, pointerSize), 0x1000, pointerSize);
    Check(parsed.Text == secret && parsed.LengthCharacters == secret.Length && parsed.Status == InspectionStatus.Available, "Unicode text and length must be exact.");
    Check(!parsed.ToString().Contains(secret), "CommandLineSection must redact secrets.");
}
foreach (var mutate in new Action<byte[]>[]
{
    b => BinaryPrimitives.WriteUInt16LittleEndian(b, 3),
    b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 0),
    b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 0xFFF),
    b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 0x2000),
    b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), (ulong)(0x1000 + b.Length - 1))
})
{
    var bytes = Unicode(secret);
    mutate(bytes);
    var parsed = ParseCommandLine(bytes, 0x1000, 8);
    Check(parsed.Status == InspectionStatus.Failed && parsed.Text is null, "Invalid Unicode bounds must omit text.");
}
var empty = ParseCommandLine(new byte[16], 0x1000, 8);
Check(empty.Status == InspectionStatus.Available && empty.Text == "" && empty.LengthCharacters == 0, "Empty Unicode string is available.");
Check(ParseCommandLine([], 0, 8).Status == InspectionStatus.Failed, "Missing structure must fail.");
var redacted = await Capture(new() { Text = secret });
Check(!redacted.ToString().Contains(secret), "Aggregate ToString must redact command text.");
Console.WriteLine("PASS: command-line parsing and aggregate redaction.");

foreach (var (ntstatus, expected) in new[]
{
    (0xC0000022u, InspectionStatus.AccessDenied), (0xC0000003u, InspectionStatus.Unsupported),
    (0xC0000002u, InspectionStatus.Unsupported), (0xC0000225u, InspectionStatus.Unsupported)
})
{
    var result = await Capture(new() { QueryStatus = unchecked((int)ntstatus) });
    Check(result.CommandLine.Status == expected && result.CommandLine.Text is null, "NTSTATUS mapping must be honest.");
    if (ntstatus == 0xC0000225) Check(result.CommandLine.Detail.Contains("kernel reports no command line"), "Missing command detail.");
}
foreach (var ntstatus in new[] { 0xC0000004u, 0x80000005u, 0xC0000023u })
{
    var native = new FakeNative { QueryStatus = unchecked((int)ntstatus), Required = 32769 };
    var result = await Capture(native);
    Check(result.CommandLine.Status == InspectionStatus.Truncated && result.CommandLine.Text is null && result.CommandLine.LengthCharacters is null && native.QueryCalls == 1, "Oversized queries must not read a length from the unfilled buffer.");
}
var retryNative = new FakeNative { QueryStatus = unchecked((int)0xC0000004), Required = 4096 };
var retried = await Capture(retryNative);
Check(retried.CommandLine.Status == InspectionStatus.Truncated && retried.CommandLine.Text is null && retryNative.QueryCalls == 3, "Retry cap must bound command queries.");
Console.WriteLine("PASS: fake native command query errors, buffer bound and retry cap.");

var privilegeBuffer = new byte[4 + 129 * 12];
BinaryPrimitives.WriteUInt32LittleEndian(privilegeBuffer, 129);
for (var i = 0; i < 129; i++)
{
    BinaryPrimitives.WriteUInt32LittleEndian(privilegeBuffer.AsSpan(4 + i * 12), (uint)i);
    BinaryPrimitives.WriteInt32LittleEndian(privilegeBuffer.AsSpan(8 + i * 12), -2);
    BinaryPrimitives.WriteUInt32LittleEndian(privilegeBuffer.AsSpan(12 + i * 12), (uint)(i % 8));
}
var privileges = ParsePrivileges(privilegeBuffer, out var truncated);
Check(truncated && privileges.Length == 128 && privileges[7].Low == 7 && privileges[7].High == -2 && privileges[7].Attributes == 7,
    "Privilege count, LUID and attributes must decode exactly.");
Check((privileges[2].Attributes & 2) != 0 && (privileges[1].Attributes & 1) != 0 && (privileges[4].Attributes & 4) != 0, "Privilege enabled/default/removed bits.");
Check(ParsePrivileges(new byte[4], out truncated).Length == 0 && !truncated, "Empty privilege list.");
try { ParsePrivileges(privilegeBuffer.AsSpan(0, 16), out _); throw new InvalidOperationException("Invalid count accepted."); }
catch (ArgumentException) { }
Console.WriteLine("PASS: privilege decoding, malformed count and 128-entry cap.");

var sid = new byte[28];
sid[16] = 1; sid[17] = 1; sid[23] = 16;
BinaryPrimitives.WriteUInt32LittleEndian(sid.AsSpan(24), 0x2100);
Check(TrySidBounds(sid, 0x1000, 0x1010, out var offset, out var sidLength) && offset == 16 && sidLength == 12 && ParseIntegrityRid(sid, 0x1000, 0x1010) == 0x2100, "SID bounds and RID.");
Check(!TrySidBounds(sid, 0x1000, 0xFFF, out _, out _) && !TrySidBounds(sid, 0x1000, 0x101C, out _, out _) && ParseIntegrityRid([], 0, 0) is null, "Invalid SID bounds.");
sid[17] = 15;
Check(!TrySidBounds(sid, 0x1000, 0x1010, out _, out _), "SID count must fit buffer.");
foreach (var (rid, level) in new[] { (0u, IntegrityLevel.Untrusted), (0xFFFu, IntegrityLevel.Untrusted), (0x1000u, IntegrityLevel.Low), (0x1FFFu, IntegrityLevel.Low), (0x2000u, IntegrityLevel.Medium), (0x20FFu, IntegrityLevel.Medium), (0x2100u, IntegrityLevel.MediumPlus), (0x2FFFu, IntegrityLevel.MediumPlus), (0x3000u, IntegrityLevel.High), (0x3FFFu, IntegrityLevel.High), (0x4000u, IntegrityLevel.System), (0x4FFFu, IntegrityLevel.System), (0x5000u, IntegrityLevel.Protected), (uint.MaxValue, IntegrityLevel.Protected) })
    Check(MapIntegrity(rid) == level, "Integrity boundary.");
Check(MapElevation(1) == TokenElevationKind.Default && MapElevation(2) == TokenElevationKind.Full && MapElevation(3) == TokenElevationKind.Limited && MapElevation(4) is null && MapElevation(null) is null, "Elevation values.");
string[] protectionNames = ["WINTCB_LIGHT", "WINDOWS", "WINDOWS_LIGHT", "ANTIMALWARE_LIGHT", "LSA_LIGHT", "WINTCB", "CODEGEN_LIGHT", "AUTHENTICODE", "PPL_APP"];
for (uint i = 0; i < protectionNames.Length; i++) Check(ProtectionName(i) == protectionNames[i], "Protection level.");
Check(ProtectionName(0xFFFFFFFE) == "NONE" && ProtectionName(123) == "Unknown (0x0000007B)", "Unknown protection level.");
Check(MapError(87) == InspectionStatus.Failed && MapOpenError(87) == InspectionStatus.Exited && MapError(5) == InspectionStatus.AccessDenied && MapOpenError(5) == InspectionStatus.AccessDenied && MapError(50) == InspectionStatus.Unsupported, "Open/operation error split.");
Console.WriteLine("PASS: SID, integrity, elevation, protection and error mappers.");

int[] numbers = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 14, 15, 13, 16];
foreach (var kind in Enum.GetValues<MitigationPolicyKind>()) Check(PolicyNumber(kind) == numbers[(int)kind], "Mitigation SDK enum value.");
foreach (var (kind, bit, name) in new[] { (MitigationPolicyKind.Aslr, 2, "EnableHighEntropy"), (MitigationPolicyKind.DynamicCode, 3, "AuditProhibitDynamicCode"), (MitigationPolicyKind.ControlFlowGuard, 4, "EnableXfgAuditMode"), (MitigationPolicyKind.ChildProcess, 2, "AllowSecureProcessCreation"), (MitigationPolicyKind.SideChannelIsolation, 4, "RestrictCoreSharing"), (MitigationPolicyKind.UserShadowStack, 9, "SetContextIpValidationRelaxedMode"), (MitigationPolicyKind.RedirectionTrust, 1, "AuditRedirectionTrust") })
{
    var raw = 1u << bit;
    var decoded = DecodeMitigation(kind, raw);
    var observation = new MitigationPolicyObservation(kind, InspectionStatus.Available, "fixed", raw, decoded);
    Check(observation.RawFlags == raw && decoded.Single(f => f.Value).Name == name, "Named bit and raw flags must be preserved.");
}
Check(DecodeMitigation(MitigationPolicyKind.Dep, 2, true).Single(f => f.Name == "Permanent").Value && !DecodeMitigation(MitigationPolicyKind.Dep, 2).Single(f => f.Name == "Enable").Value, "DEP BOOLEAN Permanent is independent of DWORD.");
Console.WriteLine("PASS: mitigation enum, bit positions, raw flags and DEP Permanent.");

var noIdentityNative = new FakeNative();
Discarded(await Capture(noIdentityNative, identity: new(123, null)), InspectionStatus.IdentityMismatch);
Check(noIdentityNative.Calls == 0, "Unknown creation time must make no native calls.");
using (var ct = new CancellationTokenSource())
{
    ct.Cancel();
    var native = new FakeNative();
    await Cancelled(() => Capture(native, ct: ct.Token));
    Check(native.Calls == 0, "Pre-cancellation must make no native calls.");
}
foreach (var status in new[] { InspectionStatus.IdentityMismatch, InspectionStatus.Exited })
{
    var native = new FakeNative { FinalValidation = status, Text = secret };
    Discarded(await Capture(native), status);
    Check(native.Validations == 2 && native.CommandCollections == 1, "Final validation must follow actual collection.");
}
var independent = await Capture(new() { TokenStatus = InspectionStatus.AccessDenied });
Check(independent.Token.PrivilegesStatus == InspectionStatus.AccessDenied && independent.Token.Privileges.Count == 0 && !string.IsNullOrWhiteSpace(independent.Token.Detail), "Empty denied privileges must retain status and reason, not imply none reported.");
var budgetExpired = await Capture(new() { TimeoutMitigations = true });
Check(budgetExpired.Mitigations.Status == InspectionStatus.TimedOut && budgetExpired.Mitigations.Policies.Count == 0 && !string.IsNullOrWhiteSpace(budgetExpired.Mitigations.Detail) &&
    budgetExpired.Runtime.Status == InspectionStatus.TimedOut && budgetExpired.Runtime.Modules.Count == 0 && !string.IsNullOrWhiteSpace(budgetExpired.Runtime.Detail), "Budget expiry must retain explicit reasons for empty mitigation and runtime sections.");
Console.WriteLine("PASS: empty denied privileges and timed-out mitigation/runtime sections retain status and reason.");
Check(independent.Status == InspectionStatus.Partial && independent.Token.Status == InspectionStatus.AccessDenied && independent.CommandLine.Status == InspectionStatus.Available && independent.Image.Status == InspectionStatus.Available && independent.Job.Status == InspectionStatus.Available, "Sections must remain independent.");
var runtimePresent = await Capture(new(), new() { Rows = [new("coreclr.dll", "C:\\nonexistent-security-smoke\\coreclr.dll", 0, 1)] });
Check(runtimePresent.Runtime.Modules.Single().RuntimeFamily == ".NET", "Runtime identification uses loaded module file name.");
var runtimeEmpty = await Capture(new());
Check(runtimeEmpty.Runtime.Status == InspectionStatus.Available && runtimeEmpty.Runtime.Modules.Count == 0, "Complete empty module inventory.");
foreach (var status in new[] { InspectionStatus.AccessDenied, InspectionStatus.Truncated, InspectionStatus.Exited })
{
    var result = await Capture(new(), new() { Status = status });
    Check(result.Runtime.Status == status && result.Runtime.Modules.Count == 0 && result.Runtime.Detail.Contains("absence not established"), "Empty incomplete runtime status must propagate.");
}
var runtimePartial = await Capture(new(), new() { Status = InspectionStatus.Truncated, Rows = [new("coreclr.dll", "C:\\nonexistent-security-smoke\\coreclr.dll", 0, 1)] });
Check(runtimePartial.Runtime.Status == InspectionStatus.Partial && runtimePartial.Runtime.Modules.Count == 1, "Retained incomplete runtime prefix is partial.");
Console.WriteLine("PASS: provider identity/discard, cancellation, section independence and runtime inventory.");

var directory = Path.Combine(Path.GetTempPath(), "SystemExplorer.ProcessSecurity." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "unsigned.bin");
    File.WriteAllBytes(path, [1, 2, 3]);
    const int noSignature = unchecked((int)0x800B0100);
    const int untrusted = unchecked((int)0x800B0109);
    foreach (var (hr, catalog, expected) in new[] { (0, (string?)null, SignatureState.EmbeddedTrusted), (noSignature, "synthetic.cat", SignatureState.CatalogTrusted), (noSignature, null, SignatureState.Unsigned), (untrusted, null, SignatureState.NotTrusted) })
    {
        var fake = new FakeTrust { HResult = hr, Catalog = catalog };
        var value = await new ImageSignatureVerifier(fake).InspectAsync(path, default);
        Check(value.Signature == expected && value.CatalogPath == catalog && value.SignatureHResult == (catalog is null ? hr : 0), "Fake trust result and HRESULT.");
    }
    var cacheNative = new FakeTrust();
    var verifier = new ImageSignatureVerifier(cacheNative);
    var first = await verifier.InspectAsync(path, default);
    Check(ReferenceEquals(first, await verifier.InspectAsync(path, default)) && cacheNative.Calls == 1, "Cache hit must avoid native calls and return cached record.");
    File.WriteAllBytes(path, [1, 2, 3, 4]);
    await verifier.InspectAsync(path, default);
    Check(cacheNative.Calls == 2, "Changed file must miss cache.");
    var missing = await verifier.InspectAsync(Path.Combine(directory, "missing"), default);
    Check(missing.Signature == SignatureState.Unreadable && missing.Status == InspectionStatus.Failed, "Missing file must be unreadable.");
    var evictionNative = new FakeTrust();
    var eviction = new ImageSignatureVerifier(evictionNative);
    for (var i = 0; i <= ImageSignatureVerifier.CacheCapacity; i++)
    {
        var item = Path.Combine(directory, i + ".bin");
        File.WriteAllBytes(item, [1]);
        await eviction.InspectAsync(item, default);
    }
    await eviction.InspectAsync(Path.Combine(directory, "0.bin"), default);
    Check(evictionNative.Calls == ImageSignatureVerifier.CacheCapacity + 2, "Least recently used record must be evicted.");
    foreach (var hr in new[] { noSignature, unchecked((int)0x800B0003), unchecked((int)0x800B0001) })
        Check(ImageSignatureVerifier.MapHResult(hr) == SignatureState.Unsigned, "Unsigned HRESULT mapping.");
    Check(ImageSignatureVerifier.MapHResult(0) == SignatureState.EmbeddedTrusted && ImageSignatureVerifier.MapHResult(0, true) == SignatureState.CatalogTrusted && ImageSignatureVerifier.MapHResult(untrusted) == SignatureState.NotTrusted, "Trust HRESULT table.");
    Console.WriteLine("PASS: fake image trust, missing file, cache hit/invalidation/eviction and HRESULT table.");

    Check(OperatingSystem.IsWindows(), "Live smoke requires Windows.");
    using var currentHandle = NativeMethods.OpenProcess(0x1000, false, (uint)Environment.ProcessId);
    Check(!currentHandle.IsInvalid && NativeMethods.GetProcessTimes(currentHandle, out _, out _, out _, out _), "Current identity acquisition.");
    NativeMethods.GetProcessTimes(currentHandle, out var creation, out _, out _, out _);
    var identity = new ProcessIdentity(Environment.ProcessId, (long)creation.Ticks);
    var real = new WindowsProcessSecurityProvider();
    var live = await real.CaptureAsync(identity, default);
    Check(live.Status is not (InspectionStatus.IdentityMismatch or InspectionStatus.Exited) && live.CommandLine.Status == InspectionStatus.Available && live.CommandLine.LengthCharacters > 0, "Current process identity and command observation.");
    Check(live.Token.Status == InspectionStatus.Available && live.Token.UserSid is not null && live.Token.Integrity is not null, "Current token SID and integrity.");
    Check(live.Mitigations.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.AccessDenied, "Honest mitigation status.");
    foreach (var (status, detail) in new[] { (live.Status, live.Detail), (live.CommandLine.Status, live.CommandLine.Detail), (live.Token.Status, live.Token.Detail), (live.Protection.Status, live.Protection.Detail), (live.Mitigations.Status, live.Mitigations.Detail), (live.Job.Status, live.Job.Detail), (live.Runtime.Status, live.Runtime.Detail), (live.Image.Status, live.Image.Detail) })
        Check(Enum.IsDefined(status) && !string.IsNullOrWhiteSpace(detail), "Every section must explain a defined status.");
    Console.WriteLine($"PASS: live current process; overall={live.Status}, command={live.CommandLine.Status}, length={live.CommandLine.LengthCharacters}, token={live.Token.Status}, mitigations={live.Mitigations.Status}.");
    using var systemHandle = NativeMethods.OpenProcess(0x1000, false, 4);
    long? systemCreation = null;
    if (!systemHandle.IsInvalid && NativeMethods.GetProcessTimes(systemHandle, out var systemTime, out _, out _, out _)) systemCreation = (long)systemTime.Ticks;
    var system = await real.CaptureAsync(new(4, systemCreation), default);
    Check(system.CommandLine.Status != InspectionStatus.Available && system.CommandLine.Text is null, "PID 4 must not publish available command text on this host.");
    Console.WriteLine($"PASS: live PID 4; overall={system.Status}, command={system.CommandLine.Status}, length={system.CommandLine.LengthCharacters}, image={system.Image.Status}.");
    Discarded(await real.CaptureAsync(identity with { CreationTimeFileTime = identity.CreationTimeFileTime + 1 }, default), InspectionStatus.IdentityMismatch);
    Console.WriteLine("PASS: live fabricated identity discarded.");
    var realVerifier = new ImageSignatureVerifier();
    foreach (var file in new[] { "kernel32.dll", "notepad.exe" })
    {
        var value = await realVerifier.InspectAsync(Path.Combine(Environment.SystemDirectory, file), default);
        Check(value.Signature is SignatureState.EmbeddedTrusted or SignatureState.CatalogTrusted && !string.IsNullOrWhiteSpace(value.SignerSubject), "Windows image must have local trust and signer.");
        Console.WriteLine($"PASS: live {file}; signature={value.Signature}.");
    }
    var unsigned = await realVerifier.InspectAsync(path, default);
    Check(unsigned.Signature is SignatureState.Unsigned or SignatureState.NotTrusted, "Unsigned fixture must not be trusted.");
    Console.WriteLine($"PASS: live unsigned file; signature={unsigned.Signature}.");
}
finally { Directory.Delete(directory, true); }

sealed class FakeNative : IProcessSecurityNative
{
    public int Calls, Validations, QueryCalls, CommandCollections;
    public string Text = "synthetic";
    public int? QueryStatus;
    public uint Required;
    public InspectionStatus? FinalValidation;
    public InspectionStatus TokenStatus = InspectionStatus.Available;
    public bool TimeoutMitigations;
    public SafeProcessHandle Open(int pid, uint access, out int error) { Calls++; error = 0; return new(new IntPtr(123), false); }
    public (InspectionStatus Status, string Detail)? Validate(SafeProcessHandle process, ProcessIdentity identity)
    {
        Calls++; Validations++;
        return Validations == 2 && FinalValidation.HasValue ? (FinalValidation.Value, "synthetic lifetime changed") : null;
    }
    public CommandLineSection CommandLine(SafeProcessHandle process, Action check)
    {
        Calls++; CommandCollections++;
        if (QueryStatus is null) return new(InspectionStatus.Available, "synthetic command", Text, Text.Length);
        return ReadCommandLine((IntPtr buffer, uint size, out uint required) =>
        { QueryCalls++; required = Required; return QueryStatus.Value; }, check);
    }
    public TokenSection Token(SafeProcessHandle process, Action check)
    {
        Calls++;
        return new(TokenStatus, "synthetic token", "S-1-5-18", "synthetic", IntegrityLevel.System, 0x4000, true,
            TokenElevationKind.Default, false, false, false, false, 1, [], TokenStatus, 1);
    }
    public ProtectionSection Protection(SafeProcessHandle process) { Calls++; return new(InspectionStatus.Available, "synthetic protection", 0xFFFFFFFE, "NONE"); }
    public MitigationSection Mitigations(int pid, Action check) { Calls++; if (TimeoutMitigations) throw new TimeoutException(); return new(InspectionStatus.Available, "synthetic mitigations", [new(MitigationPolicyKind.Dep, InspectionStatus.Available, "synthetic", 1, DecodeMitigation(MitigationPolicyKind.Dep, 1))]); }
    public JobSection Job(SafeProcessHandle process) { Calls++; return new(InspectionStatus.Available, "synthetic job", false); }
    public (InspectionStatus Status, string Detail, string? Path) ImagePath(SafeProcessHandle process) { Calls++; return (InspectionStatus.Available, "synthetic image", "synthetic.exe"); }
}
sealed class FakeModules : IProcessInspectionProvider
{
    public InspectionStatus Status = InspectionStatus.Available;
    public IReadOnlyList<ModuleObservation> Rows = [];
    public Task<ProcessInspection> CaptureAsync(ProcessIdentity identity, ProcessInspectionKind kind, CancellationToken ct) =>
        Task.FromResult(new ProcessInspection(identity, kind, DateTimeOffset.UtcNow, Status, "synthetic modules", Rows, []));
}
sealed class FakeImage : IImageSignatureVerifier
{
    public Task<ImageProvenance> InspectAsync(string path, CancellationToken ct) => Task.FromResult(new ImageProvenance(path,
        InspectionStatus.Available, "synthetic provenance", 1, DateTimeOffset.UnixEpoch, null, null, null, null, null,
        SignatureState.EmbeddedTrusted, 0, "synthetic signer", null, DateTimeOffset.UnixEpoch));
}
sealed class FakeTrust : ImageSignatureVerifier.IImageTrustNative
{
    public int Calls, HResult;
    public string? Catalog;
    public ImageSignatureVerifier.TrustResult Verify(string path, IntPtr file, string? catalog, string? tag, IntPtr admin)
    { Calls++; return new(HResult, HResult == 0 ? "synthetic signer" : null); }
    public ImageSignatureVerifier.CatalogResult VerifyCatalog(string path, IntPtr file, Action check) =>
        new(Catalog, new(Catalog is null ? unchecked((int)0x800B0100) : 0, Catalog is null ? null : "synthetic catalog signer"));
}
