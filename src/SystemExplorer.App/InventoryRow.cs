namespace SystemExplorer.App;

public sealed record InventoryRow(string Name, string Detail, string First = "", string Second = "", string Third = "",
    decimal? Number1 = null, decimal? Number2 = null, decimal? Number3 = null, string? FullText = null, object? Value = null, decimal? NameKey = null, string? Key = null)
{
    public Microsoft.UI.Xaml.GridLength FirstWidth => new(First.Length == 0 ? 0 : 112);
    public Microsoft.UI.Xaml.GridLength SecondWidth => new(Second.Length == 0 ? 0 : 112);
    public Microsoft.UI.Xaml.GridLength ThirdWidth => new(Third.Length == 0 ? 0 : 112);
    public string Description => FullText ?? $"{Name}\n{Detail}\n{First} · {Second} · {Third}";
}
