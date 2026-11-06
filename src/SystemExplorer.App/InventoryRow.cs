namespace SystemExplorer.App;

public sealed record InventoryRow(string Name, string Detail, string First = "", string Second = "", string Third = "",
    decimal? Number1 = null, decimal? Number2 = null, decimal? Number3 = null, string? FullText = null, object? Value = null)
{
    public string Description => FullText ?? $"{Name}\n{Detail}\n{First} · {Second} · {Third}";
}
