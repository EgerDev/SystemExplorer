using System.Diagnostics;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsHandleInventoryProvider : IHandleInventoryProvider
{
    internal const int MaximumRows = 4096;
    private const string Scope = " Handle names are out of scope because they require DuplicateHandle; unknown type names remain null.";
    private readonly IInventoryNative native;
    public WindowsHandleInventoryProvider() : this(new NativeSystemInformation()) { }
    internal WindowsHandleInventoryProvider(IInventoryNative native) => this.native = native;

    public Task<HandleInventory> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identity, cancellationToken), cancellationToken);

    private HandleInventory Capture(ProcessIdentity identity, CancellationToken token)
    {
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        bool Expired() => clock.Elapsed >= TimeSpan.FromSeconds(3);
        HandleInventory Empty(InspectionStatus status, string detail) => new(identity, captured, status, detail + Scope, null, [], [], null, []);
        token.ThrowIfCancellationRequested();
        if (!identity.IsStable) return Empty(InspectionStatus.IdentityMismatch, "Stable process creation time is required; no rows attributed.");
        if (!native.IsSupported) return Empty(InspectionStatus.Unsupported, "Only Windows x64 layouts are supported.");
        try
        {
            using var lease = native.Open(identity);
            var valid = lease.Validate(identity);
            if (valid != InspectionStatus.Available) return Empty(valid, "Process identity/liveness validation failed before collection; no rows attributed.");
            var snapshot = native.Query(64, false, token, Expired);
            if (snapshot.Status != InspectionStatus.Available) return Empty(snapshot.Status, snapshot.Detail);
            var types = native.Query(0, true, token, Expired);
            var names = new Dictionary<int, string>();
            var typeDetail = types.Detail;
            if (types.Status == InspectionStatus.Available)
            {
                try { names = NativeSystemInformation.ParseTypeNames(types.Bytes, types.BaseAddress, token, Expired); }
                catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
                { types = types with { Status = InspectionStatus.Failed }; typeDetail = error.Message; }
            }
            var result = Parse(snapshot.Bytes, identity, names, MaximumRows, token, Expired) with { CapturedAt = captured };
            token.ThrowIfCancellationRequested();
            valid = lease.Validate(identity);
            if (valid != InspectionStatus.Available) return Empty(valid, "Process identity/liveness validation failed after collection; data discarded.");
            if (Expired()) return Empty(InspectionStatus.TimedOut, "Three-second cooperative budget exceeded; native calls cannot be interrupted.");
            if (types.Status != InspectionStatus.Available)
                result = result with { Status = result.Status == InspectionStatus.Available ? InspectionStatus.Partial : result.Status,
                    Detail = result.Detail + $" Type names unavailable ({types.Status}): {typeDetail}" };
            return result;
        }
        catch (InventoryNativeException error) { return Empty(error.Status, error.Message); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return Empty(InspectionStatus.Unsupported, "Required native API unavailable; no fallback attempted."); }
    }

    internal static HandleInventory Parse(ReadOnlySpan<byte> bytes, ProcessIdentity identity, IReadOnlyDictionary<int, string> names,
        int cap = MaximumRows, CancellationToken token = default, Func<bool>? expired = null)
    {
        HandleInventory Empty(InspectionStatus status, string detail) => new(identity, DateTimeOffset.UtcNow, status, detail + Scope, null, [], [], null, []);
        if (!identity.IsStable) return Empty(InspectionStatus.IdentityMismatch, "Stable identity required.");
        try
        {
            NativeSystemInformation.RequireEntries(bytes.Length, 16, 0, 40);
            var count = NativeSystemInformation.U64(bytes, 0);
            NativeSystemInformation.RequireEntries(bytes.Length, 16, count, 40);
            var system = new Dictionary<int, long>();
            var process = new Dictionary<int, long>();
            var rows = new List<HandleObservation>();
            long processCount = 0;
            for (ulong i = 0; i < count; i++)
            {
                if (i % 256 == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (expired?.Invoke() == true) return Empty(InspectionStatus.TimedOut, "Handle parsing exceeded cooperative budget.");
                }
                var offset = checked(16 + (int)i * 40);
                var type = NativeSystemInformation.U16(bytes, offset + 30);
                system[type] = system.GetValueOrDefault(type) + 1;
                if (NativeSystemInformation.U64(bytes, offset + 8) != (ulong)identity.Pid) continue;
                processCount++;
                process[type] = process.GetValueOrDefault(type) + 1;
                if (rows.Count < cap) rows.Add(new(NativeSystemInformation.U64(bytes, offset + 16), type, names.GetValueOrDefault(type),
                    NativeSystemInformation.U32(bytes, offset + 24), NativeSystemInformation.U32(bytes, offset + 32)));
            }
            HandleTypeCount[] Counts(Dictionary<int, long> counts) => counts.OrderBy(p => p.Key)
                .Select(p => new HandleTypeCount(p.Key, names.GetValueOrDefault(p.Key), p.Value)).ToArray();
            return new(identity, DateTimeOffset.UtcNow, processCount > cap ? InspectionStatus.Truncated : InspectionStatus.Available,
                $"SystemExtendedHandleInformation snapshot; counts cover the complete snapshot; rows capped at {cap}." + Scope,
                processCount, Counts(process), rows.ToArray(), (long)count, Counts(system));
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { return Empty(InspectionStatus.Failed, "Malformed handle snapshot: " + error.Message); }
    }
}
