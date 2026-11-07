using SystemExplorer.Core;

namespace SystemExplorer.App;

internal sealed class HiddenDeveloperContext
{
    private readonly Dictionary<string, string?> directories = new(StringComparer.OrdinalIgnoreCase);

    public string? Inspect(string path, CancellationToken token)
    {
        var pathContext = SuspiciousLeadRules.DeveloperPathContext(path);
        var directory = Path.GetDirectoryName(path);
        for (int depth = 0; depth < 6 && directory is not null; depth++, directory = Path.GetDirectoryName(directory))
        {
            token.ThrowIfCancellationRequested();
            if (!directories.TryGetValue(directory, out var marker))
            {
                marker = FindMarker(directory, token);
                directories[directory] = marker;
            }
            if (marker is not null) return string.Join("; ", new[] { pathContext, marker }.Where(s => s is not null));
        }
        return pathContext;
    }

    private static string? FindMarker(string directory, CancellationToken token)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            foreach (var marker in new[] { ".git", "package.json" })
                if (File.Exists(Path.Combine(directory, marker)) || marker == ".git" && Directory.Exists(Path.Combine(directory, marker)))
                    return $"Project marker {marker} in {directory}";
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(256))
            {
                token.ThrowIfCancellationRequested();
                if (entry.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && File.Exists(entry)) return $"Project marker {Path.GetFileName(entry)} in {directory}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
}
