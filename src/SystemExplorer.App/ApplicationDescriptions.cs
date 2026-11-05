using System.Diagnostics;
using System.Globalization;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed class ApplicationDescriptions
{
    private const int Capacity = 128;
    private readonly SemaphoreSlim pending = new(2);
    private readonly Dictionary<string, (string? Text, DateTimeOffset Expires)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> order = new();

    public string? GetCached(ProcessSnapshot process)
    {
        var path = process.ExecutablePath;
        if (!process.Identity.IsStable || path is null || !string.Equals(Path.GetFileName(path), process.Name, StringComparison.OrdinalIgnoreCase)) return null;
        lock (cache) return cache.TryGetValue(path, out var found) ? found.Text : null;
    }

    public async Task<string?> LoadAsync(ProcessSnapshot process, CancellationToken cancellationToken)
    {
        var path = process.ExecutablePath;
        if (!process.Identity.IsStable || path is null || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0 ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(path), process.Name, StringComparison.OrdinalIgnoreCase)) return null;
        await pending.WaitAsync(cancellationToken);
        try
        {
            lock (cache)
                if (cache.TryGetValue(path, out var found) && found.Expires > DateTimeOffset.UtcNow) return found.Text;
            var text = await Task.Run(() => ReadDescription(path), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (cache)
            {
                if (!cache.ContainsKey(path))
                {
                    if (cache.Count == Capacity) cache.Remove(order.Dequeue());
                    order.Enqueue(path);
                }
                cache[path] = (text, DateTimeOffset.UtcNow.AddMinutes(text is null ? 1 : 5));
            }
            return text;
        }
        finally { pending.Release(); }
    }

    private static string? ReadDescription(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null || new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) return null;
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            }
            var before = new FileInfo(path);
            var stamp = (before.Length, before.LastWriteTimeUtc);
            var description = Normalize(FileVersionInfo.GetVersionInfo(path).FileDescription);
            before.Refresh();
            return (before.Length, before.LastWriteTimeUtc) == stamp ? description : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static string? Normalize(string? description)
    {
        var text = description?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > 160 || text.Any(c =>
            char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            ? null : text;
    }
}
