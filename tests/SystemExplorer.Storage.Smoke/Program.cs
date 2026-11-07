using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using SystemExplorer.Windows;

static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
static void Native(bool value) { if (!value) throw new Win32Exception(); }
static void Command(string command, string args)
{
    using var process = Process.Start(new ProcessStartInfo(command, args) { UseShellExecute = false, CreateNoWindow = true })!;
    process.WaitForExit(); Check(process.ExitCode == 0, command + " failed: " + process.ExitCode);
}

Check(OperatingSystem.IsWindows(), "Requires Windows");
if (args.Contains("--memory-cap"))
{
    var timer = Stopwatch.StartNew();
    var aggregate = new StorageScanAggregator("C:/", new(), true);
    for (var i = 0; i < 250_000; i++)
    {
        var name = $"developer-project-{i:D6}-12345678901234567890";
        var dir = aggregate.AddListing(0, "C:/", [new(name, true, 0, 0, 0, 0, 0, default, default)]).Single();
        var files = Enumerable.Range(0, 8).Select(j => new StorageEntry($"file-{j}.bin", false, 1024, 4096, 0, 0, i * 8L + j + 1, default, default)).ToArray();
        aggregate.AddListing(dir.Node, dir.Path, files);
    }
    Console.WriteLine($"Cap stress mutable heap: {GC.GetTotalMemory(true):N0} bytes");
    var snapshot = aggregate.Complete(StorageScanState.Completed, timer.Elapsed, "synthetic caps", []);
    Console.WriteLine($"Cap stress retained heap: {GC.GetTotalMemory(true):N0} bytes; nodes={snapshot.Nodes.Count:N0}; IDs covered={snapshot.HardLinkIdsTracked:N0}; files={snapshot.Totals.Files:N0}; elapsed={timer.Elapsed}; peak WS={Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes");
    Check(snapshot.DirectoryCapReached && snapshot.HardLinkCapReached && snapshot.Totals.Files == 2_000_000, "Cap stress totals/flags mismatch");
    GC.KeepAlive(aggregate); GC.KeepAlive(snapshot);
    return;
}
var provider = new WindowsStorageProvider();
var volumes = await provider.GetVolumesAsync(CancellationToken.None);
Console.WriteLine($"Elevated: {WindowsStorageProvider.IsElevated}; scanner does not enable backup or other privileges.");
Console.WriteLine("Volume | FS | Total | Free | Available | Cluster | Media | Bus | Product | BitLocker | Compression");
Console.WriteLine("Media: seek-penalty IOCTL with disk-number-mapped MSFT_PhysicalDisk fallback; Unspecified is not classified as HDD/SSD.");
foreach (var v in volumes)
    Console.WriteLine($"{v.Root} | {v.FileSystem} | {v.TotalBytes} | {v.FreeBytes} | {v.AvailableToUserBytes} | {v.ClusterBytes} | {v.Media.Display} | {v.Bus.Display} | {v.Product.Display} | {v.BitLocker.Display} | {v.Compression.Display}");
