using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;
using static SystemExplorer.Windows.NativeStorageMethods;

namespace SystemExplorer.Windows;

public sealed class WindowsStorageScan
{
    private const int BufferSize = 64 * 1024;
    private readonly object gate = new();
    private readonly Stack<(int Node, string Path)> work = new();
    private readonly CancellationToken cancellation;
    private readonly string root;
    private readonly List<string> notes = [];
    private int active, fallback, rootFailed;
    private long cluster = 4096;
    private bool clusterKnown;
    private bool stableIds;
    public StorageScanAggregator Aggregator { get; }
    public StorageProgress Progress => Aggregator.Progress;
    public Task<StorageScanResult> Completion { get; }

    internal WindowsStorageScan(string rootPath, StorageScanLimits limits, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(rootPath); cancellation = cancellationToken;
        Aggregator = new(root, limits, true);
        work.Push((0, root));
        Completion = Task.Run(Run);
    }
    private StorageScanResult Run()
    {
        var timer = Stopwatch.StartNew();
        var volume = Path.GetPathRoot(root)!;
        var fs = new StringBuilder(261);
        var fsKnown = GetVolumeInformationW(volume, new StringBuilder(261), 261, out _, out _, out _, fs, 261);
        stableIds = fsKnown && StorageFileSystems.HasStableFileIds(fs.ToString());
        if (!stableIds) AddNote($"Hard-link de-duplication disabled: {(fsKnown ? fs.ToString() : "file system unavailable: " + new Win32Exception().Message)} does not provide verified stable unique file IDs (only NTFS/ReFS are enabled).");
        clusterKnown = GetDiskFreeSpaceW(volume, out var sectors, out var bytes, out _, out _);
        if (clusterKnown) cluster = (long)sectors * bytes;
        else AddNote("Cluster size unavailable: fallback allocation uses logical bytes, not measured on-disk bytes.");
        var threads = Enumerable.Range(0, Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2))).Select(_ => new Thread(Worker)
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Storage metadata scanner" }).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        work.Clear(); work.TrimExcess();
        return Aggregator.Complete(cancellation.IsCancellationRequested ? StorageScanState.Cancelled : rootFailed != 0 ? StorageScanState.Failed : StorageScanState.Completed,
            timer.Elapsed, fallback != 0 ? "FileIdBothDirectoryInfo + FindFirstFileEx (fallback allocation estimated; fallback hard links not deduplicated)" : "FileIdBothDirectoryInfo (measured allocation)", notes);
    }
    private void Worker()
    {
        var buffer = Marshal.AllocHGlobal(BufferSize);
        var background = SetThreadPriority(GetCurrentThread(), 0x10000);
        if (!background) AddNote("Background I/O priority unavailable: " + new Win32Exception().Message);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                (int Node, string Path) next;
                lock (gate)
                {
                    while (work.Count == 0 && active > 0 && !cancellation.IsCancellationRequested) Monitor.Wait(gate, 50);
                    if (cancellation.IsCancellationRequested || work.Count == 0) return;
                    next = work.Pop(); active++;
                }
                try { ScanDirectory(next.Node, next.Path, buffer); }
                finally { lock (gate) { active--; Monitor.PulseAll(gate); } }
            }
        }
        finally
        {
            if (background) SetThreadPriority(GetCurrentThread(), 0x20000);
            Marshal.FreeHGlobal(buffer);
        }
    }
    private void ScanDirectory(int node, string path, IntPtr buffer)
    {
        try
        {
            if (cancellation.IsCancellationRequested) return;
            using var handle = CreateFileW(LongPath(path), 1, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception();
            var restart = true;
            while (!cancellation.IsCancellationRequested)
            {
                if (!GetFileInformationByHandleEx(handle, restart ? 11 : 10, buffer, BufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18) return;
                    if (restart && error is 1 or 50 or 87 or 124) { FindListing(node, path, buffer); return; }
                    throw new Win32Exception(error);
                }
                restart = false;
                var entries = new List<StorageEntry>();
                var offset = 0;
                while (true)
                {
                    var p = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(p);
                    var nameBytes = Marshal.ReadInt32(p, 60);
                    if (nameBytes < 0 || nameBytes % 2 != 0 || offset + 104L + nameBytes > BufferSize) throw new InvalidDataException("Invalid native directory record length");
                    var attributes = (FileAttributes)Marshal.ReadInt32(p, 56);
                    entries.Add(new(Marshal.PtrToStringUni(IntPtr.Add(p, 104), nameBytes / 2)!, (attributes & FileAttributes.Directory) != 0,
                        Marshal.ReadInt64(p, 40), Marshal.ReadInt64(p, 48), attributes, unchecked((uint)Marshal.ReadInt32(p, 64)),
                        stableIds ? Marshal.ReadInt64(p, 96) : 0, Date(Marshal.ReadInt64(p, 24)), Date(Marshal.ReadInt64(p, 8))));
                    if (next == 0) break;
                    if (next < 104 || offset + (long)next >= BufferSize) throw new InvalidDataException("Invalid native directory record offset");
                    offset += next;
                }
                Submit(node, path, entries, buffer);
            }
        }
        catch (Exception ex)
        {
            if (node == 0 && StoragePaths.Normalize(path).Equals(StoragePaths.Normalize(root), StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref rootFailed, 1);
            Aggregator.AddDenied(node, path, ex.Message);
        }
    }
    private void Submit(int node, string path, IReadOnlyList<StorageEntry> entries, IntPtr buffer)
    {
        foreach (var child in Aggregator.AddListing(node, path, entries))
        {
            if (cancellation.IsCancellationRequested) return;
            var queued = false;
            lock (gate)
            {
                if (work.Count < 4096) { work.Push(child); queued = true; Monitor.PulseAll(gate); }
            }
            if (!queued) ScanDirectory(child.Node, child.Path, buffer);
        }
    }
    private void FindListing(int node, string path, IntPtr buffer)
    {
        Interlocked.Exchange(ref fallback, 1);
        AddNote("FindFirstFileEx fallback: allocation estimated from logical size and cluster size; hard-link tracking off for fallback entries.");
        var find = FindFirstFileExW(LongPath(path.TrimEnd('\\', '/') + "\\*"), 1, out var data, 0, IntPtr.Zero, 2);
        if (find == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return;
            throw new Win32Exception(error);
        }
        try
        {
            var entries = new List<StorageEntry>(256);
            while (!cancellation.IsCancellationRequested)
            {
                var size = checked(((long)data.SizeHigh << 32) | data.SizeLow);
                var allocated = clusterKnown && cluster > 0 ? checked((size + cluster - 1) / cluster * cluster) : size;
                entries.Add(new(data.Name, (data.Attributes & 16) != 0, size, allocated, (FileAttributes)data.Attributes, data.Reserved0, 0,
                    Date(((long)data.Write.dwHighDateTime << 32) | (uint)data.Write.dwLowDateTime), Date(((long)data.Creation.dwHighDateTime << 32) | (uint)data.Creation.dwLowDateTime)));
                if (entries.Count == 256) { Submit(node, path, entries, buffer); entries.Clear(); }
                if (FindNextFileW(find, out data)) continue;
                var error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error);
                break;
            }
            if (entries.Count > 0) Submit(node, path, entries, buffer);
        }
        finally { FindClose(find); }
    }
    private void AddNote(string note) { lock (gate) if (!notes.Contains(note)) notes.Add(note); }
    private static DateTime Date(long value) { try { return value > 0 ? DateTime.FromFileTimeUtc(value) : default; } catch (ArgumentOutOfRangeException) { return default; } }
    private static string LongPath(string path)
    {
        path = path.Replace('/', '\\');
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
