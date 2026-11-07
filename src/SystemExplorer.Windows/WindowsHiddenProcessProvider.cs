using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using static SystemExplorer.Windows.HiddenNativeMethods;

namespace SystemExplorer.Windows;

public sealed class WindowsHiddenProcessProvider : IProcessCrossViewProvider
{
    private const int SourceCap = 8192, MaximumProbePid = 65536, MaximumBytes = 16 * 1024 * 1024;
    public Task<ProcessCrossViewSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Capture(null, cancellationToken), cancellationToken);
    public Task<ProcessCrossViewSnapshot> RecheckAsync(BoundedItems<ProcessIdentity> identities, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identities, cancellationToken), cancellationToken);

    private static ProcessCrossViewSnapshot Capture(BoundedItems<ProcessIdentity>? recheck, CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var rows = new List<ProcessSighting>();
        var statuses = new List<ProcessSourceStatus>();
        bool truncated = recheck?.Truncated == true;
        void Source(ProcessSightingSource source, Action<List<ProcessSighting>> read)
        {
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var local = new List<ProcessSighting>();
            var status = InspectionStatus.Available;
            string detail = "Read-only, current-token bounded enumeration.";
            try { read(local); }
            catch (SourceFailure ex) { status = ex.Status; detail = ex.Message; }
            catch (Win32Exception ex) { status = Error(ex.NativeErrorCode); detail = $"Windows error {ex.NativeErrorCode}: {ex.Message}"; }
            catch (EntryPointNotFoundException) { status = InspectionStatus.Unsupported; detail = "Native entry point unavailable."; }
            catch (InvalidDataException ex) { status = InspectionStatus.Failed; detail = ex.Message; }
            token.ThrowIfCancellationRequested();
            if (local.Count > SourceCap) { status = InspectionStatus.Truncated; truncated = true; }
            truncated |= status == InspectionStatus.Truncated;
            rows.AddRange(local.Take(SourceCap));
            statuses.Add(new(source, status, detail, watch.Elapsed.TotalMilliseconds));
        }
        Source(ProcessSightingSource.Toolhelp, list =>
        {
            using var snapshot = NativeMethods.CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) throw new Win32Exception();
            var entry = new NativeMethods.ProcessEntry { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry>() };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry)) { if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(); return; }
            do
            {
                token.ThrowIfCancellationRequested();
                var sighting = Probe((int)entry.Pid, ProcessSightingSource.Toolhelp);
                list.Add(sighting with { ImageName = entry.Name, Status = InspectionStatus.Available });
                if (list.Count > SourceCap) return;
            } while (NativeMethods.Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception();
        });
        Source(ProcessSightingSource.NtSystemInformation, list =>
        {
            if (IntPtr.Size != 8) throw new SourceFailure(InspectionStatus.Unsupported, "SystemProcessInformation parser requires x64.");
            var snapshot = new NativeSystemInformation().Query(5, false, token, () => false);
            if (snapshot.Status != InspectionStatus.Available) throw new SourceFailure(snapshot.Status, snapshot.Detail);
            var bytes = snapshot.Bytes;
            int offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (offset < 0 || offset > bytes.Length - 96) throw new InvalidDataException("Invalid process information record.");
                var record = bytes.AsSpan(offset);
                var pid = BinaryPrimitives.ReadInt64LittleEndian(record[80..]);
                var creation = BinaryPrimitives.ReadInt64LittleEndian(record[32..]);
                if (pid is >= 0 and <= int.MaxValue) list.Add(new(new((int)pid, creation > 0 ? creation : null), ProcessSightingSource.NtSystemInformation));
                if (list.Count > SourceCap) return;
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(record);
                if (next == 0) break;
                if (next < 96 || next > bytes.Length - offset) throw new InvalidDataException("Invalid process record offset.");
                offset += (int)next;
            }
        });
        if (recheck is null)
        {
            Source(ProcessSightingSource.NtGetNextProcess, list =>
            {
                SafeProcessHandle? previous = null;
                try
                {
                    while (list.Count <= SourceCap)
                    {
                        token.ThrowIfCancellationRequested();
                        int result = NtGetNextProcess(previous?.DangerousGetHandle() ?? IntPtr.Zero, 0x1000, 0, 0, out var next);
                        previous?.Dispose(); previous = null;
                        if (result < 0)
                        {
                            next.Dispose();
                            if (result == unchecked((int)0x8000001A)) break;
                            throw new SourceFailure(NativeSystemInformation.MapStatus(result), $"NtGetNextProcess returned NTSTATUS 0x{result:X8}; inaccessible processes may be omitted.");
                        }
                        previous = next;
                        uint pid = GetProcessId(next);
                        if (pid == 0) throw new Win32Exception();
                        list.Add(FromHandle(next, (int)pid, ProcessSightingSource.NtGetNextProcess));
                    }
                }
                finally { previous?.Dispose(); }
            });
        }
        Source(ProcessSightingSource.PidProbe, list =>
        {
            IEnumerable<int> pids = recheck is not null ? recheck.Items.Select(i => i.Pid) :
                Enumerable.Range(1, MaximumProbePid / 4).Select(i => i * 4).Concat(rows.Select(s => s.Identity.Pid).Where(p => p > MaximumProbePid));
            foreach (int pid in pids.Distinct().Take(MaximumProbePid / 4 + SourceCap))
            {
                token.ThrowIfCancellationRequested();
                if (pid <= 0) continue;
                var sighting = Probe(pid, ProcessSightingSource.PidProbe);
                if (sighting.Status != InspectionStatus.Exited || recheck is not null) list.Add(sighting);
                if (list.Count > SourceCap) return;
            }
        });
        if (recheck is null)
        {
            Source(ProcessSightingSource.EtwOwner, list => ReadEtw(list, token));
            Source(ProcessSightingSource.TcpOwner, list => ReadEndpoints(list, true, token));
            Source(ProcessSightingSource.UdpOwner, list => ReadEndpoints(list, false, token));
            Source(ProcessSightingSource.ServiceOwner, list => ReadServices(list, token));
        }
        var elevation = WindowsHiddenEnvironment.GetElevation();
        return new(at, truncated ? InspectionStatus.Truncated : statuses.Any(s => s.Status != InspectionStatus.Available) ? InspectionStatus.Partial : InspectionStatus.Available,
            $"{elevation.Detail} PID probe scope: multiples of four through {MaximumProbePid}, plus observed higher PIDs; max {SourceCap} rows per source. NtGetNextProcess only returns processes accessible for limited query. Recheck delay is caller-owned.",
            elevation.IsElevated == true, new(statuses, 8), new(rows, SourceCap * 8, truncated));
    }

    private static ProcessSighting Probe(int pid, ProcessSightingSource source)
    {
        using var process = NativeMethods.OpenProcess(0x1000, false, (uint)pid);
        if (!process.IsInvalid) return FromHandle(process, pid, source);
        int error = Marshal.GetLastWin32Error();
        return new(new(pid, null), source, Status: error == 87 ? InspectionStatus.Exited : Error(error));
    }
    private static ProcessSighting FromHandle(SafeProcessHandle process, int pid, ProcessSightingSource source)
    {
        bool time = NativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _);
        bool exit = GetExitCodeProcess(process, out uint code);
        var image = new StringBuilder(32768);
        uint length = (uint)image.Capacity;
        bool hasImage = NativeMethods.QueryFullProcessImageNameW(process, 0, image, ref length);
        string? path = hasImage ? image.ToString() : null;
        string? imageDetail = hasImage ? null : $"Image unavailable: Win32 {Marshal.GetLastWin32Error()}";
        return new(new(pid, time && creation.Ticks > 0 ? (long)creation.Ticks : null), source, ImageName: path is null ? null : Path.GetFileName(path),
            ImagePath: path, Exited: exit && code != 259, Status: time && exit ? InspectionStatus.Available : InspectionStatus.Partial,
            ExitCode: exit ? code : null, ImageDetail: imageDetail);
    }
    private static InspectionStatus Error(int error) => error == 5 ? InspectionStatus.AccessDenied : error is 50 or 120 ? InspectionStatus.Unsupported : InspectionStatus.Failed;
    private sealed class SourceFailure(InspectionStatus status, string detail) : Exception(detail) { internal InspectionStatus Status { get; } = status; }

    private static void ReadEtw(List<ProcessSighting> rows, CancellationToken token)
    {
        const int maxGuids = 2048;
        uint result = EnumerateTraceGuidsEx(0, IntPtr.Zero, 0, IntPtr.Zero, 0, out uint needed);
        if (result is not (0 or 122)) throw new Win32Exception((int)result);
        if (needed > maxGuids * 16) throw new SourceFailure(InspectionStatus.Truncated, "ETW GUID list exceeds 2048-provider cap.");
        IntPtr guids = Marshal.AllocHGlobal((int)Math.Max(needed, 16));
        bool denied = false, limited = false;
        try
        {
            uint capacity = Math.Max(needed, 16);
            result = EnumerateTraceGuidsEx(0, IntPtr.Zero, 0, guids, capacity, out needed);
            if (result != 0) throw new Win32Exception((int)result);
            if (needed > capacity || needed % 16 != 0) throw new InvalidDataException("Invalid ETW GUID list length.");
            for (int i = 0; i < needed / 16; i++)
            {
                token.ThrowIfCancellationRequested();
                IntPtr guid = IntPtr.Add(guids, i * 16);
                result = EnumerateTraceGuidsEx(1, guid, 16, IntPtr.Zero, 0, out uint bytes);
                if (result == 5) { denied = true; continue; }
                if (result is 1168 or 4201) continue;
                if (result is not (0 or 122)) throw new Win32Exception((int)result);
                if (bytes > 1024 * 1024) { limited = true; continue; }
                uint size = Math.Max(bytes, 8);
                IntPtr info = Marshal.AllocHGlobal((int)size);
                try
                {
                    result = EnumerateTraceGuidsEx(1, guid, 16, info, size, out bytes);
                    if (result == 5) { denied = true; continue; }
                    if (result is 1168 or 4201) continue;
                    if (result == 122) { limited = true; continue; }
                    if (result != 0) throw new Win32Exception((int)result);
                    if (bytes < 8 || bytes > size) throw new InvalidDataException("Invalid ETW provider information length.");
                    uint count = (uint)Marshal.ReadInt32(info);
                    int offset = 8;
                    for (uint j = 0; j < count; j++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (offset > bytes - 16) throw new InvalidDataException("Invalid ETW provider instance offset.");
                        uint pid = (uint)Marshal.ReadInt32(info, offset + 8);
                        if (pid > 0 && pid <= int.MaxValue && !rows.Any(r => r.Identity.Pid == pid)) rows.Add(new(new((int)pid, null), ProcessSightingSource.EtwOwner));
                        if (rows.Count > SourceCap) return;
                        uint next = (uint)Marshal.ReadInt32(info, offset);
                        if (j + 1 < count && (next < 16 || next > bytes - offset)) throw new InvalidDataException("Invalid ETW provider instance chain.");
                        offset += (int)next;
                    }
                }
                finally { Marshal.FreeHGlobal(info); }
            }
        }
        finally { Marshal.FreeHGlobal(guids); }
        if (limited) throw new SourceFailure(InspectionStatus.Truncated, "ETW instance information exceeded byte cap or grew during enumeration.");
        if (denied) throw new SourceFailure(InspectionStatus.AccessDenied, "Some ETW provider registrations denied access with the current token.");
    }

    private static void ReadEndpoints(List<ProcessSighting> rows, bool tcp, CancellationToken token)
    {
        foreach (uint family in new uint[] { 2, 23 })
        {
            token.ThrowIfCancellationRequested();
            uint size = 0;
            uint Query(IntPtr buffer, ref uint length) => tcp ? GetExtendedTcpTable(buffer, ref length, false, family, 5, 0) : GetExtendedUdpTable(buffer, ref length, false, family, 1, 0);
            uint result = Query(IntPtr.Zero, ref size);
            if (result is not (0 or 122)) throw new Win32Exception((int)result);
            bool read = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (size < 4 || size > MaximumBytes) throw new SourceFailure(InspectionStatus.Truncated, "Endpoint table exceeds byte cap.");
                uint capacity = size;
                IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    result = Query(buffer, ref size);
                    if (result == 122) continue;
                    if (result != 0) throw new Win32Exception((int)result);
                    int stride = (tcp, family) switch { (true, 23) => 56, (true, _) => 24, (false, 23) => 28, _ => 12 };
                    uint count = (uint)Marshal.ReadInt32(buffer);
                    if (count > (capacity - 4) / stride) throw new InvalidDataException("Invalid endpoint count.");
                    for (int i = 0; i < count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        uint pid = (uint)Marshal.ReadInt32(buffer, 4 + i * stride + stride - 4);
                        if (pid > 0 && pid <= int.MaxValue && !rows.Any(r => r.Identity.Pid == pid)) rows.Add(new(new((int)pid, null), tcp ? ProcessSightingSource.TcpOwner : ProcessSightingSource.UdpOwner));
                        if (rows.Count > SourceCap) return;
                    }
                    read = true; break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (!read) throw new SourceFailure(InspectionStatus.Partial, "Endpoint table changed during three bounded attempts.");
        }
    }

    private static void ReadServices(List<ProcessSighting> rows, CancellationToken token)
    {
        using var manager = OpenSCManagerW(null, null, 4);
        if (manager.IsInvalid) throw new Win32Exception();
        const uint capacity = 256 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
        uint resume = 0;
        try
        {
            for (int page = 0; page < 32; page++)
            {
                token.ThrowIfCancellationRequested();
                bool ok = EnumServicesStatusExW(manager, 0, 0x3ff, 3, buffer, capacity, out _, out uint count, ref resume, null);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != 234) throw new Win32Exception(error);
                int stride = Marshal.SizeOf<ServiceEntry>();
                if (count > capacity / stride) throw new InvalidDataException("Invalid service count.");
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = Marshal.PtrToStructure<ServiceEntry>(IntPtr.Add(buffer, i * stride));
                    if (entry.Pid > 0 && entry.Pid <= int.MaxValue && !rows.Any(r => r.Identity.Pid == entry.Pid)) rows.Add(new(new((int)entry.Pid, null), ProcessSightingSource.ServiceOwner));
                    if (rows.Count > SourceCap) return;
                }
                if (ok) return;
            }
            throw new SourceFailure(InspectionStatus.Truncated, "SCM exceeded 32-page cap.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
