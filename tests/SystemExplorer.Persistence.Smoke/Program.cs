using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;
using SystemExplorer.Relationships;

int checks = 0;
void Require(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
    checks++;
}
void Invalid(Action action)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException("Malformed input accepted.");
}
async Task Canceled(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException("Cancellation ignored.");
}
byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);
var rawText = WindowsPersistenceProvider.FormatRegistryValue(2, Utf16("%FICTIONAL_VAR%\\image.dll\0"));
Require(rawText.Status == InspectionStatus.Available && rawText.Values.Single() == "%FICTIONAL_VAR%\\image.dll", "EXPAND_SZ expanded or changed.");
Require(WindowsPersistenceProvider.FormatRegistryValue(1, Utf16("工具\0")).Values.Single() == "工具", "Unicode SZ changed.");
Require(WindowsPersistenceProvider.FormatRegistryValue(1, Utf16("\0")).Values.Single() == "", "Empty SZ changed.");
Invalid(() => WindowsPersistenceProvider.FormatRegistryValue(1, Utf16("unterminated")));
Invalid(() => WindowsPersistenceProvider.FormatRegistryValue(1, [1, 0, 0]));
Invalid(() => WindowsPersistenceProvider.FormatRegistryValue(7, Utf16("a\0")));
Invalid(() => WindowsPersistenceProvider.FormatRegistryValue(4, [1, 2, 3]));
var multi = WindowsPersistenceProvider.FormatRegistryValue(7, Utf16("first\0\0last\0\0"));
Require(multi.Values.SequenceEqual(new[] { "first", "", "last" }), "MULTI_SZ items/empty item changed.");
Require(WindowsPersistenceProvider.FormatRegistryValue(7, Utf16("\0\0")).Values.Count == 0, "Empty MULTI_SZ invented item.");
Require(WindowsPersistenceProvider.FormatRegistryValue(4, [0x78, 0x56, 0x34, 0x12]).Values.Single() == "0x12345678", "DWORD formatting wrong.");
Require(WindowsPersistenceProvider.FormatRegistryValue(99, []).Status == InspectionStatus.Unsupported && WindowsPersistenceProvider.FormatRegistryValue(99, []).Values.Single() is null, "Unsupported type guessed.");
Require(WindowsPersistenceProvider.FormatRegistryValue(1, new byte[8194]).Status == InspectionStatus.Truncated, "Value byte bound ignored.");

