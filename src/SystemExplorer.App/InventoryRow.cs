using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace SystemExplorer.App;

public sealed record InventoryRow(string Name, string Detail, string First = "", string Second = "", string Third = "",
    decimal? Number1 = null, decimal? Number2 = null, decimal? Number3 = null, string? FullText = null, object? Value = null, decimal? NameKey = null, string? Key = null)
{
    public InventoryColumns Columns { get; init; } = InventoryColumns.Default;
    public string Description => FullText ?? $"{Name}\n{Detail}\n{First} · {Second} · {Third}";
}

/// <summary>Column widths shared by an inventory's header and every row, so all cells stay aligned when the view resizes.</summary>
public sealed class InventoryColumns : INotifyPropertyChanged
{
    public static InventoryColumns Default { get; } = new();
    private readonly bool[] visible = [true, true, true];
    private double metric = 112;
    public event PropertyChangedEventHandler? PropertyChanged;
    public GridLength FirstWidth => Width(0);
    public GridLength SecondWidth => Width(1);
    public GridLength ThirdWidth => Width(2);
    public double MetricWidth => metric;

    public void Update(double metricWidth, bool first, bool second, bool third)
    {
        if (metric == metricWidth && visible[0] == first && visible[1] == second && visible[2] == third) return;
        metric = metricWidth; visible[0] = first; visible[1] = second; visible[2] = third;
        PropertyChanged?.Invoke(this, new(nameof(FirstWidth)));
        PropertyChanged?.Invoke(this, new(nameof(SecondWidth)));
        PropertyChanged?.Invoke(this, new(nameof(ThirdWidth)));
        PropertyChanged?.Invoke(this, new(nameof(MetricWidth)));
    }

    private GridLength Width(int column) => new(visible[column] ? metric : 0);
}
