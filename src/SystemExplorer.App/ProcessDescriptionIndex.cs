using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed class ProcessDescriptionIndex
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);
    private readonly Func<ProcessSnapshot, CancellationToken, Task<string?>> reader;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<Key, CachedDescription> cache = new(KeyComparer.Instance);
    private Dictionary<Key, ProcessSnapshot> active = new(KeyComparer.Instance);
    private Task<bool>? refreshTask;

    public ProcessDescriptionIndex() : this(new ApplicationDescriptions().LoadAsync, TimeProvider.System) { }

    internal ProcessDescriptionIndex(Func<ProcessSnapshot, CancellationToken, Task<string?>> reader, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
        this.clock = clock ?? TimeProvider.System;
    }

    public void UpdateProcesses(IReadOnlyList<ProcessSnapshot> processes)
    {
        var next = new Dictionary<Key, ProcessSnapshot>(KeyComparer.Instance);
        foreach (var process in processes)
            if (TryCreateKey(process, out var key))
                next[key] = process;

        lock (gate)
        {
            active = next;
            foreach (var key in cache.Keys.Where(key => !active.ContainsKey(key)).ToArray())
                cache.Remove(key);
        }
    }

    public string? GetCached(ProcessSnapshot process)
    {
        if (!TryCreateKey(process, out var key)) return null;
        lock (gate)
        {
            return active.ContainsKey(key) && cache.TryGetValue(key, out var found) &&
                found.Expires > clock.GetUtcNow() ? found.Text : null;
        }
    }

    public Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (refreshTask is { IsCompleted: false }) return refreshTask;
            refreshTask = RefreshCoreAsync(cancellationToken);
            return refreshTask;
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KeyValuePair<Key, ProcessSnapshot>[] pending;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            pending = active.Where(pair => !cache.TryGetValue(pair.Key, out var cached) || cached.Expires <= now).ToArray();
        }

        var changed = false;
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await reader(item.Value, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!active.TryGetValue(item.Key, out var current) || !MatchesKey(current, item.Key)) continue;
                var now = clock.GetUtcNow();
                var previous = cache.TryGetValue(item.Key, out var found) && found.Expires > now ? found.Text : null;
                cache[item.Key] = new(text, now.Add(text is null ? NegativeTtl : PositiveTtl));
                if (!string.Equals(previous, text, StringComparison.Ordinal)) changed = true;
            }
        }
        return changed;
    }

    private static bool TryCreateKey(ProcessSnapshot process, out Key key)
    {
        var path = process.ExecutablePath;
        if (!process.Identity.IsStable || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(process.Name) ||
            !string.Equals(Path.GetFileName(path), process.Name, StringComparison.OrdinalIgnoreCase))
        {
            key = default;
            return false;
        }
        key = new(process.Identity, path, process.Name);
        return true;
    }

    private static bool MatchesKey(ProcessSnapshot process, Key key) =>
        process.Identity == key.Identity &&
        string.Equals(process.ExecutablePath, key.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(process.Name, key.Name, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Key(ProcessIdentity Identity, string Path, string Name);
    private readonly record struct CachedDescription(string? Text, DateTimeOffset Expires);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals(Key x, Key y) =>
            x.Identity == y.Identity &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode(Key key) =>
            HashCode.Combine(key.Identity,
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
