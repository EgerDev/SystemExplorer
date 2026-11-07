namespace SystemExplorer.Core;

public enum InventoryPhase { Loading, Rows, Message }

public sealed class InventoryPresentationState
{
    public InventoryPhase Phase { get; private set; } = InventoryPhase.Loading;
    public string Filter { get; set; } = "";

    public void BeginLoading()
    {
        Phase = InventoryPhase.Loading;
        Filter = "";
    }

    public void ShowRows(bool preserveState)
    {
        if (Phase != InventoryPhase.Loading && !preserveState) Filter = "";
        Phase = InventoryPhase.Rows;
    }

    public void ShowMessage()
    {
        Phase = InventoryPhase.Message;
        Filter = "";
    }

    public bool Matches(string description) => description.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase);

    public string StatusText(string message, int displayed, int total, string header, bool descending, bool live)
    {
        if (Phase != InventoryPhase.Rows) return message;
        var mode = live ? "Live; updates in place" : "Frozen at capture";
        var empty = displayed == 0 ? total == 0 ? " · No observations returned" : " · No matching observations" : "";
        return $"{message}\n{displayed} of {total} observations · {header}: {(descending ? "descending" : "ascending")} · {mode}{empty}";
    }
}