var missing = await new WindowsPersistenceProvider(new FakePersistenceBackend()).CaptureAsync(default);
Require(missing.Status == InspectionStatus.Available && missing.Entries.Count == 0 && missing.Sources.Where(s => s.Scope != PersistenceScope.WmiLocalNamespace).All(s => s.Detail == "key not present"), "Missing registry keys not available empty observations.");
Require(missing.Sources.Count(s => s.Source == PersistenceSourceKind.WinlogonShell) == 3 && !missing.Sources.Any(s => s.Scope == PersistenceScope.CurrentUser32), "Shared HKCU keys duplicated across views.");
var deniedBackend = new FakePersistenceBackend { Open = (scope, path) => path.EndsWith("NetSh", StringComparison.Ordinal) && scope == PersistenceScope.LocalMachine32 ? (5, null) : (2, null) };
var denied = await new WindowsPersistenceProvider(deniedBackend).CaptureAsync(default);
Require(denied.Status == InspectionStatus.Partial && denied.Sources.Count(s => s.Status == InspectionStatus.AccessDenied) == 1 && denied.Sources.All(s => s.Status is InspectionStatus.Available or InspectionStatus.AccessDenied), "Access denial escaped its source.");
var registryBackend = new FakePersistenceBackend
{
    Open = (scope, path) => scope == PersistenceScope.LocalMachine64 && path.EndsWith(@"Session Manager\KnownDLLs", StringComparison.Ordinal)
        ? (0, new FakeKey { Values = new() { ["raw"] = new(0, 2, Utf16("%UNCHANGED%\0")), ["packages"] = new(0, 7, Utf16("one\0two\0\0")) } }) : (2, null)
};
var registry = await new WindowsPersistenceProvider(registryBackend).CaptureAsync(default);
Require(registry.Entries.Count == 3 && registry.Entries.All(e => e.IsReference) && registry.Entries.Any(e => e.Name == "packages[1]" && e.Value == "two") && registry.Entries.Any(e => e.Value == "%UNCHANGED%"), "Registry mapping wrong.");
Require(registryBackend.Disposals == 1, "Registry key not disposed.");
var subkeysBackend = new FakePersistenceBackend
{
    Open = (scope, path) => scope == PersistenceScope.LocalMachine64 && path.EndsWith("Image File Execution Options", StringComparison.Ordinal)
        ? (0, new FakeKey { SubkeyCount = 2049 }) : (2, null)
};
var subkeys = await new WindowsPersistenceProvider(subkeysBackend).CaptureAsync(default);
Require(subkeys.Sources.Count(s => s.Status == InspectionStatus.Truncated) == 2 && subkeysBackend.ChildOpens == 4096, "Subkey cap ignored.");
var rowCapBackend = new FakePersistenceBackend
{
    Open = (scope, path) => scope == PersistenceScope.LocalMachine64 && path.EndsWith(@"Session Manager\KnownDLLs", StringComparison.Ordinal)
        ? (0, new FakeKey { ValueCount = 4097, EveryValue = new(0, 1, Utf16("raw\0")) }) : (2, null)
};
var rowCap = await new WindowsPersistenceProvider(rowCapBackend).CaptureAsync(default);
Require(rowCap.Entries.Count == 4096 && rowCap.Sources.Any(s => s.Source == PersistenceSourceKind.KnownDlls && s.Status == InspectionStatus.Truncated), "Total row cap ignored.");

var wmiBackend = new FakePersistenceBackend
{
    Wmi = className => className switch
    {
        "__EventFilter" => [new FakeWmi("__EventFilter", new() { ["Name"] = "filter", ["Query"] = "SELECT * FROM Fictional", ["QueryLanguage"] = "WQL" })],
        "__EventConsumer" => [new FakeWmi("CommandLineEventConsumer", new() { ["Name"] = "command", ["ExecutablePath"] = "%RAW%\\app.exe", ["CommandLineTemplate"] = "%RAW% --fictional" }),
            new FakeWmi("ActiveScriptEventConsumer", new() { ["Name"] = "script", ["ScriptingEngine"] = "VBScript", ["ScriptFileName"] = "%RAW%\\script.vbs" }),
            new FakeWmi("OtherConsumer", new() { ["Name"] = "other" })],
        _ => [new FakeWmi("__FilterToConsumerBinding", new() { ["Filter"] = "filter-ref", ["Consumer"] = "consumer-ref" })]
    }
};
var wmi = await new WindowsPersistenceProvider(wmiBackend).CaptureAsync(default);
Require(wmi.Status == InspectionStatus.Available && wmi.Entries.Count == 5 && wmi.Entries.Single(e => e.Name == "filter").Properties["QueryLanguage"] == "WQL", "WMI filter mapping wrong.");
Require(wmi.Entries.Single(e => e.Name == "command").Value == "%RAW%\\app.exe" && wmi.Entries.Single(e => e.Name == "script").Value is null &&
    wmi.Entries.Single(e => e.Name == "script").Properties["ScriptingEngine"] == "VBScript" && wmi.Entries.Any(e => e.Name == "filter-ref" && e.Value == "consumer-ref"), "WMI consumer/binding mapping wrong.");
