using System.Collections.ObjectModel;

namespace SystemExplorer.Core;

public readonly record struct ReconciliationCounts(int Removed, int Moved, int Inserted);

public static class MinimalKeyedRows
{
    public static ReconciliationCounts Reconcile<T, TDesired, TKey>(ObservableCollection<T> rows,
        IReadOnlyList<TDesired> desired, Func<T, TKey> rowKey, Func<TDesired, TKey> desiredKey,
        Func<TDesired, T> create, Action<T, TDesired> update) where TKey : notnull
    {
        var wanted = desired.ToDictionary(desiredKey);
        var removed = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!wanted.ContainsKey(rowKey(rows[i]))) { rows.RemoveAt(i); removed++; }
        var positions = rows.Select((row, index) => (Key: rowKey(row), Index: index))
            .ToDictionary(pair => pair.Key, pair => pair.Index);
        var retained = desired.Where(item => positions.ContainsKey(desiredKey(item))).ToArray();
        var tails = new int[retained.Length];
        var previous = new int[retained.Length];
        var length = 0;
        for (var i = 0; i < retained.Length; i++)
        {
            var index = positions[desiredKey(retained[i])];
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (positions[desiredKey(retained[tails[middle]])] < index) low = middle + 1;
                else high = middle;
            }
            previous[i] = low == 0 ? -1 : tails[low - 1];
            tails[low] = i;
            if (low == length) length++;
        }
        var stationary = new bool[retained.Length];
        for (var i = length == 0 ? -1 : tails[length - 1]; i >= 0; i = previous[i]) stationary[i] = true;
        var moved = 0;
        for (var i = retained.Length - 1; i >= 0; i--)
        {
            var key = desiredKey(retained[i]);
            var from = positions[key];
            update(rows[from], retained[i]);
            if (stationary[i]) continue;
            var to = i == retained.Length - 1 ? rows.Count - 1 : positions[desiredKey(retained[i + 1])];
            if (i != retained.Length - 1 && from < to) to--;
            rows.Move(from, to);
            moved++;
            for (var j = Math.Min(from, to); j <= Math.Max(from, to); j++) positions[rowKey(rows[j])] = j;
        }
        var inserted = 0;
        for (var i = 0; i < desired.Count; i++)
            if (!positions.ContainsKey(desiredKey(desired[i]))) { rows.Insert(i, create(desired[i])); inserted++; }
        return new(removed, moved, inserted);
    }
}
