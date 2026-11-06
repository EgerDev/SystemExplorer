using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsProcessSecurityProvider : IProcessSecurityProvider
{
    private const string RuntimeSource = "detected from loaded modules";
    private readonly IProcessSecurityNative native;
    private readonly IProcessInspectionProvider modules;
    private readonly IImageSignatureVerifier images;
    public WindowsProcessSecurityProvider() : this(new ProcessSecurityNative(), new WindowsProcessInspectionProvider(), new ImageSignatureVerifier()) { }
    internal WindowsProcessSecurityProvider(IProcessSecurityNative native, IProcessInspectionProvider modules, IImageSignatureVerifier images)
    {
        this.native = native;
        this.modules = modules;
        this.images = images;
    }

    public Task<ProcessSecurityInspection> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identity, cancellationToken), cancellationToken);

    private async Task<ProcessSecurityInspection> Capture(ProcessIdentity identity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var command = EmptyCommand(InspectionStatus.TimedOut, "Observation budget reached before collection.");
        var token = EmptyToken(command.Status, command.Detail);
        var protection = new ProtectionSection(command.Status, command.Detail, null, null);
        var mitigations = new MitigationSection(command.Status, command.Detail, []);
        var job = new JobSection(command.Status, command.Detail, null);
        var runtime = new RuntimeSection(command.Status, command.Detail, RuntimeSource, []);
        var image = new ImageSection(command.Status, command.Detail, null, null);
        ProcessSecurityInspection Discard(InspectionStatus status, string detail) => new(identity, captured, status, detail,
            EmptyCommand(status, detail), EmptyToken(status, detail), new(status, detail, null, null), new(status, detail, []),
            new(status, detail, null), new(status, detail, RuntimeSource, []), new(status, detail, null, null));
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new TimeoutException();
        }
        bool CanCollect() { ct.ThrowIfCancellationRequested(); return clock.Elapsed < TimeSpan.FromSeconds(10); }
        if (identity.CreationTimeFileTime is null)
            return Discard(InspectionStatus.IdentityMismatch, "creation time unknown; cannot attribute data to this process lifetime.");
        if (identity.Pid <= 0) return Discard(InspectionStatus.Failed, "A positive PID is required.");
        using var process = native.Open(identity.Pid, 0x1000 | 0x100000, out var error);
        if (process.IsInvalid) return Discard(MapOpenError(error), $"OpenProcess failed (Windows error {error}); no elevation or fallback.");
        var validation = native.Validate(process, identity);
        if (validation is { } invalid) return Discard(invalid.Status, invalid.Detail);
        try
        {
            if (CanCollect()) command = native.CommandLine(process, Check);
            if (CanCollect()) token = native.Token(process, Check);
            if (CanCollect()) protection = native.Protection(process);
            if (CanCollect()) mitigations = native.Mitigations(identity.Pid, Check);
            if (CanCollect()) job = native.Job(process);
            if (CanCollect())
            {
                var inventory = await modules.CaptureAsync(identity, ProcessInspectionKind.Modules, ct).ConfigureAwait(false);
                var rows = new List<RuntimeModuleObservation>();
                var versionFailures = false;
                foreach (var module in inventory.Modules.Take(4096))
                {
                    Check();
                    var family = RuntimeFamily(Path.GetFileName(module.Path));
                    if (family is null) continue;
                    string? fileVersion = null, productVersion = null;
                    try
                    {
                        var version = FileVersionInfo.GetVersionInfo(module.Path);
                        fileVersion = NullIfEmpty(version.FileVersion);
                        productVersion = NullIfEmpty(version.ProductVersion);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { versionFailures = true; }
                    rows.Add(new(Path.GetFileName(module.Path), module.Path, family, fileVersion, productVersion));
                }
                var complete = inventory.Status == InspectionStatus.Available && inventory.Modules.Count <= 4096;
                var runtimeStatus = !complete && rows.Count == 0 && inventory.Status != InspectionStatus.Available
                    ? inventory.Status : complete && !versionFailures ? InspectionStatus.Available : InspectionStatus.Partial;
                runtime = new(runtimeStatus,
                    !complete ? $"Module inspection status {inventory.Status}; absence not established."
                    : versionFailures ? "Runtime modules observed; some version information was unreadable."
                    : rows.Count == 0 ? "no .NET runtime modules loaded" : "Runtime modules observed; no assembly enumeration.", RuntimeSource, rows.ToArray());
            }
            if (CanCollect())
            {
                var path = native.ImagePath(process);
                image = path.Path is null ? new(path.Status, path.Detail, null, null)
                    : new ImageSection(InspectionStatus.TimedOut, "Observation budget reached before image provenance collection.", path.Path, null);
                if (path.Path is not null)
                {
                    Check();
                    var provenance = await images.InspectAsync(path.Path, ct).ConfigureAwait(false);
                    image = new(provenance.Status, provenance.Detail, path.Path, provenance);
                }
            }
        }
        catch (TimeoutException) { }
        ct.ThrowIfCancellationRequested();
        validation = native.Validate(process, identity);
        if (validation is { } changed) return Discard(changed.Status, changed.Detail);
        var statuses = new[] { command.Status, token.Status, protection.Status, mitigations.Status, job.Status, runtime.Status, image.Status };
        return new(identity, captured, statuses.All(s => s == InspectionStatus.Available) ? InspectionStatus.Available : InspectionStatus.Partial,
            "Read-only local observations; unsupported, incomplete and denied sections are not inferred.", command, token, protection, mitigations, job, runtime, image);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static CommandLineSection EmptyCommand(InspectionStatus status, string detail) => new(status, detail, null, null);
    private static TokenSection EmptyToken(InspectionStatus status, string detail) =>
        new(status, detail, null, null, null, null, null, null, null, null, null, null, null, [], status, null);
    internal static InspectionStatus MapError(int error) => error switch
    {
        5 => InspectionStatus.AccessDenied,
        50 or 120 or 127 => InspectionStatus.Unsupported,
        _ => InspectionStatus.Failed
    };
    internal static InspectionStatus MapOpenError(int error) => error is 87 or 1168 ? InspectionStatus.Exited : MapError(error);
    internal static IntegrityLevel MapIntegrity(uint rid) => rid switch
    {
        < 0x1000 => IntegrityLevel.Untrusted, < 0x2000 => IntegrityLevel.Low, < 0x2100 => IntegrityLevel.Medium,
        < 0x3000 => IntegrityLevel.MediumPlus, < 0x4000 => IntegrityLevel.High, < 0x5000 => IntegrityLevel.System, _ => IntegrityLevel.Protected
    };
    internal static string ProtectionName(uint level) => level switch
    {
        0 => "WINTCB_LIGHT", 1 => "WINDOWS", 2 => "WINDOWS_LIGHT", 3 => "ANTIMALWARE_LIGHT", 4 => "LSA_LIGHT",
        5 => "WINTCB", 6 => "CODEGEN_LIGHT", 7 => "AUTHENTICODE", 8 => "PPL_APP", 0xFFFFFFFE => "NONE", _ => $"Unknown (0x{level:X8})"
    };
    internal static string? RuntimeFamily(string name) => name.ToLowerInvariant() switch
    {
        "coreclr.dll" => ".NET", "clr.dll" => ".NET Framework 4", "mscorwks.dll" => ".NET Framework 2.0-3.5",
        "hostfxr.dll" or "hostpolicy.dll" => ".NET host", _ => null
    };
    internal static uint? ParseDword(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
    internal static TokenElevationKind? MapElevation(uint? value) => value switch
    {
        1 => TokenElevationKind.Default, 2 => TokenElevationKind.Full, 3 => TokenElevationKind.Limited, _ => null
    };
    internal static uint? ParseIntegrityRid(ReadOnlySpan<byte> bytes, ulong baseAddress, ulong pointer) =>
        TrySidBounds(bytes, baseAddress, pointer, out var offset, out var length) && length >= 12
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + length - 4, 4)) : null;

    internal static CommandLineSection ParseCommandLine(byte[] bytes, ulong baseAddress, int pointerSize)
    {
        if (pointerSize is not (4 or 8) || bytes.Length < (pointerSize == 8 ? 16 : 8))
            return EmptyCommand(InspectionStatus.Failed, "Invalid command-line structure.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (length == 0) return new(InspectionStatus.Available, "kernel reported an empty command line", "", 0);
        var maximum = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        var pointer = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if ((length & 1) != 0 || length > maximum || pointer < baseAddress || pointer - baseAddress > (ulong)bytes.Length ||
            (ulong)length > (ulong)bytes.Length - (pointer - baseAddress))
            return new(InspectionStatus.Failed, "Invalid command-line buffer bounds.", null, length / 2);
        return new(InspectionStatus.Available, "Kernel-supplied command line; may contain secrets.",
            Encoding.Unicode.GetString(bytes, (int)(pointer - baseAddress), length), length / 2);
    }

    internal readonly record struct PrivilegeValue(uint Low, int High, uint Attributes);
    internal static PrivilegeValue[] ParsePrivileges(ReadOnlySpan<byte> bytes, out bool truncated)
    {
        if (bytes.Length < 4) throw new ArgumentException("Invalid privilege buffer.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > (uint)((bytes.Length - 4) / 12)) throw new ArgumentException("Invalid privilege count.");
        truncated = count > 128;
        var result = new PrivilegeValue[Math.Min(count, 128)];
        for (var i = 0; i < result.Length; i++)
        {
            var item = bytes.Slice(4 + i * 12, 12);
            result[i] = new(BinaryPrimitives.ReadUInt32LittleEndian(item), BinaryPrimitives.ReadInt32LittleEndian(item[4..]), BinaryPrimitives.ReadUInt32LittleEndian(item[8..]));
        }
        return result;
    }

    // Windows SDK winnt.h, PROCESS_MITIGATION_POLICY.
    internal static int PolicyNumber(MitigationPolicyKind kind) => kind switch
    {
        MitigationPolicyKind.Dep => 0, MitigationPolicyKind.Aslr => 1, MitigationPolicyKind.DynamicCode => 2,
        MitigationPolicyKind.StrictHandleCheck => 3, MitigationPolicyKind.SystemCallDisable => 4,
        MitigationPolicyKind.ExtensionPointDisable => 6, MitigationPolicyKind.ControlFlowGuard => 7,
        MitigationPolicyKind.BinarySignature => 8, MitigationPolicyKind.FontDisable => 9, MitigationPolicyKind.ImageLoad => 10,
        MitigationPolicyKind.ChildProcess => 13, MitigationPolicyKind.SideChannelIsolation => 14,
        MitigationPolicyKind.UserShadowStack => 15, MitigationPolicyKind.RedirectionTrust => 16, _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    internal static IReadOnlyList<MitigationFlag> DecodeMitigation(MitigationPolicyKind kind, uint flags, bool permanent = false)
    {
        string[] names = kind switch
        {
            MitigationPolicyKind.Dep => ["Enable", "DisableAtlThunkEmulation"],
            MitigationPolicyKind.Aslr => ["EnableBottomUpRandomization", "EnableForceRelocateImages", "EnableHighEntropy", "DisallowStrippedImages"],
            MitigationPolicyKind.DynamicCode => ["ProhibitDynamicCode", "AllowThreadOptOut", "AllowRemoteDowngrade", "AuditProhibitDynamicCode"],
            MitigationPolicyKind.StrictHandleCheck => ["RaiseExceptionOnInvalidHandleReference", "HandleExceptionsPermanentlyEnabled"],
            MitigationPolicyKind.SystemCallDisable => ["DisallowWin32kSystemCalls", "AuditDisallowWin32kSystemCalls", "DisallowFsctlSystemCalls", "AuditDisallowFsctlSystemCalls"],
            MitigationPolicyKind.ExtensionPointDisable => ["DisableExtensionPoints"],
            MitigationPolicyKind.ControlFlowGuard => ["EnableControlFlowGuard", "EnableExportSuppression", "StrictMode", "EnableXfg", "EnableXfgAuditMode"],
            MitigationPolicyKind.BinarySignature => ["MicrosoftSignedOnly", "StoreSignedOnly", "MitigationOptIn", "AuditMicrosoftSignedOnly", "AuditStoreSignedOnly"],
            MitigationPolicyKind.FontDisable => ["DisableNonSystemFonts", "AuditNonSystemFontLoading"],
            MitigationPolicyKind.ImageLoad => ["NoRemoteImages", "NoLowMandatoryLabelImages", "PreferSystem32Images", "AuditNoRemoteImages", "AuditNoLowMandatoryLabelImages"],
            MitigationPolicyKind.ChildProcess => ["NoChildProcessCreation", "AuditNoChildProcessCreation", "AllowSecureProcessCreation"],
            MitigationPolicyKind.SideChannelIsolation => ["SmtBranchTargetIsolation", "IsolateSecurityDomain", "DisablePageCombine", "SpeculativeStoreBypassDisable", "RestrictCoreSharing"],
            MitigationPolicyKind.UserShadowStack => ["EnableUserShadowStack", "AuditUserShadowStack", "SetContextIpValidation", "AuditSetContextIpValidation", "EnableUserShadowStackStrictMode", "BlockNonCetBinaries", "BlockNonCetBinariesNonEhcont", "AuditBlockNonCetBinaries", "CetDynamicApisOutOfProcOnly", "SetContextIpValidationRelaxedMode"],
            MitigationPolicyKind.RedirectionTrust => ["EnforceRedirectionTrust", "AuditRedirectionTrust"], _ => []
        };
        var result = names.Select((name, bit) => new MitigationFlag(name, (flags & (1u << bit)) != 0)).ToList();
        if (kind == MitigationPolicyKind.Dep) result.Add(new("Permanent", permanent));
        return result.ToArray();
    }

    internal interface IProcessSecurityNative
    {
        SafeProcessHandle Open(int pid, uint access, out int error);
        (InspectionStatus Status, string Detail)? Validate(SafeProcessHandle process, ProcessIdentity identity);
        CommandLineSection CommandLine(SafeProcessHandle process, Action check);
        TokenSection Token(SafeProcessHandle process, Action check);
        ProtectionSection Protection(SafeProcessHandle process);
        MitigationSection Mitigations(int pid, Action check);
        JobSection Job(SafeProcessHandle process);
        (InspectionStatus Status, string Detail, string? Path) ImagePath(SafeProcessHandle process);
    }

    internal delegate int CommandLineQuery(IntPtr buffer, uint size, out uint required);
    internal static CommandLineSection ReadCommandLine(CommandLineQuery query, Action check) => ProcessSecurityNative.ReadCommandLine(query, check);

    private sealed class ProcessSecurityNative : IProcessSecurityNative
    {
        public SafeProcessHandle Open(int pid, uint access, out int error)
        {
            var handle = NativeMethods.OpenProcess(access, false, (uint)pid);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            return handle;
        }
        public (InspectionStatus Status, string Detail)? Validate(SafeProcessHandle process, ProcessIdentity identity)
        {
            var wait = NativeSecurityMethods.WaitForSingleObject(process, 0);
            if (wait == 0) return (InspectionStatus.Exited, "Process exited; all section data discarded.");
            if (wait != 258) return (InspectionStatus.Failed, "Liveness could not be verified; all section data discarded.");
            if (!NativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
                return (MapError(Marshal.GetLastWin32Error()), "Creation time could not be verified; all section data discarded.");
            return created.Ticks == (ulong)identity.CreationTimeFileTime!.Value ? null
                : (InspectionStatus.IdentityMismatch, "Creation time changed; all section data discarded.");
        }

        public CommandLineSection CommandLine(SafeProcessHandle process, Action check) => ReadCommandLine((IntPtr buffer, uint size, out uint required) =>
            NativeSecurityMethods.NtQueryInformationProcess(process, 60, buffer, size, out required), check);

        internal static CommandLineSection ReadCommandLine(CommandLineQuery query, Action check)
        {
            var size = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                check();
                var buffer = Marshal.AllocHGlobal(size);
                var bytes = new byte[size];
                try
                {
                    Marshal.Copy(bytes, 0, buffer, size);
                    var status = query(buffer, (uint)size, out var required);
                    check();
                    if (status == 0)
                    {
                        Marshal.Copy(buffer, bytes, 0, size);
                        return ParseCommandLine(bytes, unchecked((ulong)buffer.ToInt64()), IntPtr.Size);
                    }
                    if (status is unchecked((int)0xC0000004) or unchecked((int)0x80000005) or unchecked((int)0xC0000023))
                    {
                        if (required > 32768) return new(InspectionStatus.Truncated, $"Command line requires {required} bytes, exceeding the 32 KiB buffer cap; text omitted.", null, null);
                        size = (int)Math.Min(32768u, Math.Max(required, (uint)size * 2));
                        continue;
                    }
                    if (status == unchecked((int)0xC0000225))
                        return EmptyCommand(InspectionStatus.Unsupported, "kernel reports no command line for this process (e.g. System/minimal processes)");
                    return EmptyCommand(status switch
                    {
                        unchecked((int)0xC0000003) or unchecked((int)0xC0000002) => InspectionStatus.Unsupported,
                        unchecked((int)0xC0000022) => InspectionStatus.AccessDenied, _ => InspectionStatus.Failed
                    }, $"Command-line query returned NTSTATUS 0x{status:X8}; no fallback.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    Marshal.Copy(bytes, 0, buffer, size > bytes.Length ? bytes.Length : size);
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return EmptyCommand(InspectionStatus.Truncated, "Command-line query did not stabilize within three attempts; text omitted.");
        }

        public ProtectionSection Protection(SafeProcessHandle process)
        {
            if (NativeSecurityMethods.GetProcessInformation(process, 7, out var level, 4))
                return new(InspectionStatus.Available, "Documented process protection level.", level, ProtectionName(level));
            var error = Marshal.GetLastWin32Error();
            return new(error is 87 or 50 ? InspectionStatus.Unsupported : MapError(error), $"Protection query failed (Windows error {error}).", null, null);
        }
        public MitigationSection Mitigations(int pid, Action check)
        {
            using var process = Open(pid, 0x400, out var error);
            if (process.IsInvalid) return new(MapOpenError(error), $"Mitigation handle unavailable (Windows error {error}).", []);
            var rows = new List<MitigationPolicyObservation>();
            foreach (var kind in Enum.GetValues<MitigationPolicyKind>())
            {
                check();
                var bytes = new byte[kind == MitigationPolicyKind.Dep ? 8 : 4];
                if (NativeSecurityMethods.GetProcessMitigationPolicy(process, PolicyNumber(kind), bytes, (UIntPtr)bytes.Length))
                {
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    rows.Add(new(kind, InspectionStatus.Available, "Policy observed.", flags, DecodeMitigation(kind, flags, bytes.Length == 8 && bytes[4] != 0)));
                }
                else
                {
                    error = Marshal.GetLastWin32Error();
                    rows.Add(new(kind, error is 87 or 50 ? InspectionStatus.Unsupported : MapError(error), $"Policy query failed (Windows error {error}).", null, []));
                }
            }
            return new(rows.All(r => r.Status is InspectionStatus.Available or InspectionStatus.Unsupported) ? InspectionStatus.Available : InspectionStatus.Partial,
                "Policies queried independently; unsupported policies are not inferred.", rows.ToArray());
        }
        public JobSection Job(SafeProcessHandle process) => NativeSecurityMethods.IsProcessInJob(process, IntPtr.Zero, out var inJob)
            ? new(InspectionStatus.Available, "Job membership observed; no job opened.", inJob)
            : new(MapError(Marshal.GetLastWin32Error()), "Job membership query failed.", null);
        public (InspectionStatus Status, string Detail, string? Path) ImagePath(SafeProcessHandle process)
        {
            var text = new StringBuilder(32768);
            uint size = 32768;
            if (NativeMethods.QueryFullProcessImageNameW(process, 0, text, ref size)) return (InspectionStatus.Available, "Image path observed.", text.ToString());
            var error = Marshal.GetLastWin32Error();
            return (error == 122 ? InspectionStatus.Truncated : MapError(error), $"Image path query failed (Windows error {error}).", null);
        }

        public TokenSection Token(SafeProcessHandle process, Action check)
        {
            if (!NativeSecurityMethods.OpenProcessToken(process, 8, out var token))
            {
                var error = Marshal.GetLastWin32Error();
                token.Dispose();
                return EmptyToken(MapError(error), $"Token query handle unavailable (Windows error {error}).");
            }
            using (token)
            {
                var failures = new List<string>();
                byte[]? Read(int kind, string name, Action<IntPtr, int>? consume = null)
                {
                    check();
                    NativeSecurityMethods.GetTokenInformation(token, kind, IntPtr.Zero, 0, out var required);
                    if (required == 0 || required > 65536) { failures.Add($"{name}: invalid size or 64 KiB cap exceeded"); return null; }
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        check();
                        var capacity = required;
                        var memory = Marshal.AllocHGlobal((int)capacity);
                        try
                        {
                            if (NativeSecurityMethods.GetTokenInformation(token, kind, memory, capacity, out required))
                            {
                                if (required > capacity) { failures.Add($"{name}: invalid returned size"); return null; }
                                var bytes = new byte[required];
                                Marshal.Copy(memory, bytes, 0, bytes.Length);
                                consume?.Invoke(memory, bytes.Length);
                                return bytes;
                            }
                            var error = Marshal.GetLastWin32Error();
                            if (error != 122 || required > 65536 || required <= capacity)
                            { failures.Add($"{name}: Windows error {error} or buffer cap exceeded"); return null; }
                        }
                        finally { Marshal.FreeHGlobal(memory); }
                    }
                    failures.Add($"{name}: retry cap reached");
                    return null;
                }
                uint? Dword(int kind, string name)
                {
                    var bytes = Read(kind, name);
                    if (bytes is null) return null;
                    if (ParseDword(bytes) is { } value) return value;
                    failures.Add($"{name}: invalid DWORD"); return null;
                }
                bool? Boolean(int kind, string name) => Dword(kind, name) is { } value ? value != 0 : null;
                string? sid = null, account = null;
                uint? integrityRid = null;
                Read(1, "TokenUser", (memory, length) =>
                {
                    if (!TrySid(memory, length, out var pointer, out _)) { failures.Add("TokenUser: invalid SID bounds"); return; }
                    if (NativeSecurityMethods.ConvertSidToStringSidW(pointer, out var text))
                    {
                        try { sid = Marshal.PtrToStringUni(text); }
                        finally { NativeSecurityMethods.LocalFree(text); }
                    }
                    else failures.Add("TokenUser: SID string unavailable");
                    uint nameSize = 0, domainSize = 0;
                    check();
                    NativeSecurityMethods.LookupAccountSidW(null, pointer, null, ref nameSize, null, ref domainSize, out _);
                    if (nameSize is 0 or > 1024 || domainSize > 1024) { failures.Add("TokenUser: account lookup unavailable"); return; }
                    var name = new StringBuilder((int)nameSize);
                    var domain = new StringBuilder((int)Math.Max(domainSize, 1));
                    check();
                    if (NativeSecurityMethods.LookupAccountSidW(null, pointer, name, ref nameSize, domain, ref domainSize, out _))
                        account = domain.Length == 0 ? name.ToString() : $"{domain}\\{name}";
                    else failures.Add("TokenUser: account lookup unavailable");
                });
                Read(25, "Integrity", (memory, length) =>
                {
                    var bytes = new byte[length];
                    Marshal.Copy(memory, bytes, 0, length);
                    if (length >= IntPtr.Size)
                    {
                        var pointer = IntPtr.Size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                        integrityRid = ParseIntegrityRid(bytes, unchecked((ulong)memory.ToInt64()), pointer);
                    }
                    if (!integrityRid.HasValue) failures.Add("Integrity: invalid SID bounds");
                });
                var elevated = Boolean(20, "Elevation");
                var rawElevation = Dword(18, "ElevationType");
                var elevation = MapElevation(rawElevation);
                if (rawElevation.HasValue && !elevation.HasValue) failures.Add("ElevationType: unknown value");
                var allowed = Boolean(23, "VirtualizationAllowed");
                var enabled = Boolean(24, "VirtualizationEnabled");
                var appContainer = Boolean(29, "IsAppContainer");
                var session = Dword(12, "SessionId");
                var groups = Dword(2, "Groups count");
                if (groups > int.MaxValue) failures.Add("Groups count: invalid count");
                var rows = new List<TokenPrivilegeObservation>();
                var privilegeBytes = Read(3, "Privileges");
                var privilegeStatus = privilegeBytes is null ? InspectionStatus.Failed : InspectionStatus.Available;
                if (privilegeBytes is not null)
                {
                    try
                    {
                        var entries = ParsePrivileges(privilegeBytes, out var truncated);
                        if (truncated) { privilegeStatus = InspectionStatus.Truncated; failures.Add("Privileges: capped at 128 entries"); }
                        foreach (var entry in entries)
                        {
                            check();
                            var luid = new NativeSecurityMethods.Luid { Low = entry.Low, High = entry.High };
                            uint size = 0;
                            NativeSecurityMethods.LookupPrivilegeNameW(null, luid, null, ref size);
                            string? name = null;
                            if (size is > 0 and <= 1024)
                            {
                                var text = new StringBuilder((int)size + 1);
                                size++;
                                if (NativeSecurityMethods.LookupPrivilegeNameW(null, luid, text, ref size)) name = text.ToString();
                            }
                            if (name is null) { failures.Add("Privileges: name lookup unavailable"); if (privilegeStatus == InspectionStatus.Available) privilegeStatus = InspectionStatus.Partial; }
                            rows.Add(new(name ?? $"LUID {entry.High:X8}:{entry.Low:X8}", entry.Low, entry.High, entry.Attributes,
                                (entry.Attributes & 2) != 0, (entry.Attributes & 1) != 0, (entry.Attributes & 4) != 0));
                        }
                    }
                    catch (ArgumentException) { privilegeStatus = InspectionStatus.Failed; failures.Add("Privileges: invalid buffer"); }
                }
                check();
                uint packageSize = 0;
                var packageError = NativeSecurityMethods.GetPackageFamilyName(process, ref packageSize, null);
                bool? package = packageError switch { 15700 => false, 122 => true, _ => null };
                if (!package.HasValue) failures.Add($"PackageIdentity: Windows error {packageError}");
                return new(failures.Count == 0 ? InspectionStatus.Available : InspectionStatus.Partial,
                    failures.Count == 0 ? "Token queried read-only with TOKEN_QUERY." : string.Join("; ", failures.Distinct()), sid, account,
                    integrityRid.HasValue ? MapIntegrity(integrityRid.Value) : null, integrityRid, elevated, elevation, allowed, enabled,
                    appContainer, package, session, rows.ToArray(), privilegeStatus, groups <= int.MaxValue ? (int?)groups : null);
            }
        }

        private static bool TrySid(IntPtr memory, int length, out IntPtr sid, out int sidLength)
        {
            sid = IntPtr.Zero;
            sidLength = 0;
            if (length < IntPtr.Size) return false;
            var bytes = new byte[length];
            Marshal.Copy(memory, bytes, 0, length);
            var pointer = IntPtr.Size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            var address = unchecked((ulong)memory.ToInt64());
            if (!TrySidBounds(bytes, address, pointer, out var offset, out sidLength)) return false;
            sid = IntPtr.Add(memory, offset);
            return true;
        }
    }

    internal static bool TrySidBounds(ReadOnlySpan<byte> bytes, ulong baseAddress, ulong pointer, out int offset, out int length)
    {
        offset = length = 0;
        if (pointer < baseAddress || pointer - baseAddress > (ulong)bytes.Length) return false;
        offset = (int)(pointer - baseAddress);
        if (bytes.Length - offset < 8 || bytes[offset] != 1 || bytes[offset + 1] > 15) return false;
        length = 8 + bytes[offset + 1] * 4;
        return length <= bytes.Length - offset;
    }
}