Require(wmiBackend.WmiDisposals == 5 && wmi.Entries.All(e => !e.Properties.ContainsKey("ScriptText")), "WMI resource/script-text contract broken.");
var wmiCap = await new WindowsPersistenceProvider(new FakePersistenceBackend
{
    Wmi = _ => Enumerable.Range(0, 1025).Select(i => new FakeWmi("OtherConsumer", new() { ["Name"] = "name", ["Filter"] = "filter", ["Consumer"] = "consumer" }))
}).CaptureAsync(default);
Require(wmiCap.Sources.Where(s => s.Scope == PersistenceScope.WmiLocalNamespace).All(s => s.Status == InspectionStatus.Truncated && s.RowCount == 1024), "WMI class row cap ignored.");
foreach (var (hr, status) in new[] { (unchecked((int)0x80041003), InspectionStatus.AccessDenied), (unchecked((int)0x80070005), InspectionStatus.AccessDenied),
    (unchecked((int)0x8004100E), InspectionStatus.Unsupported), (unchecked((int)0x80004005), InspectionStatus.Failed) })
{
    var result = await new WindowsPersistenceProvider(new FakePersistenceBackend { Wmi = _ => throw new COMException("fictional", hr) }).CaptureAsync(default);
    Require(result.Sources.Where(s => s.Scope == PersistenceScope.WmiLocalNamespace).All(s => s.Status == status && s.Detail.Contains($"0x{hr:X8}")), "WMI error classification wrong.");
}
var timeoutBackend = new FakePersistenceBackend();
timeoutBackend.Wmi = _ => { timeoutBackend.Elapsed = TimeSpan.FromSeconds(5); return [new FakeWmi("OtherConsumer", new() { ["Name"] = "name" })]; };
var timeout = await new WindowsPersistenceProvider(timeoutBackend).CaptureAsync(default);
Require(timeout.Sources.Where(s => s.Scope == PersistenceScope.WmiLocalNamespace).All(s => s.Status == InspectionStatus.TimedOut) && timeout.Entries.Count == 0 && timeoutBackend.WmiDisposals == 1, "WMI timeout/cleanup wrong.");
var longWmi = await new WindowsPersistenceProvider(new FakePersistenceBackend { Wmi = c => c == "__EventFilter" ? [new FakeWmi(c, new() { ["Name"] = "filter", ["Query"] = new string('q', 4097) })] : [] }).CaptureAsync(default);
Require(longWmi.Entries.Single().Status == InspectionStatus.Truncated && longWmi.Entries.Single().Value?.Length == 4096, "WMI string cap ignored.");

using (var buffer = new NativeBuffer(256 * 1024))
{
    int stride = Marshal.SizeOf<DriverServiceEntry>();
    IntPtr name = IntPtr.Add(buffer.Pointer, 200000);
    byte[] bytes = Utf16("fictional\0");
    Marshal.Copy(bytes, 0, name, bytes.Length);
    for (int i = 0; i < 2049; i++) Marshal.StructureToPtr(new DriverServiceEntry { Name = name, Type = 1, State = 4 }, IntPtr.Add(buffer.Pointer, i * stride), false);
    var drivers = WindowsDriverServiceProvider.Decode(buffer.Pointer, buffer.Capacity, 2049, default);
    Require(drivers.Status == InspectionStatus.Truncated && drivers.Drivers.Count == 2048, "Driver row cap ignored.");
    Require(WindowsDriverServiceProvider.Decode(IntPtr.Zero, 0, 0, default).Drivers.Count == 0, "Empty driver buffer rejected.");
    Marshal.StructureToPtr(new DriverServiceEntry { Name = IntPtr.Add(buffer.Pointer, buffer.Capacity) }, buffer.Pointer, false);
    Invalid(() => WindowsDriverServiceProvider.Decode(buffer.Pointer, buffer.Capacity, 1, default));
    Invalid(() => WindowsDriverServiceProvider.Decode(buffer.Pointer, 1, 1, default));
}
var driverBackend = new FakeDriverBackend();
var driverInventory = await new WindowsDriverServiceProvider(driverBackend).CaptureAsync(default);
Require(driverInventory.Status == InspectionStatus.Partial && driverInventory.Drivers.Single(d => d.Name == "denied").ConfigStatus == InspectionStatus.AccessDenied &&
    driverInventory.Drivers.Single(d => d.Name == "denied").ImagePath is null, "Driver per-row failure lost.");
