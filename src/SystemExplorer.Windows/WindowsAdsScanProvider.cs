using System.Runtime.InteropServices;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsAdsScanProvider : IAdsScanProvider
{
    public Task<AdsScanResult> ScanAsync(AdsScanOptions options, CancellationToken cancellationToken)
    {
        if (options.MaxFiles is < 1 or > 50000 || options.MaxDepth is < 0 or > 64 || options.MaxResults is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(options), "Bounds: 1..50000 objects, depth 0..64, 1..5000 streams.");
        return Task.Run(() => Scan(options, cancellationToken), cancellationToken);
    }

    private static AdsScanResult Scan(AdsScanOptions options, CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var rows = new List<AdsObservation>();
        int visited = 0, denied = 0, skipped = 0, failed = 0;
        bool truncated = false;
        void Error(int error)
        {
            if (error == 5) denied++;
            else if (error is not (2 or 3 or 18 or 38)) failed++;
        }
        void Streams(string path)
        {
            using var search = HiddenNativeMethods.FindFirstStreamW(path, 0, out var data, 0);
            if (search.IsInvalid) { Error(Marshal.GetLastWin32Error()); return; }
            int count = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                if (++count > 5001) { truncated = true; return; }
                if (data.Name == "::$DATA") continue;
                if (rows.Count >= options.MaxResults) { truncated = true; return; }
                rows.Add(new(path, data.Name, data.Size));
            } while (HiddenNativeMethods.FindNextStreamW(search, out data));
            Error(Marshal.GetLastWin32Error());
        }
        void Visit(string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (visited >= options.MaxFiles) { truncated = true; return; }
            visited++;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; return; }
                Streams(path);
                if (truncated || (attributes & FileAttributes.Directory) == 0) return;
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (depth >= options.MaxDepth) { truncated = true; return; }
                    Visit(child, depth + 1);
                    if (truncated) return;
                }
            }
            catch (UnauthorizedAccessException) { denied++; }
            catch (IOException) { failed++; }
        }
        Visit(Path.GetFullPath(options.Folder), 0);
        token.ThrowIfCancellationRequested();
        return new(at, truncated ? InspectionStatus.Truncated : denied > 0 ? InspectionStatus.AccessDenied : failed > 0 ? InspectionStatus.Partial : InspectionStatus.Available,
            $"Stream names and sizes only; contents never read. Objects={visited}; denied={denied}; failed={failed}; skipped reparse points={skipped}. Depth includes the selected root at zero.",
            visited, denied, skipped, new(rows, options.MaxResults, truncated));
    }
}
