namespace SystemExplorer.Core;

public static class InventoryOrdering
{
    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> rows, Func<T, string> text, Func<T, decimal?> number, bool numeric, bool descending)
        => !numeric ? descending ? rows.OrderByDescending(text, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(text, StringComparer.OrdinalIgnoreCase)
            : descending ? rows.OrderBy(r => number(r) is null).ThenByDescending(number) : rows.OrderBy(r => number(r) is null).ThenBy(number);
}
