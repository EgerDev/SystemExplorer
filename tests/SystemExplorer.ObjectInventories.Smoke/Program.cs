using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;
using SystemExplorer.Windows;

var passed = 0;
var failed = 0;
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task Section(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error}"); }
}
void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
var identity = new ProcessIdentity(123, 456);
byte[] Handles()
{
    var b = new byte[136];
    W64(b, 0, 3);
    for (var i = 0; i < 3; i++)
    {
        var o = 16 + 40 * i;
        W64(b, o, 0xdeadbeef);
        W64(b, o + 8, i == 2 ? 999UL : 123UL);
        W64(b, o + 16, (ulong)(100 + i));
        W32(b, o + 24, 0x12345678);
        b[o + 30] = (byte)(i + 2);
        W32(b, o + 32, 0xabcdef);
    }
    return b;
}
await Section("handle x64 layout, totals, caps, malformed and empty", () =>
{
    var b = Handles();
    var r = WindowsHandleInventoryProvider.Parse(b, identity, new Dictionary<int, string> { [2] = "File" }, 1);
    Assert(r.Status == InspectionStatus.Truncated && r.Handles.Count == 1, "row cap");
    Assert(r.ProcessHandleCount == 2 && r.SystemHandleCount == 3 && r.ProcessCountsByType.Sum(x => x.HandleCount) == 2 && r.SystemCountsByType.Sum(x => x.HandleCount) == 3, "complete counts");
    Assert(r.Handles[0] == new HandleObservation(100, 2, "File", 0x12345678, 0xabcdef), "entry offsets");
    var full = WindowsHandleInventoryProvider.Parse(b, identity, new Dictionary<int, string>());
    Assert(full.Status == InspectionStatus.Available && full.Handles.All(x => x.TypeName == null), "unknown names");
    Assert(WindowsHandleInventoryProvider.Parse(b.AsSpan(0, 135), identity, new Dictionary<int, string>()).Status == InspectionStatus.Failed, "returned length");
    W64(b, 0, ulong.MaxValue);
    Assert(WindowsHandleInventoryProvider.Parse(b, identity, new Dictionary<int, string>()).Status == InspectionStatus.Failed, "count overflow");
    Assert(WindowsHandleInventoryProvider.Parse(new byte[16], identity, new Dictionary<int, string>()).ProcessHandleCount == 0, "empty");
    Assert(WindowsHandleInventoryProvider.Parse([], identity, new Dictionary<int, string>()).Status == InspectionStatus.Failed, "short header");
    Assert(WindowsHandleInventoryProvider.Parse(Handles(), identity, new Dictionary<int, string>(), expired: () => true).Status == InspectionStatus.TimedOut, "parser budget");
    return Task.CompletedTask;
});
await Section("pool x64 offsets, byte units, escaping, caps and malformed", () =>
{
    var b = new byte[88]; W32(b, 0, 2);
    b[8] = 65; b[9] = 0; b[10] = 255; b[11] = 32;
    W32(b, 12, 123); W32(b, 16, 456); W64(b, 24, 0x123456789UL);
    W32(b, 32, 789); W32(b, 36, 987); W64(b, 40, 0x987654321UL);
    var r = WindowsPoolTagProvider.Parse(b, 1);
    Assert(r.Status == InspectionStatus.Truncated && r.Tags.Count == 1, "pool cap");
    Assert(r.Tags[0] == new PoolTagObservation("A\\x00\\xFF ", 0x20ff0041, 123, 456, 0x123456789UL, 789, 987, 0x987654321UL), "pool layout and byte units");
    Assert(WindowsPoolTagProvider.Parse(b.AsSpan(0, 87)).Status == InspectionStatus.Failed, "pool returned length");
    Assert(WindowsPoolTagProvider.Parse(new byte[8]).Status == InspectionStatus.Available, "empty pool");
    Assert(WindowsPoolTagProvider.Parse([]).Status == InspectionStatus.Failed, "pool short header");
    Assert(WindowsPoolTagProvider.Parse(b, expired: () => true).Status == InspectionStatus.TimedOut, "pool parser budget");
    return Task.CompletedTask;
});
await Section("object types alignment, TypeIndex and pointer bounds", () =>
{
    var b = new byte[248]; W32(b, 0, 2);
    for (var i = 0; i < 2; i++)
    {
        var o = 8 + 120 * i;
        b[o] = 8; b[o + 2] = 16; W64(b, o + 8, (ulong)(0x1000 + o + 104)); b[o + 90] = (byte)(20 + i);
        Encoding.Unicode.GetBytes("File").CopyTo(b, o + 104);
    }
    var names = NativeSystemInformation.ParseTypeNames(b, 0x1000);
    Assert(names[20] == "File" && names[21] == "File", "aligned type names");
    W64(b, 16, 0xffff);
    var rejected = false;
    try { NativeSystemInformation.ParseTypeNames(b, 0x1000); } catch (FormatException) { rejected = true; }
    Assert(rejected, "out-of-range name pointer");
    Assert(NativeSystemInformation.ParseTypeNames(new byte[8], 0).Count == 0, "empty types");
    return Task.CompletedTask;
});
await Section("bounded growth, returned length, native refusal and cancellation", () =>
{
    var api = new FakeQuery(unchecked((int)0xC0000004));
    var r = NativeSystemInformation.QueryBounded(api, 64, false, default, () => false, 1024 * 1024, 2);
    Assert(r.Status == InspectionStatus.Truncated && api.Calls == 2, "attempt cap");
    api = new FakeQuery(unchecked((int)0xC0000004));
    Assert(NativeSystemInformation.QueryBounded(api, 64, false, default, () => false, 65536).Status == InspectionStatus.Truncated && api.Calls == 1, "byte cap");
    Assert(NativeSystemInformation.QueryBounded(new FakeQuery(unchecked((int)0xC0000022)), 22, false, default, () => false, 65536).Status == InspectionStatus.AccessDenied, "denied");
    foreach (var code in new uint[] { 0xC0000002, 0xC0000003, 0xC00000BB }) Assert(NativeSystemInformation.MapStatus(unchecked((int)code)) == InspectionStatus.Unsupported, "unsupported");
    Assert(NativeSystemInformation.QueryBounded(new FakeQuery(0, 65537), 22, false, default, () => false, 65536).Status == InspectionStatus.Failed, "invalid returned length");
    Assert(NativeSystemInformation.QueryBounded(new FakeQuery(0), 22, false, default, () => true, 65536).Status == InspectionStatus.TimedOut, "budget");
    var canceled = false;
    try { NativeSystemInformation.QueryBounded(api, 22, false, new CancellationToken(true), () => false, 65536); } catch (OperationCanceledException) { canceled = true; }
    Assert(canceled, "cancellation");
    var growing = new GrowingQuery();
    var copied = NativeSystemInformation.QueryBounded(growing, 0, true, default, () => false, 1024 * 1024);
    Assert(copied.Status == InspectionStatus.Available && copied.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }) && growing.Calls == 2 && copied.BaseAddress != 0, "successful growth and returned-length copy");
    return Task.CompletedTask;
});
await Section("handle identity before/after, unstable, denied and successful fake", async () =>
{
    foreach (var status in new[] { InspectionStatus.IdentityMismatch, InspectionStatus.Exited })
    {
        foreach (var after in new[] { false, true })
        {
            var fake = new FakeInventory(Handles(), after ? InspectionStatus.Available : status, status);
            var r = await new WindowsHandleInventoryProvider(fake).CaptureAsync(identity, default);
            Assert(r.Status == status && r.Handles.Count == 0 && r.ProcessHandleCount == null, "identity discard");
            Assert(fake.Queries == (after ? 2 : 0), "validation dispatch");
        }
    }
    var good = new FakeInventory(Handles(), InspectionStatus.Available, InspectionStatus.Available);
    Assert((await new WindowsHandleInventoryProvider(good).CaptureAsync(identity, default)).Status == InspectionStatus.Available, "fake success");
    Assert((await new WindowsHandleInventoryProvider(good).CaptureAsync(new(123, null), default)).Status == InspectionStatus.IdentityMismatch, "unstable");
    var denied = new FakeInventory([], InspectionStatus.Available, InspectionStatus.Available) { QueryStatus = InspectionStatus.AccessDenied };
    Assert((await new WindowsHandleInventoryProvider(denied).CaptureAsync(identity, default)).Status == InspectionStatus.AccessDenied, "snapshot denial");
});
await Section("live own handles and wrong creation identity", async () =>
{
    using var process = Process.GetCurrentProcess();
    var own = new ProcessIdentity(process.Id, process.StartTime.ToFileTimeUtc());
    var provider = new WindowsHandleInventoryProvider();
    var r = await provider.CaptureAsync(own, default);
    Console.WriteLine($"  handles: {r.Status}; process={r.ProcessHandleCount}; system={r.SystemHandleCount}; rows={r.Handles.Count}; {r.Detail}");
    Assert(r.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.AccessDenied or InspectionStatus.Unsupported or InspectionStatus.TimedOut, "honest live handle status");
    if (r.ProcessHandleCount.HasValue) Assert(r.ProcessHandleCount == r.ProcessCountsByType.Sum(x => x.HandleCount) && r.SystemHandleCount == r.SystemCountsByType.Sum(x => x.HandleCount) && r.Handles.Count <= WindowsHandleInventoryProvider.MaximumRows, "live counts");
    var wrong = await provider.CaptureAsync(own with { CreationTimeFileTime = own.CreationTimeFileTime + 1 }, default);
    Assert(wrong.Status == InspectionStatus.IdentityMismatch && wrong.Handles.Count == 0, "live wrong identity");
});
await Section("live pool tags", async () =>
{
    var r = await new WindowsPoolTagProvider().CaptureAsync(default);
    Console.WriteLine($"  pool: {r.Status}; rows={r.Tags.Count}; {r.Detail}");
    Assert(r.Status is InspectionStatus.Available or InspectionStatus.Truncated or InspectionStatus.AccessDenied or InspectionStatus.Unsupported or InspectionStatus.TimedOut, "honest pool status");
    if (r.Status == InspectionStatus.Available) Assert(r.Tags.All(x => x.Tag.Length >= 4) && r.Tags.Count <= WindowsPoolTagProvider.MaximumRows, "sane tags");
});
await ThreadActivitySmoke.Run(Section);
await FileUsageSmoke.Run(Section);
Console.WriteLine($"FINAL PASS={passed} FAIL={failed}");
return failed == 0 ? 0 : 1;

