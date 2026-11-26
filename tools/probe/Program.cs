using System.Diagnostics;
using System.Text.Json;
using SystemExplorer.Windows;

var duration = int.Parse(args[0]);
var interval = int.Parse(args[1]);
var output = args[2];

using var provider = new WindowsMonitorProvider();
using var writer = new StreamWriter(output);
var sw = Stopwatch.StartNew();
await provider.CaptureAsync(CancellationToken.None);
await Task.Delay(interval);
var scanTimes = new List<double>();
while (sw.Elapsed.TotalSeconds < duration)
{
    var scanStart = Stopwatch.GetTimestamp();
    var scan = await provider.CaptureAsync(CancellationToken.None);
    var scanMs = Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds;
    scanTimes.Add(scanMs);
    var record = new Dictionary<string, object?>
    {
        ["atUtc"] = scan.CapturedAt.UtcDateTime.ToString("o"),
        ["elapsedMs"] = sw.Elapsed.TotalMilliseconds,
        ["scanMs"] = scanMs,
        ["processes"] = scan.Processes.Count,
        ["cpuPercent"] = scan.TotalCpuPercent,
        ["physicalTotalBytes"] = scan.Memory.PhysicalTotalBytes,
        ["physicalAvailableBytes"] = scan.Memory.PhysicalAvailableBytes,
        ["physicalUsedBytes"] = scan.Memory.PhysicalUsedBytes,
        ["commitTotalBytes"] = scan.Memory.CommitTotalBytes,
        ["commitLimitBytes"] = scan.Memory.CommitLimitBytes,
        ["systemCacheBytes"] = scan.Memory.SystemCacheBytes,
        ["systemThreads"] = scan.Memory.ThreadCount,
        ["systemHandles"] = scan.Memory.HandleCount,
        ["systemProcesses"] = scan.Memory.ProcessCount,
        ["diskReadBps"] = scan.DiskReadBytesPerSecond,
        ["diskWriteBps"] = scan.DiskWriteBytesPerSecond,
    };
    writer.WriteLine(JsonSerializer.Serialize(record));
    writer.Flush();
    await Task.Delay(interval);
}
scanTimes.Sort();
Console.WriteLine($"probe complete scans={scanTimes.Count} scanP50={scanTimes[scanTimes.Count/2]:F1}ms scanP95={scanTimes[(int)Math.Ceiling(scanTimes.Count*0.95)-1]:F1}ms");
