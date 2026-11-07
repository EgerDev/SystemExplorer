namespace SystemExplorer.Core;

public readonly record struct RowViewportAnchor(string Key, double ContentTop, double Offset, double Y);

public static class RowViewportAnchoring
{
    public static RowViewportAnchor? Capture<T>(IEnumerable<T> rows, Func<T, string> key, Func<T, double> height,
        string anchorKey, double offset, double y)
    {
        if (!double.IsFinite(offset) || offset < 0 || !double.IsFinite(y)) return null;
        return ContentTop(rows, key, height, anchorKey) is { } top ? new(anchorKey, top, offset, y) : null;
    }

    public static double? RestoreOffset<T>(RowViewportAnchor anchor, IEnumerable<T> rows, Func<T, string> key,
        Func<T, double> height, double extent)
    {
        if (!double.IsFinite(extent) || extent < 0) return null;
        return ContentTop(rows, key, height, anchor.Key) is { } top
            ? Math.Clamp(anchor.Offset + top - anchor.ContentTop, 0, extent) : null;
    }

    private static double? ContentTop<T>(IEnumerable<T> rows, Func<T, string> key, Func<T, double> height, string anchorKey)
    {
        var top = 0d;
        foreach (var row in rows)
        {
            if (key(row) == anchorKey) return top;
            var size = height(row);
            if (!double.IsFinite(size) || size <= 0 || !double.IsFinite(top + size)) return null;
            top += size;
        }
        return null;
    }
}