sealed class FakeQuery(int status, int length = 0) : ISystemInformationApi
{
    internal int Calls;
    public int Query(int informationClass, bool objectTypes, IntPtr buffer, int size, out int returned)
    { Calls++; returned = length; return status; }
}
sealed class GrowingQuery : ISystemInformationApi
{
    internal int Calls;
    public int Query(int informationClass, bool objectTypes, IntPtr buffer, int size, out int returned)
    {
        if (Calls++ == 0) { returned = 100000; return unchecked((int)0xC0000004); }
        returned = 4;
        Marshal.Copy(new byte[] { 1, 2, 3, 4 }, 0, buffer, 4);
        return 0;
    }
}
sealed class FakeInventory(byte[] handles, InspectionStatus before, InspectionStatus after) : IInventoryNative
{
    internal int Queries;
    internal InspectionStatus QueryStatus = InspectionStatus.Available;
    public bool IsSupported => true;
    public NativeInformation Query(int informationClass, bool objectTypes, CancellationToken token, Func<bool> expired)
    { Queries++; return new(QueryStatus, "fake", objectTypes ? new byte[8] : handles); }
    public IInventoryProcessLease Open(ProcessIdentity identity) => new Lease(before, after);
    private sealed class Lease(InspectionStatus before, InspectionStatus after) : IInventoryProcessLease
    {
        private int calls;
        public InspectionStatus Validate(ProcessIdentity identity) => calls++ == 0 ? before : after;
        public void Dispose() { }
    }
}