Require(driverInventory.Drivers.Single(d => d.Name == "good").StartType == 3 && driverInventory.Drivers.Single(d => d.Name == "good").ImagePath == "%RAW%\\driver.sys" && driverBackend.Disposals == 2, "Driver config mapping/cleanup wrong.");
var timedDriver = new FakeDriverBackend { ExpireAfterEnumeration = true };
var timedInventory = await new WindowsDriverServiceProvider(timedDriver).CaptureAsync(default);
Require(timedInventory.Drivers.All(d => d.ConfigStatus == InspectionStatus.TimedOut && d.StartType is null) && timedDriver.ConfigOpens == 0, "Driver timeout invented configuration.");
using (var source = new CancellationTokenSource())
{
    source.Cancel();
    await Canceled(() => new WindowsPersistenceProvider(new FakePersistenceBackend()).CaptureAsync(source.Token));
    await Canceled(() => new WindowsDriverServiceProvider(new FakeDriverBackend()).CaptureAsync(source.Token));
}

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live smoke requires Windows.");
bool Allowed(InspectionStatus status) => status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.TimedOut or InspectionStatus.AccessDenied or InspectionStatus.Unsupported or InspectionStatus.Failed or InspectionStatus.Exited;
var livePersistence = await new WindowsPersistenceProvider().CaptureAsync(default);
Require(livePersistence.Sources.Count > 0 && livePersistence.Sources.All(s => Allowed(s.Status)), "Invalid live persistence source states.");
Require(livePersistence.Entries.All(e => Allowed(e.Status) && !e.Properties.ContainsKey("ScriptText")), "Live persistence exposed ScriptText or invalid status.");
var liveDrivers = await new WindowsDriverServiceProvider().CaptureAsync(default);
Require(Allowed(liveDrivers.Status) && (liveDrivers.Status != InspectionStatus.Available || liveDrivers.Drivers.Count > 0), "Available live driver inventory empty.");
Require(liveDrivers.Drivers.All(d => d.CurrentState is >= 1 and <= 7 && Allowed(d.ConfigStatus)), "Invalid live driver states.");
Require(liveDrivers.Drivers.All(d => d.ConfigStatus != InspectionStatus.Available || d.StartType is not null && d.ErrorControl is not null), "Available driver configuration lacks values.");
Console.WriteLine($"Persistence sources: {string.Join(", ", livePersistence.Sources.GroupBy(s => s.Status).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))}; rows={livePersistence.Entries.Count}.");
Console.WriteLine($"Driver configuration: {string.Join(", ", liveDrivers.Drivers.GroupBy(d => d.ConfigStatus).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))}; inventory={liveDrivers.Status}.");
Console.WriteLine($"PASS: {checks} assertions. No sensitive metadata values logged; no service, registry or WMI changes made.");

