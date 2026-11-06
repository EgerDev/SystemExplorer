using System.Diagnostics;
using System.Text;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsPoolTagProvider : IPoolTagProvider
{
    internal const int MaximumRows = 4096;
    private readonly IInventoryNative native;
    public WindowsPoolTagProvider() : this(new NativeSystemInformation()) { }
    internal WindowsPoolTagProvider(IInventoryNative native) => this.native = native;
    public Task<PoolTagInventory> CaptureAsync(CancellationToken cancellationToken) => Task.Run(() => Capture(cancellationToken), cancellationToken);

    private PoolTagInventory Capture(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        bool Expired() => clock.Elapsed >= TimeSpan.FromSeconds(3);
        if (!native.IsSupported) return new(captured, InspectionStatus.Unsupported, "Only Windows x64 layouts are supported.", []);
        try
        {
            var snapshot = native.Query(22, false, token, Expired);
            if (snapshot.Status != InspectionStatus.Available) return new(captured, snapshot.Status, snapshot.Detail, []);
            return Parse(snapshot.Bytes, MaximumRows, token, Expired) with { CapturedAt = captured };
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return new(captured, InspectionStatus.Unsupported, "Required native API unavailable; no fallback attempted.", []); }
    }

    internal static PoolTagInventory Parse(ReadOnlySpan<byte> bytes, int cap = MaximumRows, CancellationToken token = default, Func<bool>? expired = null)
    {
        try
        {
            NativeSystemInformation.RequireEntries(bytes.Length, 8, 0, 40);
            var count = NativeSystemInformation.U32(bytes, 0);
            NativeSystemInformation.RequireEntries(bytes.Length, 8, count, 40);
            var rows = new List<PoolTagObservation>();
            for (var i = 0; i < Math.Min((long)count, cap); i++)
            {
                token.ThrowIfCancellationRequested();
                if (expired?.Invoke() == true) return new(DateTimeOffset.UtcNow, InspectionStatus.TimedOut, "Pool tag parsing exceeded cooperative budget.", []);
                var offset = checked(8 + i * 40);
                var tag = new StringBuilder();
                for (var j = 0; j < 4; j++)
                {
                    var value = bytes[offset + j];
                    tag.Append(value is >= 32 and <= 126 ? ((char)value).ToString() : $"\\x{value:X2}");
                }
                rows.Add(new(tag.ToString(), NativeSystemInformation.U32(bytes, offset), NativeSystemInformation.U32(bytes, offset + 4),
                    NativeSystemInformation.U32(bytes, offset + 8), NativeSystemInformation.U64(bytes, offset + 16),
                    NativeSystemInformation.U32(bytes, offset + 24), NativeSystemInformation.U32(bytes, offset + 28), NativeSystemInformation.U64(bytes, offset + 32)));
            }
            return new(DateTimeOffset.UtcNow, count > cap ? InspectionStatus.Truncated : InspectionStatus.Available,
                $"SystemPoolTagInformation; used values are bytes, allocation/free values are counts. Rows capped at {cap}; no driver attribution or pooltag.txt lookup.", rows.ToArray());
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { return new(DateTimeOffset.UtcNow, InspectionStatus.Failed, "Malformed pool tag snapshot: " + error.Message, []); }
    }
}
