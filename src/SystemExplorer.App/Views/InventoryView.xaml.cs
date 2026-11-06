using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SystemExplorer.App.Views;

public sealed partial class InventoryView : UserControl
{
    private IReadOnlyList<InventoryRow> source = [];
    private string status = "Collecting a bounded read-only observation…";
    private int sort;
    private bool descending;
    private bool ready;
    private Action<int>? targetChanged;
    public event Action<InventoryRow>? ActionRequested;
    public event Action? BackRequested;
    public InventoryView() { InitializeComponent(); ready = true; }

    public void ShowRows(string message, IEnumerable<InventoryRow> rows, string name, string first, string second, string third, string? action = null, bool back = false)
    {
        source = rows.ToArray(); status = message; sort = 0; descending = false;
        NameHeader.Content = name; FirstHeader.Content = first; SecondHeader.Content = second; ThirdHeader.Content = third;
        ActionButton.Content = action; ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        Search.Text = "";
        Rebuild();
    }
    public void ShowMessage(string message, bool back = false) => ShowRows(message, [], "Observation", "", "", "", back: back);
    public void SetTargets(IReadOnlyList<string> labels, Action<int> changed)
    {
        targetChanged = changed; Targets.ItemsSource = labels;
        Targets.Visibility = Visibility.Visible; Targets.SelectedIndex = labels.Count > 0 ? 0 : -1;
    }
    private void TargetChanged(object sender, SelectionChangedEventArgs args) { if (ready && Targets.SelectedIndex >= 0) targetChanged?.Invoke(Targets.SelectedIndex); }
    private void FilterChanged(object sender, TextChangedEventArgs args) { if (ready) Rebuild(); }
    private void SortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var column)) return;
        descending = column == sort && !descending; sort = column; Rebuild();
    }
    private void Rebuild()
    {
        var filtered = source.Where(r => r.Description.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        decimal? Number(InventoryRow row) => sort switch { 1 => row.Number1, 2 => row.Number2, _ => row.Number3 };
        var ordered = sort == 0 ? descending ? filtered.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : descending ? filtered.OrderBy(r => Number(r) is null).ThenByDescending(Number) : filtered.OrderBy(r => Number(r) is null).ThenBy(Number);
        var displayed = ordered.ToArray();
        Rows.ItemsSource = displayed;
        Detail.Text = "Select an observation for full, copyable details."; ActionButton.IsEnabled = false;
        Status.Text = $"{status}\n{displayed.Length} of {source.Count} observations{(displayed.Length == 0 ? " · No matching observations" : "")}";
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        Detail.Text = Rows.SelectedItem is InventoryRow row ? row.Description : "Select an observation for full, copyable details.";
        ActionButton.IsEnabled = Rows.SelectedItem is InventoryRow { Value: not null };
    }
    private void ActionClicked(object sender, RoutedEventArgs args) { if (Rows.SelectedItem is InventoryRow row) ActionRequested?.Invoke(row); }
    private void BackClicked(object sender, RoutedEventArgs args) => BackRequested?.Invoke();
}
