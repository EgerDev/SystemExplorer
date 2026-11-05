using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using global::Windows.Storage;
using global::Windows.Storage.FileProperties;

namespace SystemExplorer.App;

public sealed class ProcessIcons
{
    private const int Capacity = 128;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("Create ProcessIcons on the window UI thread.");
    private readonly Dictionary<string, (ImageSource? Image, DateTimeOffset Expires)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> order = new();
    private readonly SemaphoreSlim pending = new(2);

    public async Task<ImageSource?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Load icons on the owning window UI thread.");
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0 ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;

        await pending.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(path, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
                return entry.Image;
            ImageSource? image = null;
            if (await Task.Run(() => IsLocalRegularFile(path), cancellationToken))
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.ResizeThumbnail).AsTask(cancellationToken);
                if (thumbnail is not null && thumbnail.Size is > 0 and <= 262144)
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 32, DecodePixelHeight = 32 };
                    await bitmap.SetSourceAsync(thumbnail).AsTask(cancellationToken);
                    image = bitmap;
                }
            }
            Store(path, image);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Store(path, null);
            return null;
        }
        finally
        {
            pending.Release();
        }
    }

    private void Store(string path, ImageSource? image)
    {
        if (!cache.ContainsKey(path))
        {
            if (cache.Count == Capacity) cache.Remove(order.Dequeue());
            order.Enqueue(path);
        }
        cache[path] = (image, DateTimeOffset.UtcNow.AddMinutes(image is null ? 1 : 5));
    }

    private static bool IsLocalRegularFile(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) return false;
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return (File.GetAttributes(path) & System.IO.FileAttributes.Directory) == 0;
    }
}
