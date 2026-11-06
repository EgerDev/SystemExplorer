using System.Runtime.InteropServices;
using SystemExplorer.Core;
using SystemExplorer.Windows;
using static SystemExplorer.Windows.WindowsPagefileProvider;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static PagefileInformation Info(ulong total = 17, ulong used = 5, ulong peak = 29) => new()
{
    Size = (uint)Marshal.SizeOf<PagefileInformation>(),
    TotalSize = (UIntPtr)total, TotalInUse = (UIntPtr)used, PeakUsage = (UIntPtr)peak
};

static bool Emit(PagefileCallback callback, PagefileInformation? information = null, string name = "C:\\pagefile.sys")
{
    var info = Marshal.AllocHGlobal(Marshal.SizeOf<PagefileInformation>());
    var filename = Marshal.StringToHGlobalUni(name);
    try
    {
        Marshal.StructureToPtr(information ?? Info(), info, false);
        return callback(IntPtr.Zero, info, filename);
    }
    finally
    {
        Marshal.FreeHGlobal(filename);
        Marshal.FreeHGlobal(info);
    }
}

static async Task ExpectCancellation(Func<Task<PagefileInspection>> capture)
{
    try { await capture(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation rather than a published result.");
}

static async Task<PagefileInspection> Capture(FakeNative native, Func<TimeSpan>? elapsed = null) =>
    await new WindowsPagefileProvider(native, elapsed).CaptureAsync(CancellationToken.None);

Check(OperatingSystem.IsWindows(), "This smoke project requires Windows.");
Check(Marshal.SizeOf<PagefileInformation>() == 8 + 3 * IntPtr.Size, "Native pagefile structure size mismatch.");
Check(Marshal.OffsetOf<PagefileInformation>(nameof(PagefileInformation.TotalSize)).ToInt32() == 8 &&
      Marshal.OffsetOf<PagefileInformation>(nameof(PagefileInformation.TotalInUse)).ToInt32() == 8 + IntPtr.Size &&
      Marshal.OffsetOf<PagefileInformation>(nameof(PagefileInformation.PeakUsage)).ToInt32() == 8 + 2 * IntPtr.Size,
    "SIZE_T native offsets mismatch.");

foreach (uint pageSize in new uint[] { 4096, 65536 })
{
    var converted = ConvertPages("synthetic", Info(), pageSize);
    Check(converted == new PagefileObservation("synthetic", 17UL * pageSize, 5UL * pageSize, 29UL * pageSize),
        "Every page count must be multiplied exactly and peaks must not be clamped.");
    Check(ConvertPages("zero", Info(0, 0, 0), pageSize) == new PagefileObservation("zero", 0, 0, 0),
        "Actual observed zero fields must remain zero.");
}
if (IntPtr.Size == 8)
{
    foreach (var overflowing in new[] { Info(ulong.MaxValue, 0, 0), Info(0, ulong.MaxValue, 0), Info(0, 0, ulong.MaxValue) })
    {
        bool threw = false;
        try { ConvertPages("overflow", overflowing, 4096); }
        catch (OverflowException) { threw = true; }
        Check(threw, "Each field's conversion must use checked ulong arithmetic.");
        var overflow = await Capture(new(callback =>
        {
            Check(!Emit(callback, overflowing), "Overflow must stop the callback without escaping.");
            return new(false, 0);
        }));
        Check(overflow.Status == InspectionStatus.Failed && overflow.Pagefiles.Count == 0,
            "Overflow must not publish a wrapped or guessed observation.");
    }
}

var available = await Capture(new(callback =>
{
    Check(Emit(callback, name: "\\Device\\Disk\\页面.sys"), "Valid callback should continue.");
    return new(true, 0);
}) { PageSize = 65536 });
Check(available.Status == InspectionStatus.Available && available.Pagefiles.Single() ==
    new PagefileObservation("\\Device\\Disk\\页面.sys", 17UL * 65536, 5UL * 65536, 29UL * 65536),
    "Capture must preserve names and use the native page size for every field.");
var empty = await Capture(new(_ => new(true, 0)));
Check(empty.Status == InspectionStatus.Available && empty.Pagefiles.Count == 0 &&
    empty.Detail.Contains("no pagefiles were enumerated", StringComparison.OrdinalIgnoreCase), "Successful zero-entry enumeration must be explicit.");
foreach (var (error, status) in new[] { (5, InspectionStatus.AccessDenied), (50, InspectionStatus.Unsupported), (87, InspectionStatus.Failed) })
{
    var failed = await Capture(new(_ => new(false, error)));
    Check(failed.Status == status && failed.Pagefiles.Count == 0 && failed.Detail.Contains($"error {error}"),
        "Native failure must never be reported as successful empty data.");
}
var partial = await Capture(new(callback =>
{
    Check(Emit(callback), "Prefix should be accepted.");
    return new(false, 5);
}));
Check(partial.Status == InspectionStatus.Partial && partial.Pagefiles.Count == 1 && partial.Detail.Contains("incomplete prefix"),
    "Failure after a valid record must explicitly identify the partial prefix.");
var invalidSize = Info();
invalidSize.Size--;
var malformed = await Capture(new(callback =>
{
    Check(Emit(callback), "Valid prefix should continue.");
    Check(!Emit(callback, invalidSize), "Invalid layout should stop without escaping.");
    return new(true, 0);
}));
Check(malformed.Status == InspectionStatus.Partial && malformed.Pagefiles.Count == 1,
    "Native success must not mask a callback failure.");
var nullPointers = await Capture(new(callback =>
{
    Check(!callback(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "Null callback pointers must be rejected safely.");
    return new(false, 0);
}));
Check(nullPointers.Status == InspectionStatus.Failed && nullPointers.Pagefiles.Count == 0, "Null pointers cannot yield an observation.");

foreach (int count in new[] { MaximumRecords, MaximumRecords + 1 })
{
    int visited = 0;
    var bounded = await Capture(new(callback =>
    {
        for (int index = 0; index < count; index++)
        {
            visited++;
            if (!Emit(callback)) return new(false, 0);
        }
        return new(true, 0);
    }));
    Check(bounded.Pagefiles.Count == MaximumRecords && visited == count && bounded.Status ==
        (count == MaximumRecords ? InspectionStatus.Available : InspectionStatus.Truncated),
        "The cap must distinguish exactly 128 complete records from a truncated 129th.");
}
var lastTerminator = await Capture(new(callback => new(Emit(callback, name: new string('n', MaximumNameCharacters - 1)), 0)));
Check(lastTerminator.Status == InspectionStatus.Available && lastTerminator.Pagefiles.Single().Name.Length == MaximumNameCharacters - 1,
    "Terminator at the last bounded character must be accepted.");
var longName = await Capture(new(callback =>
{
    Check(Emit(callback), "Valid prefix before long name should continue.");
    Check(!Emit(callback, name: new string('n', MaximumNameCharacters)), "Name decoding must stop at its bound.");
    return new(false, 0);
}));
Check(longName.Status == InspectionStatus.Truncated && longName.Pagefiles.Count == 1, "Overlong names must not be silently truncated or published.");

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var native = new FakeNative(_ => throw new InvalidOperationException("Pre-cancelled capture reached native code."));
    await ExpectCancellation(() => new WindowsPagefileProvider(native).CaptureAsync(cancelled.Token));
    Check(native.PageSizeCalls == 0 && native.EnumerationCalls == 0, "Pre-cancellation must avoid all native calls.");
}
using (var cancelled = new CancellationTokenSource())
{
    bool nativeReturned = false;
    var native = new FakeNative(callback =>
    {
        Check(Emit(callback), "Record before cancellation should continue.");
        cancelled.Cancel();
        Check(!Emit(callback), "Cancelled callback must stop without throwing across native code.");
        nativeReturned = true;
        return new(false, 0);
    });
    await ExpectCancellation(() => new WindowsPagefileProvider(native).CaptureAsync(cancelled.Token));
    Check(nativeReturned, "OCE must be thrown only after the native enumeration returns.");
}
using (var cancelled = new CancellationTokenSource())
{
    await ExpectCancellation(() => new WindowsPagefileProvider(new FakeNative(_ =>
    {
        cancelled.Cancel();
        return new(true, 0);
    })).CaptureAsync(cancelled.Token));
}

var elapsed = TimeSpan.Zero;
var timed = await Capture(new(callback =>
{
    Check(Emit(callback), "Valid prefix before timeout should continue.");
    elapsed = TimeSpan.FromSeconds(3);
    Check(!Emit(callback), "Callback must enforce the cooperative budget.");
    return new(true, 0);
}), () => elapsed);
Check(timed.Status == InspectionStatus.TimedOut && timed.Pagefiles.Count == 1 && timed.Detail.Contains("incomplete prefix"),
    "Timeout must preserve only an explicitly partial prefix.");
elapsed = TimeSpan.Zero;
var late = await Capture(new(_ =>
{
    elapsed = TimeSpan.FromSeconds(4);
    return new(true, 0);
}), () => elapsed);
Check(late.Status == InspectionStatus.TimedOut && late.Pagefiles.Count == 0, "Late native success without callbacks is not complete within budget.");
int clockChecks = 0;
var duringName = await Capture(new(callback =>
{
    Check(!Emit(callback, name: new string('n', 1024)), "Name decoding must also cooperate with the time budget.");
    return new(true, 0);
}), () => ++clockChecks >= 4 ? TimeSpan.FromSeconds(3) : TimeSpan.Zero);
Check(duringName.Status == InspectionStatus.TimedOut && duringName.Pagefiles.Count == 0 && clockChecks == 4,
    "Timeout during bounded name decoding must omit the incomplete record.");
var beforeNative = new FakeNative(_ => throw new InvalidOperationException("Expired budget reached enumeration."));
var expired = await Capture(beforeNative, () => TimeSpan.FromSeconds(3));
Check(expired.Status == InspectionStatus.TimedOut && beforeNative.EnumerationCalls == 0, "Budget must be checked before enumeration.");

bool insideNative = false;
var callbackException = await Capture(new(callback =>
{
    insideNative = true;
    Check(!Emit(callback), "Managed callback exceptions must be contained.");
    insideNative = false;
    return new(true, 0);
}), () => insideNative ? throw new InvalidOperationException("Injected callback failure") : TimeSpan.Zero);
Check(callbackException.Status == InspectionStatus.Failed, "A callback exception must prevent an Available result.");
var noPageSizeNative = new FakeNative(_ => throw new InvalidOperationException("Missing page size reached enumeration.")) { PageSize = 0 };
var noPageSize = await Capture(noPageSizeNative);
Check(noPageSize.Status == InspectionStatus.Failed && noPageSizeNative.EnumerationCalls == 0, "Zero system page size must fail without inventing a default.");
var missingApi = await Capture(new(_ => throw new EntryPointNotFoundException()));
Check(missingApi.Status == InspectionStatus.Unsupported && missingApi.Pagefiles.Count == 0, "Missing API must be explicit.");

Console.WriteLine("PASS deterministic page conversion, failures, bounds, cancellation, budgets and callback containment.");
var started = DateTimeOffset.UtcNow;
var live = await new WindowsPagefileProvider().CaptureAsync(CancellationToken.None);
Check(live.CapturedAt >= started && live.CapturedAt <= DateTimeOffset.UtcNow, "Native capture timestamp must be from this request.");
Check(live.Status == InspectionStatus.Available, $"Native enumeration did not complete: {live.Status}: {live.Detail}");
Check(live.Pagefiles.Count <= MaximumRecords, "Native enumeration exceeded the record bound.");
foreach (var pagefile in live.Pagefiles)
{
    Check(pagefile.Name.Length is > 0 and < MaximumNameCharacters && !pagefile.Name.Contains('\0'), "Invalid native decoded name.");
    Check(pagefile.TotalBytes % (uint)Environment.SystemPageSize == 0 &&
          pagefile.InUseBytes % (uint)Environment.SystemPageSize == 0 &&
          pagefile.PeakUsageBytes % (uint)Environment.SystemPageSize == 0, "Native byte values must be page aligned.");
}
Console.WriteLine($"PASS local native EnumPageFilesW: {live.Status}, {live.Pagefiles.Count} record(s); page size {Environment.SystemPageSize}; layout {Marshal.SizeOf<PagefileInformation>()} bytes. No minimum pagefile count or peak/total ordering assumed.");

sealed class FakeNative(Func<PagefileCallback, NativeEnumeration> enumerate) : IPagefileNative
{
    internal uint PageSize { get; init; } = 4096;
    internal int PageSizeCalls { get; private set; }
    internal int EnumerationCalls { get; private set; }
    public uint GetPageSize() { PageSizeCalls++; return PageSize; }
    public NativeEnumeration Enumerate(PagefileCallback callback) { EnumerationCalls++; return enumerate(callback); }
}
