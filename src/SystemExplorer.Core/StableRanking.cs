namespace SystemExplorer.Core;

public static class StableRanking
{
    public static IReadOnlyList<T> Take<T, TKey>(IEnumerable<T> items, Func<T, TKey> key,
        Func<T, double?> value, IReadOnlyDictionary<TKey, int> previousRanks,
        Func<double, double> tolerance, IComparer<T> tieBreaker, int count) where TKey : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var remaining = items.Select(item => (Item: item, Value: value(item),
            Rank: previousRanks.TryGetValue(key(item), out var rank) ? rank : int.MaxValue)).ToList();
        if (remaining.Any(entry => entry.Value is { } number && !double.IsFinite(number)))
            throw new ArgumentException("Ranking values must be finite or null.", nameof(items));
        var result = new List<T>(Math.Min(count, remaining.Count));
        while (result.Count < count && remaining.Count > 0)
        {
            var maximum = remaining.Max(entry => entry.Value);
            var margin = maximum is { } max ? tolerance(max) : 0;
            if (!double.IsFinite(margin) || margin < 0)
                throw new ArgumentException("Tolerance must be finite and non-negative.", nameof(tolerance));
            var best = -1;
            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                if (maximum is { } m && (candidate.Value is null || candidate.Value < m - margin)) continue;
                if (best < 0) { best = i; continue; }
                var incumbent = remaining[best];
                var comparison = candidate.Rank.CompareTo(incumbent.Rank);
                if (comparison == 0) comparison = Nullable.Compare(incumbent.Value, candidate.Value);
                if (comparison == 0) comparison = tieBreaker.Compare(candidate.Item, incumbent.Item);
                if (comparison < 0) best = i;
            }
            result.Add(remaining[best].Item);
            remaining.RemoveAt(best);
        }
        return result;
    }
}
