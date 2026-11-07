using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsRamBreakdownProvider : IRamBreakdownProvider
{
    public Task<MonitorSnapshot> CaptureProcessesAsync(CancellationToken cancellationToken) => Task.Run(() => ReadProcesses(cancellationToken), cancellationToken);

    private static MonitorSnapshot ReadProcesses(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var captured = DateTimeOffset.UtcNow;
        var native = new NativeSystemInformation();
        var data = native.Query(5, false, token, () => clock.Elapsed >= TimeSpan.FromSeconds(3));
        var rows = new List<ProcessSnapshot>();
        var offset = 0;
        var reason = data.Detail;
        if (data.Status == InspectionStatus.Available)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= TimeSpan.FromSeconds(3) || rows.Count >= 65536 || offset > data.Bytes.Length - 256)
                { reason = "SystemProcessInformation malformed or collection cap/time budget reached."; break; }
                var next = BitConverter.ToUInt32(data.Bytes, offset);
                var nameLength = BitConverter.ToUInt16(data.Bytes, offset + 56);
                var pointer = BitConverter.ToUInt64(data.Bytes, offset + 64);
                var name = "System idle process";
                if (nameLength != 0)
                {
                    if ((nameLength & 1) != 0 || pointer < data.BaseAddress || pointer - data.BaseAddress > (ulong)data.Bytes.Length ||
                        (ulong)nameLength > (ulong)data.Bytes.Length - (pointer - data.BaseAddress))
                    { reason = "Invalid process image-name bounds; collection not trusted."; break; }
                    name = Encoding.Unicode.GetString(data.Bytes, checked((int)(pointer - data.BaseAddress)), nameLength);
                }
                var pid = BitConverter.ToUInt64(data.Bytes, offset + 80);
                if (pid > int.MaxValue) { reason = "Invalid process identifier."; break; }
                rows.Add(new() { Identity = new((int)pid, BitConverter.ToInt64(data.Bytes, offset + 32)), Name = name,
                    PrivateWorkingSetBytes = BitConverter.ToUInt64(data.Bytes, offset + 8), WorkingSetBytes = BitConverter.ToUInt64(data.Bytes, offset + 144) });
                if (next == 0) return new() { CapturedAt = captured, Processes = rows, Limitations = ["Read-only SystemProcessInformation; no process handles opened."] };
                if (next < 256 || next > data.Bytes.Length - offset) { reason = "Invalid process entry offset."; break; }
                offset += (int)next;
            }
        }
        return new() { CapturedAt = captured, Processes = [new() { Identity = default, Name = "Unavailable process inventory" }], Limitations = [reason] };
    }

    public Task<RamBreakdown> CaptureAsync(MonitorSnapshot snapshot, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var memory = new RamNative.MemoryStatus { Length = (uint)Marshal.SizeOf<RamNative.MemoryStatus>() };
        ulong? usable = RamNative.GlobalMemoryStatusEx(ref memory) ? memory.TotalPhys : null;
        var usableError = usable.HasValue ? 0 : Marshal.GetLastWin32Error();
        ulong? installed = RamNative.GetPhysicallyInstalledSystemMemory(out var kb) ? checked(kb * 1024) : null;
        var installedError = installed.HasValue ? 0 : Marshal.GetLastWin32Error();
        if (snapshot.Processes.Any(p => !p.PrivateWorkingSetBytes.HasValue)) snapshot = ReadProcesses(cancellationToken);
        var performance = new byte[4096];
        var status = RamNative.NtQuerySystemInformation(2, performance, performance.Length, out var length);
        cancellationToken.ThrowIfCancellationRequested();
        ulong? Pages(int offset) => status >= 0 && length >= offset + 4 && clock.Elapsed < TimeSpan.FromSeconds(3)
            ? checked((ulong)BitConverter.ToUInt32(performance, offset) * (uint)Environment.SystemPageSize) : null;
        var lists = new byte[176];
        var listStatus = RamNative.NtQuerySystemInformation(80, lists, lists.Length, out var listLength);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = listStatus >= 0 && listLength >= lists.Length && clock.Elapsed < TimeSpan.FromSeconds(3) ? PhysicalMemoryParser.ParseMemoryLists(lists) : null;
        ulong? Bytes(ulong? pages) => pages.HasValue ? checked(pages.Value * (uint)Environment.SystemPageSize) : null;
        var result = RamAttribution.Reconcile(new(installed, usable, Bytes(parsed is null ? null : parsed.Modified + parsed.ModifiedNoWrite),
            Enumerable.Range(0, 8).Select(i => Bytes(parsed?.StandbyByPriority[i])).ToArray(),
            Bytes(parsed is null ? null : parsed.Zero + parsed.Free), Pages(164), Pages(168), Pages(116), Pages(172), Pages(140), snapshot.Processes));
        return result with { Detail = $"Installed physical memory: {(installed.HasValue ? "available" : $"unavailable (Win32 {installedError})")}; usable physical memory: {(usable.HasValue ? "available" : $"unavailable (Win32 {usableError})")}. " + string.Join("; ", snapshot.Limitations), Categories = result.Categories.Select(r => r with
        { Detail = r.Detail + $" Source snapshot {snapshot.CapturedAt:O}; performance NTSTATUS 0x{status:X8}; memory lists NTSTATUS 0x{listStatus:X8}." }).ToArray() };
    }, cancellationToken);
}
