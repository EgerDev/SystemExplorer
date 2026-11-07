using System.Collections.ObjectModel;

namespace SystemExplorer.Core;

public static class KeyedRows
{
    public static bool HasUniqueKeys<T, TKey>(IEnumerable<T> rows, Func<T, TKey?> key) where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        foreach (var row in rows)
            if (key(row) is not { } value || !seen.Add(value)) return false;
        return true;
    }

    public static void Reconcile<T, TKey>(ObservableCollection<T> rows, IReadOnlyList<T> desired, Func<T, TKey> key) where TKey : notnull
    {
        var wanted = desired.ToDictionary(key);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!wanted.ContainsKey(key(rows[i]))) rows.RemoveAt(i);
        var existing = rows.ToDictionary(key);
        for (var i = 0; i < desired.Count; i++)
        {
            var next = desired[i];
            if (existing.TryGetValue(key(next), out var current))
            {
                var index = rows.IndexOf(current);
                if (index != i) rows.Move(index, i);
                if (!EqualityComparer<T>.Default.Equals(current, next)) rows[i] = next;
            }
            else rows.Insert(i, next);
        }
    }
}
