using System.Diagnostics;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsPoolTagDriverProvider : IPoolTagDriverProvider
{
    private sealed record CachedFile(long Size, long Modified, IReadOnlySet<uint> Tags, IReadOnlySet<uint> Matches);
    private readonly Dictionary<string, CachedFile> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PoolTagDriverInspection> CaptureAsync(IReadOnlyCollection<uint> tags,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Capture(tags, progress, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private PoolTagDriverInspection Capture(IReadOnlyCollection<uint> tags, IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var requested = tags.Take(65536).ToHashSet();
        var matches = requested.ToDictionary(t => t, _ => new List<string>());
        var status = tags.Count > 65536 ? RamReadingStatus.Truncated : RamReadingStatus.Available;
        var scanned = 0; var unreadable = 0; var oversized = 0;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(Path.Combine(system, "drivers"), "*.sys")
                .Concat(Directory.EnumerateFiles(system, "win32k*.sys")).Append(Path.Combine(system, "ntoskrnl.exe"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3001).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(RamReadingStatus.Unavailable, "Local driver enumeration failed: " + ex.Message, 0, 0, []); }
        if (paths.Length > 3000) status = RamReadingStatus.Truncated;
        foreach (var path in paths.Take(3000))
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(30)) { status = RamReadingStatus.TimedOut; break; }
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 64 * 1024 * 1024) { oversized++; continue; }
                IReadOnlySet<uint> found;
                if (cache.TryGetValue(path, out var old) && old.Size == info.Length && old.Modified == info.LastWriteTimeUtc.Ticks && old.Tags.SetEquals(requested))
                    found = old.Matches;
                else
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        65536, FileOptions.SequentialScan);
                    var buffer = new byte[65536 + 3];
                    var all = new HashSet<uint>();
                    var carry = 0; long readTotal = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (clock.Elapsed >= TimeSpan.FromSeconds(30)) { status = RamReadingStatus.TimedOut; break; }
                        var read = stream.Read(buffer, carry, 65536);
                        if (read == 0) break;
                        readTotal += read;
                        if (readTotal > 64 * 1024 * 1024) { status = RamReadingStatus.Truncated; break; }
                        var length = carry + read;
                        all.UnionWith(PoolTagDriverMatcher.Match(buffer.AsSpan(0, length), requested, token));
                        carry = Math.Min(3, length);
                        buffer.AsSpan(length - carry, carry).CopyTo(buffer);
                    }
                    found = all;
                    if (status != RamReadingStatus.TimedOut && readTotal == info.Length)
                    {
                        if (cache.Count >= 128) cache.Clear();
                        if (requested.Count <= 4096) cache[path] = new(info.Length, info.LastWriteTimeUtc.Ticks, requested, found);
                    }
                }
                foreach (var tag in found) matches[tag].Add(Path.GetFileName(path));
                scanned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
            progress?.Report((scanned + unreadable + oversized, Math.Min(paths.Length, 3000)));
        }
        if ((unreadable > 0 || oversized > 0) && status == RamReadingStatus.Available) status = RamReadingStatus.Truncated;
        return new(status, $"Local files only; scanned {scanned}, unreadable {unreadable}, >64 MiB {oversized}. Caps: 3000 files, 65536 tags, 30 seconds (cooperative; blocking file reads cannot be interrupted). Missing matches are not exclusion evidence. " + PoolTagDriverMatcher.LeadDetail,
            scanned, unreadable, matches.Select(p => PoolTagDriverMatcher.Lead(p.Key, p.Value)).ToArray());
    }
}
