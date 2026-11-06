using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using SystemExplorer.Core;

namespace SystemExplorer.Relationships;

public sealed class WindowsPersistenceProvider : IPersistenceInventoryProvider
{
    internal const int MaximumValueBytes = 8192, MaximumSubkeys = 2048, MaximumRows = 4096, MaximumWmiRows = 1024, MaximumCharacters = 4096;
    private readonly IPersistenceBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);
    public WindowsPersistenceProvider() : this(new WindowsPersistenceBackend()) { }
    internal WindowsPersistenceProvider(IPersistenceBackend backend) => this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
    public async Task<PersistenceInventory> CaptureAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => new Capture(backend, cancellationToken).Run(), cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal sealed record FormattedRegistryValue(InspectionStatus Status, string Detail, IReadOnlyList<string?> Values);
    internal static FormattedRegistryValue FormatRegistryValue(uint type, byte[] data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (data.Length > MaximumValueBytes) return new(InspectionStatus.Truncated, "Registry value exceeds 8192-byte bound; no partial value returned.", [null]);
        if (type == 4)
        {
            if (data.Length != 4) throw new InvalidDataException("DWORD must contain four bytes.");
            return new(InspectionStatus.Available, "Observed raw DWORD.", [$"0x{System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data):X8}"]);
        }
        if (type is not (1 or 2 or 7)) return new(InspectionStatus.Unsupported, $"unsupported registry type {type}", [null]);
        if (data.Length % 2 != 0 || data.Length < (type == 7 ? 4 : 2)) throw new InvalidDataException("Invalid UTF-16 registry length.");
        string value;
        try { value = new UnicodeEncoding(false, false, true).GetString(data); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Invalid UTF-16 registry value."); }
        if (!value.EndsWith('\0') || (type == 7 && !value.EndsWith("\0\0", StringComparison.Ordinal))) throw new InvalidDataException("Unterminated registry text.");
        if (type != 7)
        {
            if (value[..^1].Contains('\0')) throw new InvalidDataException("Embedded registry text terminator.");
            return new(InspectionStatus.Available, "Observed raw unexpanded registry text.", [value[..^1]]);
        }
        // Remove the final list terminator, retaining empty items inside the list.
        var items = value == "\0\0" ? Array.Empty<string>() : value[..^2].Split('\0');
        return new(InspectionStatus.Available, "Observed raw MULTI_SZ items.", items);
    }

    private sealed record Source(PersistenceSourceKind Kind, PersistenceScope Scope, string Path, string? Value, bool Children = false, bool AllValues = false, bool Reference = false);
    private static IEnumerable<Source> RegistrySources()
    {
        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\";
        foreach (var scope in new[] { PersistenceScope.LocalMachine64, PersistenceScope.LocalMachine32, PersistenceScope.CurrentUser64 })
        {
            yield return new(PersistenceSourceKind.WinlogonShell, scope, nt + "Winlogon", "Shell");
            yield return new(PersistenceSourceKind.WinlogonUserinit, scope, nt + "Winlogon", "Userinit");
            yield return new(PersistenceSourceKind.ActiveSetupStubPath, scope, @"SOFTWARE\Microsoft\Active Setup\Installed Components", "StubPath", true);
            if (scope is PersistenceScope.CurrentUser64 or PersistenceScope.CurrentUser32) continue;
            yield return new(PersistenceSourceKind.WinlogonNotify, scope, nt + @"Winlogon\Notify", "DllName", true);
            yield return new(PersistenceSourceKind.ImageFileExecutionOptionsDebugger, scope, nt + "Image File Execution Options", "Debugger", true);
            yield return new(PersistenceSourceKind.ImageFileExecutionOptionsGlobalFlag, scope, nt + "Image File Execution Options", "GlobalFlag", true);
            yield return new(PersistenceSourceKind.SilentProcessExitMonitor, scope, nt + "SilentProcessExit", "MonitorProcess", true);
            yield return new(PersistenceSourceKind.AppInitDlls, scope, nt + "Windows", "AppInit_DLLs");
            yield return new(PersistenceSourceKind.LoadAppInitDlls, scope, nt + "Windows", "LoadAppInit_DLLs");
            yield return new(PersistenceSourceKind.NetshHelpers, scope, @"SOFTWARE\Microsoft\NetSh", null, AllValues: true);
        }
        const string control = @"SYSTEM\CurrentControlSet\Control\";
        yield return new(PersistenceSourceKind.KnownDlls, PersistenceScope.LocalMachine64, control + @"Session Manager\KnownDLLs", null, AllValues: true, Reference: true);
        yield return new(PersistenceSourceKind.LsaAuthenticationPackages, PersistenceScope.LocalMachine64, control + "Lsa", "Authentication Packages");
        yield return new(PersistenceSourceKind.LsaSecurityPackages, PersistenceScope.LocalMachine64, control + "Lsa", "Security Packages");
        yield return new(PersistenceSourceKind.LsaNotificationPackages, PersistenceScope.LocalMachine64, control + "Lsa", "Notification Packages");
        yield return new(PersistenceSourceKind.LsaSecurityPackages, PersistenceScope.LocalMachine64, control + @"Lsa\OSConfig", "Security Packages");
        yield return new(PersistenceSourceKind.PrintMonitors, PersistenceScope.LocalMachine64, control + @"Print\Monitors", "Driver", true);
    }

    private sealed class Capture(IPersistenceBackend backend, CancellationToken token)
    {
        private readonly List<PersistenceEntry> entries = [];
        private readonly List<PersistenceSourceStatus> sources = [];
        private readonly DateTimeOffset captured = DateTimeOffset.UtcNow;
        private long started = backend.GetTimestamp();
        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) throw new BudgetException();
        }
        private void Add(PersistenceEntry entry)
        {
            Check();
            if (entries.Count >= MaximumRows) throw new LimitException();
            entries.Add(entry);
        }
        internal PersistenceInventory Run()
        {
            foreach (var source in RegistrySources()) Registry(source);
            started = backend.GetTimestamp();
            foreach (var (kind, className) in new[] { (PersistenceSourceKind.WmiEventFilter, "__EventFilter"), (PersistenceSourceKind.WmiEventConsumer, "__EventConsumer"), (PersistenceSourceKind.WmiFilterToConsumerBinding, "__FilterToConsumerBinding") })
                Wmi(kind, className);
            var statuses = sources.Select(s => s.Status).Distinct().ToArray();
            var status = statuses.All(s => s == InspectionStatus.Available) ? InspectionStatus.Available :
                statuses.Contains(InspectionStatus.Available) ? InspectionStatus.Partial : statuses.Length == 1 ? statuses[0] : InspectionStatus.Partial;
            return new(captured, status, "Read-only local current-token persistence configuration, not execution or suspiciousness evidence. Raw values are unexpanded and can be sensitive. Registry and WMI each have a cooperative five-second budget; synchronous calls cannot be interrupted. No scripts read or executed.", sources.ToArray(), entries.ToArray());
        }
        private static string Location(Source source, string path) => (source.Scope is PersistenceScope.CurrentUser64 or PersistenceScope.CurrentUser32 ? "HKCU\\" : "HKLM\\") + path;
        private void Registry(Source source)
        {
            int before = entries.Count;
            var status = InspectionStatus.Available;
            string detail = "Observed raw registry configuration; no expansion.";
            try
            {
                Check();
                if (entries.Count >= MaximumRows) throw new LimitException();
                using var key = backend.OpenKey(source.Scope, source.Path, out int error);
                Check();
                if (key is null)
                {
                    if (error == 2) detail = "key not present";
                    else throw new RegistryException(error);
                }
                else if (source.Children)
                {
                    for (uint i = 0; ; i++)
                    {
                        Check();
                        error = key.EnumerateName(i, true, out string? child);
                        Check();
                        if (error == 259) break;
                        if (i >= MaximumSubkeys) throw new LimitException();
                        if (error != 0) throw new RegistryException(error);
                        if (string.IsNullOrEmpty(child) || child.Length > 256 || child.Contains('\\')) throw new InvalidDataException("Invalid registry child name.");
                        string path = source.Path + "\\" + child;
                        using var subkey = backend.OpenKey(source.Scope, path, out error);
                        Check();
                        if (subkey is null) { if (error == 2) continue; throw new RegistryException(error); }
                        ReadValue(source, subkey, path, source.Value!, ref status);
                    }
                }
                else if (source.AllValues)
                {
                    for (uint i = 0; ; i++)
                    {
                        Check();
                        error = key.EnumerateName(i, false, out string? name);
                        Check();
                        if (error == 259) break;
                        if (i >= MaximumRows) throw new LimitException();
                        if (error != 0) throw new RegistryException(error);
                        if (name is null || name.Length > 16383) throw new InvalidDataException("Invalid registry value name.");
                        ReadValue(source, key, source.Path, name, ref status);
                    }
                }
                else ReadValue(source, key, source.Path, source.Value!, ref status);
                if (status != InspectionStatus.Available) detail = "Some registry values unavailable or bounded; see row status/detail.";
            }
            catch (Exception ex) when (ReadFailure(ex)) { status = ErrorStatus(ex); detail = ErrorDetail(ex); }
            sources.Add(new(source.Kind, source.Scope, Location(source, source.Path), status, detail, entries.Count - before));
        }
        private void ReadValue(Source source, IPersistenceRegistryKey key, string path, string name, ref InspectionStatus sourceStatus)
        {
            Check();
            var raw = key.ReadValue(name);
            Check();
            if (raw.Error == 2) return;
            var properties = new Dictionary<string, string?>();
            if (source.Kind == PersistenceSourceKind.SilentProcessExitMonitor)
            {
                var mode = key.ReadValue("ReportingMode");
                Check();
                if (mode.Error == 0)
                {
                    try { var formatted = FormatRegistryValue(mode.Type, mode.Data, token); properties["ReportingMode"] = formatted.Values.FirstOrDefault(); properties["ReportingModeStatus"] = formatted.Status.ToString(); if (formatted.Status != InspectionStatus.Available) sourceStatus = InspectionStatus.Partial; }
                    catch (InvalidDataException) { properties["ReportingMode"] = null; properties["ReportingModeStatus"] = InspectionStatus.Failed.ToString(); sourceStatus = InspectionStatus.Partial; }
                }
                else { properties["ReportingMode"] = null; properties["ReportingModeStatus"] = (mode.Error == 2 ? "NotPresent" : ErrorStatus(new RegistryException(mode.Error)).ToString()); }
            }
            FormattedRegistryValue value;
            if (raw.Error == 234) value = new(InspectionStatus.Truncated, "Registry value exceeds 8192-byte bound; no partial value returned.", [null]);
            else if (raw.Error != 0) value = new(ErrorStatus(new RegistryException(raw.Error)), $"Registry value unavailable (Windows error {raw.Error}).", [null]);
            else
            {
                try { value = FormatRegistryValue(raw.Type, raw.Data, token); }
                catch (InvalidDataException) { value = new(InspectionStatus.Failed, "Invalid registry length or terminator; no value returned.", [null]); }
            }
            if (value.Status != InspectionStatus.Available) sourceStatus = value.Status == InspectionStatus.Truncated ? InspectionStatus.Truncated : InspectionStatus.Partial;
            for (int i = 0; i < value.Values.Count; i++)
                Add(new(source.Kind, Location(source, path), raw.Type == 7 ? $"{name}[{i}]" : name, value.Values[i], source.Scope,
                    value.Status, value.Detail, source.Reference, properties));
        }

        private void Wmi(PersistenceSourceKind kind, string className)
        {
            int before = entries.Count;
            var status = InspectionStatus.Available;
            string detail = "Observed local subscription metadata; script text never requested.";
            try
            {
                Check();
                if (entries.Count >= MaximumRows) throw new LimitException();
                int count = 0;
                foreach (var row in backend.QueryWmi(className, Check))
                {
                    using (row)
                    {
                        Check();
                        if (count++ >= MaximumWmiRows) throw new LimitException();
                        bool truncated = false;
                        string? Bound(string? text)
                        {
                            if (text is null || text.Length <= MaximumCharacters) return text;
                            truncated = true;
                            int length = char.IsHighSurrogate(text[MaximumCharacters - 1]) ? MaximumCharacters - 1 : MaximumCharacters;
                            return text[..length];
                        }
                        string? Read(string name) { Check(); string? value = row.Read(name); Check(); return Bound(value); }
                        var props = new Dictionary<string, string?>();
                        string? name, value = null;
                        if (kind == PersistenceSourceKind.WmiFilterToConsumerBinding) { name = Read("Filter"); value = Read("Consumer"); }
                        else
                        {
                            name = Read("Name");
                            if (kind == PersistenceSourceKind.WmiEventFilter) { value = Read("Query"); props["QueryLanguage"] = Read("QueryLanguage"); }
                            else
                            {
                                Check();
                                string? consumerClass = Bound(row.ClassName);
                                Check();
                                props["ConsumerClass"] = consumerClass;
                                if (consumerClass == "CommandLineEventConsumer") { value = Read("ExecutablePath"); props["CommandLineTemplate"] = Read("CommandLineTemplate"); }
                                else if (consumerClass == "ActiveScriptEventConsumer") { props["ScriptingEngine"] = Read("ScriptingEngine"); props["ScriptFileName"] = Read("ScriptFileName"); }
                            }
                        }
                        if (name is null) throw new InvalidDataException("Missing WMI name/reference.");
                        var rowStatus = truncated ? InspectionStatus.Truncated : InspectionStatus.Available;
                        if (truncated) status = InspectionStatus.Truncated;
                        Add(new(kind, @"root\subscription:" + className, name, value, PersistenceScope.WmiLocalNamespace, rowStatus,
                            truncated ? "WMI strings limited to 4096 characters." : "Observed local subscription metadata only; no script text.", false, props));
                    }
                }
                Check();
            }
            catch (Exception ex) when (ReadFailure(ex)) { status = ErrorStatus(ex); detail = ErrorDetail(ex); }
            sources.Add(new(kind, PersistenceScope.WmiLocalNamespace, @"root\subscription:" + className, status, detail, entries.Count - before));
        }
    }

    private sealed class BudgetException : Exception;
    private sealed class LimitException : Exception;
    private sealed class RegistryException(int error) : Exception { internal int Error { get; } = error; }
    private static bool ReadFailure(Exception ex) => ex is RegistryException or BudgetException or LimitException or COMException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or RuntimeBinderException or InvalidCastException;
    private static InspectionStatus ErrorStatus(Exception ex) => ex switch
    {
        BudgetException => InspectionStatus.TimedOut,
        LimitException => InspectionStatus.Truncated,
        RegistryException { Error: 5 } or UnauthorizedAccessException => InspectionStatus.AccessDenied,
        RegistryException { Error: 234 } => InspectionStatus.Truncated,
        COMException com when com.HResult is unchecked((int)0x80041003) or unchecked((int)0x80070005) => InspectionStatus.AccessDenied,
        COMException com when com.HResult is unchecked((int)0x8004100E) or unchecked((int)0x80040154) => InspectionStatus.Unsupported,
        NotSupportedException => InspectionStatus.Unsupported,
        _ => InspectionStatus.Failed
    };
    private static string ErrorDetail(Exception ex) => ex switch
    {
        BudgetException => "Cooperative five-second budget expired; remaining reads skipped.",
        LimitException => "Row or subkey bound reached; remaining reads omitted.",
        RegistryException registry => $"Registry observation unavailable (Windows error {registry.Error}).",
        _ => $"Observation unavailable (HRESULT 0x{ex.HResult:X8})."
    };
}