var c = volumes.Single(v => v.Root.Equals(@"C:\", StringComparison.OrdinalIgnoreCase));
var drive = new DriveInfo(@"C:\");
Check(c.TotalBytes == drive.TotalSize && Math.Abs(c.FreeBytes!.Value - drive.TotalFreeSpace) < 64L * 1024 * 1024, "C: capacity/free mismatch");
Check(c.FileSystem == "NTFS" && c.ClusterBytes > 0, "Expected C: NTFS and a cluster size");
foreach (var reading in new[] { c.Media, c.Bus, c.Product, c.BitLocker, c.Compression }) Check(reading.Value != null || !string.IsNullOrEmpty(reading.UnavailableReason), "Unavailable value lacks reason");
Check(c.Media.UnavailableReason != "Windows reports unrecognized media type 0", "Unspecified media must not be treated as unrecognized");
foreach (var volume in volumes.Where(v => v.FileSystem is "FAT32" or "exFAT"))
{
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var result = await provider.ScanAsync(volume.Root, budget.Token);
    Console.WriteLine($"{volume.FileSystem} scan: {volume.Root}, {result.State}, files={result.Totals.Files}, method={result.Method}");
    foreach (var note in result.Notes) Console.WriteLine("Note: " + note);
    Check(result.Totals.LogicalBytes >= 0 && result.Totals.AllocatedBytes >= 0, "Invalid non-NTFS totals");
    Check(result.HardLinkIdsTracked == 0 && result.Notes.Any(n => n.Contains("de-duplication disabled")), "Unstable file IDs must not be tracked");
}

foreach (var (directory, name, expected) in new[] { (@"C:\Users", "All Users", 0xA000000Cu), (@"C:\", "Documents and Settings", 0xA0000003u) })
{
    var tag = NativeSmoke.ReparseTag(directory, name);
    Console.WriteLine($"Native FILE_ID_BOTH_DIR_INFO EaSize: {Path.Combine(directory, name)} = 0x{tag:X8}");
    Check(tag == expected, "EaSize did not contain the expected native reparse tag");
}

var fixture = Path.Combine(Path.GetTempPath(), "SystemExplorer-storage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var originals = new List<string>();
    void FileOfSize(string relative, long size)
    {
        var path = Path.Combine(fixture, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path)) file.SetLength(size);
        originals.Add(path);
    }
    FileOfSize("plain.bin", 12345);
    var link = Path.Combine(fixture, "hardlink.bin");
    Native(NativeSmoke.CreateHardLinkW(link, Path.Combine(fixture, "plain.bin"), IntPtr.Zero));
    var compressed = Path.Combine(fixture, "compressed.bin");
    using (var file = File.Create(compressed))
    {
        Native(NativeSmoke.DeviceIoControl(file.SafeFileHandle, 0x9C040, new byte[] { 1, 0 }, 2, IntPtr.Zero, 0, out _, IntPtr.Zero));
        var zeros = new byte[1024 * 1024];
        for (var i = 0; i < 8; i++) file.Write(zeros);
    }
    Command("compact.exe", $"/c /f \"{compressed}\"");
    originals.Add(compressed);
    var sparse = Path.Combine(fixture, "sparse.bin");
    using (var file = File.Create(sparse))
    {
        Native(NativeSmoke.DeviceIoControl(file.SafeFileHandle, 0x900C4, [], 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
        file.SetLength(16L * 1024 * 1024);
    }
    originals.Add(sparse);
    FileOfSize("package.json", 2); FileOfSize("app.csproj", 0);
    FileOfSize(Path.Combine("node_modules", "dep", "data.bin"), 1234);
    FileOfSize(Path.Combine("node_modules", "dep", "node_modules", "nested.bin"), 567);
    FileOfSize(Path.Combine("bin", "app.dll"), 6000); FileOfSize(Path.Combine("obj", "app.o"), 7000);
    FileOfSize(Path.Combine(string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('x', 40), 7)), "long-path.bin"), 123);
    var junction = Path.Combine(fixture, "loop");
    Command("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.GetDirectoryName(fixture)}\"");
    var expectedLogical = originals.Sum(p => new FileInfo(p).Length);
    var expectedAllocated = originals.Sum(NativeSmoke.Allocation);
    var scan = await provider.ScanAsync(fixture, CancellationToken.None);
    Console.WriteLine($"Fixture: {scan.State}; {scan.Totals.Files} files; logical={scan.Totals.LogicalBytes}; allocated={scan.Totals.AllocatedBytes}; method={scan.Method}");
    Check(scan.State == StorageScanState.Completed && scan.DeniedTotal == 0, "Fixture scan not complete");
    Check(scan.Totals.Files == originals.Count && scan.Totals.LogicalBytes == expectedLogical && scan.Totals.AllocatedBytes == expectedAllocated, "Fixture totals mismatch");
    Check(scan.HardLinkDuplicates == 1, "Hard link pair not deduplicated");
    Check(scan.ReparseTotal == 1 && scan.ReparsePoints.Single().Reason.Contains("junction"), "Junction followed or missing");
    var compressedResult = scan.LargestFiles.Single(f => f.Path.EndsWith("compressed.bin"));
    var sparseResult = scan.LargestFiles.Single(f => f.Path.EndsWith("sparse.bin"));
    Console.WriteLine($"Compressed: logical={compressedResult.LogicalBytes}, allocation={compressedResult.AllocatedBytes}; sparse: logical={sparseResult.LogicalBytes}, allocation={sparseResult.AllocatedBytes}");
    Check(compressedResult.AllocatedBytes < compressedResult.LogicalBytes && sparseResult.AllocatedBytes < sparseResult.LogicalBytes, "Measured compression/sparse allocation missing");
    var ctx = new DeveloperLocationContext(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Path.GetTempPath());
    var bloat = DeveloperBloatAnalyzer.Analyze(scan, ctx, DateTimeOffset.UtcNow, TimeSpan.FromDays(90));
    var project = bloat.Projects.Single(p => StoragePaths.Normalize(p.Path) == StoragePaths.Normalize(fixture));
    Check(project.Artifacts.Count == 3 && project.Artifacts.Select(x => x.Tool).Order().SequenceEqual(new[] { "bin", "node_modules", "obj" }), "Artifact rules missing or nested artifacts double counted");
    Console.WriteLine($"Artifacts: {string.Join(", ", project.Artifacts.Select(x => x.Tool))}; regenerable={project.RegenerableBytes}");
    var missing = await provider.ScanAsync(Path.Combine(fixture, "does-not-exist"), CancellationToken.None);
    Check(missing.State == StorageScanState.Failed && missing.IsPartial && missing.DeniedTotal == 1, "Root failure should be a failed partial result");
    Console.WriteLine($"Missing root: {missing.State}, partial={missing.IsPartial}, reason={missing.DeniedPaths.Single().Reason}");
}
finally
{
    var junction = Path.Combine(fixture, "loop");
    if (Directory.Exists(junction)) Command("cmd.exe", $"/c rmdir \"{junction}\"");
    Directory.Delete(fixture, true);
}

using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel(); var timer = Stopwatch.StartNew();
    var result = await provider.ScanAsync(@"C:\Windows", cancel.Token);
    Check(result.State == StorageScanState.Cancelled && result.IsPartial && timer.Elapsed < TimeSpan.FromSeconds(5), "Immediate cancellation failed");
    Console.WriteLine($"Immediate cancellation: {result.State}, partial={result.IsPartial}, {timer.Elapsed}");
}
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    var timer = Stopwatch.StartNew();
    var operation = provider.StartScan(@"C:\", timeout.Token);
    long peak = 0;
    using var process = Process.GetCurrentProcess();
    while (!operation.Completion.IsCompleted)
    {
        process.Refresh(); peak = Math.Max(peak, process.WorkingSet64);
        var progress = operation.Progress;
        Check(progress.Files >= 0 && progress.AllocatedBytes >= 0, "Invalid progress");
        await Task.WhenAny(operation.Completion, Task.Delay(100));
    }
    var result = await operation.Completion;
    Console.WriteLine($"Managed heap after scan: {GC.GetTotalMemory(true):N0} bytes; retained nodes={result.Nodes.Count:N0}, IDs={result.HardLinkIdsTracked:N0}");
    Console.WriteLine($"C:\\ timed scan: {result.State}, partial={result.IsPartial}, elapsed={timer.Elapsed}, files={result.Totals.Files:N0}, dirs={result.Totals.Directories:N0}, files/sec={result.Totals.Files / timer.Elapsed.TotalSeconds:N0}, peak working set={Math.Max(peak, process.PeakWorkingSet64):N0} bytes");
    Console.WriteLine($"Method: {result.Method}; denied={result.DeniedTotal}; reparse={result.ReparseTotal}; hard-link duplicates={result.HardLinkDuplicates}");
    foreach (var denied in result.DeniedPaths.Take(4)) Console.WriteLine($"Denied: {denied.Path}: {denied.Reason}");
    foreach (var note in result.Notes) Console.WriteLine("Note: " + note);
    Check(result.DeniedTotal > 0 || WindowsStorageProvider.IsElevated, "Expected denied paths on a non-elevated C: scan");
    if (result.DeniedTotal == 0) Console.WriteLine($"No access denials observed in this bounded run; elevated={WindowsStorageProvider.IsElevated}, coverage is partial and no privileges were enabled.");
    else Check(result.DeniedPaths.Count > 0, "Denied paths not retained");
    Check(timer.Elapsed < TimeSpan.FromSeconds(30), "Timed scan did not cancel promptly");
    Check(result.Totals.Files > 0 && result.Method.Contains("FileIdBothDirectoryInfo"), "Real scan returned no files");
}
Console.WriteLine("PASS Storage native smoke");

internal static class NativeSmoke
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLinkW(string path, string target, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int info, IntPtr buffer, uint size);
    internal static long Allocation(string path)
    {
        using var file = CreateFileW(@"\\?\" + path, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            if (file.IsInvalid || !GetFileInformationByHandleEx(file, 1, buffer, 24)) throw new Win32Exception();
            return Marshal.ReadInt64(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static uint ReparseTag(string directory, string name)
    {
        using var file = CreateFileW(directory, 1, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            var restart = true;
            while (true)
            {
                if (!GetFileInformationByHandleEx(file, restart ? 11 : 10, buffer, 65536)) throw new Win32Exception();
                restart = false; var offset = 0;
                while (true)
                {
                    var p = IntPtr.Add(buffer, offset);
                    if (Marshal.PtrToStringUni(IntPtr.Add(p, 104), Marshal.ReadInt32(p, 60) / 2)!.Equals(name, StringComparison.OrdinalIgnoreCase)) return unchecked((uint)Marshal.ReadInt32(p, 64));
                    var next = Marshal.ReadInt32(p); if (next == 0) break; offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