sealed class NativeBuffer : IDisposable
{
    public IntPtr Pointer { get; }
    public int Capacity { get; }
    public NativeBuffer(int capacity) { Capacity = capacity; Pointer = Marshal.AllocHGlobal(capacity); Marshal.Copy(Enumerable.Repeat((byte)0xff, capacity).ToArray(), 0, Pointer, capacity); }
    public void Dispose() => Marshal.FreeHGlobal(Pointer);
}
sealed class FakeKey : IPersistenceRegistryKey
{
    public Dictionary<string, PersistenceRegistryValue> Values = new();
    public int SubkeyCount, ValueCount;
    public PersistenceRegistryValue? EveryValue;
    public Action? OnDispose;
    public int EnumerateName(uint index, bool subkeys, out string? name)
    {
        name = subkeys ? index < SubkeyCount ? $"child{index}" : null : EveryValue is not null ? index < ValueCount ? $"value{index}" : null : Values.Keys.ElementAtOrDefault((int)index);
        return name is null ? 259 : 0;
    }
    public PersistenceRegistryValue ReadValue(string name) => EveryValue ?? (Values.TryGetValue(name, out var value) ? value : new(2, 0, []));
    public void Dispose() => Interlocked.Exchange(ref OnDispose, null)?.Invoke();
}
sealed class FakeWmi(string className, Dictionary<string, string?> values) : IPersistenceWmiObject
{
    public Action? OnDispose;
    public string? ClassName => className;
    public string? Read(string name)
    {
        if (name == "ScriptText") throw new InvalidOperationException("ScriptText must never be requested.");
        return values.GetValueOrDefault(name);
    }
    public void Dispose() => Interlocked.Exchange(ref OnDispose, null)?.Invoke();
}
sealed class FakePersistenceBackend : IPersistenceBackend
{
    public Func<PersistenceScope, string, (int Error, FakeKey? Key)> Open = (_, _) => (2, null);
    public Func<string, IEnumerable<FakeWmi>> Wmi = _ => [];
    public int Disposals, ChildOpens, WmiDisposals;
    public TimeSpan Elapsed;
    public long GetTimestamp() => 0;
    public TimeSpan GetElapsedTime(long started) => Elapsed;
    public IPersistenceRegistryKey? OpenKey(PersistenceScope scope, string path, out int error)
    {
        if (path.Contains("\\child", StringComparison.Ordinal)) ChildOpens++;
        var result = Open(scope, path);
        error = result.Error;
        if (result.Key is { } key) key.OnDispose = () => Disposals++;
        return result.Key;
    }
    public IEnumerable<IPersistenceWmiObject> QueryWmi(string className, Action check)
    {
        foreach (var row in Wmi(className))
        {
            row.OnDispose = () => WmiDisposals++;
            yield return row;
        }
    }
}
sealed class FakeDriverBackend : IDriverServiceBackend
{
    public int Disposals, ConfigOpens;
    public bool ExpireAfterEnumeration;
    private bool enumerated;
    public long GetTimestamp() => 0;
    public TimeSpan GetElapsedTime(long started) => ExpireAfterEnumeration && enumerated ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
    public IDriverServiceManager? OpenManager(out int error) { error = 0; return new Manager(this); }
    private sealed class Manager(FakeDriverBackend owner) : IDriverServiceManager
    {
        public int Enumerate(IntPtr buffer, uint capacity, out uint needed, out uint count, ref uint resume)
        {
            needed = 0; count = 2;
            int stride = Marshal.SizeOf<DriverServiceEntry>();
            for (int i = 0; i < 2; i++)
            {
                byte[] bytes = Encoding.Unicode.GetBytes((i == 0 ? "denied" : "good") + "\0");
                IntPtr name = IntPtr.Add(buffer, 512 + i * 32);
                Marshal.Copy(bytes, 0, name, bytes.Length);
                Marshal.StructureToPtr(new DriverServiceEntry { Name = name, Type = 1, State = 4 }, IntPtr.Add(buffer, i * stride), false);
            }
            owner.enumerated = true;
            return 0;
        }
        public IServiceInspectionQuery? OpenConfig(string name, out int error)
        {
            owner.ConfigOpens++;
            error = name == "denied" ? 5 : 0;
            return error == 0 ? new Config(owner) : null;
        }
        public void Dispose() => owner.Disposals++;
    }
    private sealed class Config(FakeDriverBackend owner) : IServiceInspectionQuery
    {
        public int Query(IntPtr buffer, uint capacity, out uint needed)
        {
            needed = 256;
            if (capacity == 0) return 122;
            IntPtr text = IntPtr.Add(buffer, Marshal.SizeOf<ServiceInspectionConfig>());
            byte[] bytes = Encoding.Unicode.GetBytes("%RAW%\\driver.sys\0");
            Marshal.Copy(bytes, 0, text, bytes.Length);
            Marshal.StructureToPtr(new ServiceInspectionConfig { ServiceType = 1, StartType = 3, ErrorControl = 1, BinaryPath = text }, buffer, false);
            return 0;
        }
        public void Dispose() => owner.Disposals++;
    }
}