internal sealed record PersistenceRegistryValue(int Error, uint Type, byte[] Data);
internal interface IPersistenceRegistryKey : IDisposable
{
    int EnumerateName(uint index, bool subkeys, out string? name);
    PersistenceRegistryValue ReadValue(string name);
}
internal interface IPersistenceWmiObject : IDisposable
{
    string? ClassName { get; }
    string? Read(string name);
}
internal interface IPersistenceBackend
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long started);
    IPersistenceRegistryKey? OpenKey(PersistenceScope scope, string path, out int error);
    IEnumerable<IPersistenceWmiObject> QueryWmi(string className, Action check);
}

internal sealed class WindowsPersistenceBackend : IPersistenceBackend
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
    public IPersistenceRegistryKey? OpenKey(PersistenceScope scope, string path, out int error)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        bool user = scope is PersistenceScope.CurrentUser64 or PersistenceScope.CurrentUser32;
        uint view = scope is PersistenceScope.LocalMachine32 or PersistenceScope.CurrentUser32 ? 0x200u : 0x100u;
        error = NativeServiceMethods.RegOpenKeyExW(new IntPtr(unchecked((int)(user ? 0x80000001 : 0x80000002))), path, 0, 0x20019 | view, out var key);
        if (error == 0) return new RegistryKey(key);
        if (key != IntPtr.Zero) NativeServiceMethods.RegCloseKey(key);
        return null;
    }
    private sealed class RegistryKey(IntPtr handle) : IPersistenceRegistryKey
    {
        private IntPtr handle = handle;
        public int EnumerateName(uint index, bool subkeys, out string? name)
        {
            var buffer = new StringBuilder(subkeys ? 257 : 16384);
            uint length = (uint)buffer.Capacity;
            int error = subkeys ? NativeServiceMethods.RegEnumKeyExW(handle, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                : NativeServiceMethods.RegEnumValueW(handle, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            name = error == 0 ? buffer.ToString() : null;
            return error;
        }
        public PersistenceRegistryValue ReadValue(string name)
        {
            var buffer = new byte[WindowsPersistenceProvider.MaximumValueBytes];
            uint length = (uint)buffer.Length;
            int error = NativeServiceMethods.RegQueryValueExW(handle, name, IntPtr.Zero, out uint type, buffer, ref length);
            if (error != 0) return new(error, type, []);
            if (length > buffer.Length) return new(234, type, []);
            return new(0, type, buffer.AsSpan(0, (int)length).ToArray());
        }
        public void Dispose() { var owned = Interlocked.Exchange(ref handle, IntPtr.Zero); if (owned != IntPtr.Zero) NativeServiceMethods.RegCloseKey(owned); }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public IEnumerable<IPersistenceWmiObject> QueryWmi(string className, Action check)
    {
        if (className is not ("__EventFilter" or "__EventConsumer" or "__FilterToConsumerBinding")) throw new ArgumentOutOfRangeException(nameof(className));
        object? locator = null, service = null, collection = null;
        object? enumerator = null, nativeEnumerator = null;
        try
        {
            check();
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new PlatformNotSupportedException("WMI COM unavailable.");
            locator = Activator.CreateInstance(type) ?? throw new PlatformNotSupportedException("WMI COM activation failed.");
            check();
            service = ((dynamic)locator).ConnectServer(".", @"root\subscription");
            check();
            collection = ((dynamic)service).ExecQuery("SELECT * FROM " + className, "WQL", 0x30);
            check();
            enumerator = collection.GetType().InvokeMember("_NewEnum", BindingFlags.GetProperty, null, collection, null)
                ?? throw new InvalidDataException("WMI enumerator unavailable.");
            nativeEnumerator = enumerator is ICustomAdapter adapter ? adapter.GetUnderlyingObject() : enumerator;
            var iterator = (IEnumerator)enumerator;
            while (true)
            {
                check();
                if (!iterator.MoveNext()) break;
                using var row = new WmiObject(iterator.Current);
                check();
                yield return row;
            }
        }
        finally
        {
            try { if (enumerator is IDisposable disposable) disposable.Dispose(); }
            finally { Release(nativeEnumerator); Release(collection); Release(service); Release(locator); }
        }
    }
    private sealed class WmiObject(object instance) : IPersistenceWmiObject
    {
        private object? instance = instance;
        public string? ClassName
        {
            get
            {
                object? path = null;
                try { path = ((dynamic)instance!).Path_; return ((dynamic)path).Class as string; }
                finally { Release(path); }
            }
        }
        public string? Read(string name)
        {
            if (name is not ("Name" or "Query" or "QueryLanguage" or "ExecutablePath" or "CommandLineTemplate" or "ScriptingEngine" or "ScriptFileName" or "Filter" or "Consumer")) throw new ArgumentOutOfRangeException(nameof(name));
            object? properties = null, property = null;
            try
            {
                properties = ((dynamic)instance!).Properties_;
                property = ((dynamic)properties).Item(name);
                object? value = ((dynamic)property).Value;
                return value is null or DBNull ? null : value as string ?? throw new InvalidDataException("WMI property is not text.");
            }
            finally { Release(property); Release(properties); }
        }
        public void Dispose() => Release(Interlocked.Exchange(ref instance, null));
    }
}
