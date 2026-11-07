using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed partial class InventoryView : UserControl
{
    private IReadOnlyList<InventoryRow> source = [];
    private readonly ObservableCollection<InventoryRow> liveRows = new();
    private string status = "Collecting a bounded read-only observation…";
    private int sort;
    private bool descending;
    private bool ready;
    private Action<int>? targetChanged;
    public event Action<InventoryRow>? ActionRequested;
    public event Action? BackRequested;
    public InventoryView() { InitializeComponent(); ready = true; }

    public void ShowRows(string message, IEnumerable<InventoryRow> rows, string name, string first, string second, string third, string? action = null, bool back = false, int initialSort = 0, bool reverse = false, bool preserveState = false)
    {
        source = rows.ToArray(); status = message;
        if (!preserveState) { sort = initialSort; descending = reverse; }
        NameHeader.Content = name; FirstHeader.Content = first; SecondHeader.Content = second; ThirdHeader.Content = third;
        var labels = new[] { first, second, third };
        var buttons = new[] { FirstHeader, SecondHeader, ThirdHeader };
        for (var i = 0; i < labels.Length; i++)
        {
            Headers.ColumnDefinitions[i + 1].Width = new GridLength(labels[i].Length == 0 ? 0 : 112);
            buttons[i].Visibility = labels[i].Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        ActionButton.Content = action; ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        if (!preserveState) Search.Text = "";
        Rebuild(preserveState);
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
    private void Rebuild(bool preserveState = false)
    {
        var filtered = source.Where(r => r.Description.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        decimal? Number(InventoryRow row) => sort switch { 0 => row.NameKey, 1 => row.Number1, 2 => row.Number2, _ => row.Number3 };
        string Text(InventoryRow row) => sort switch { 1 => row.First, 2 => row.Second, 3 => row.Third, _ => row.Name };
        var numeric = source.Any(r => Number(r) is not null);
        var ordered = InventoryOrdering.Order(filtered, Text, Number, numeric, descending);
        var displayed = ordered.ToArray();
        if (source.Count > 0 && source.All(r => r.Key is not null))
        {
            var selectedKey = preserveState ? (Rows.SelectedItem as InventoryRow)?.Key : null;
            var scroll = preserveState ? FindScroll(Rows) : null;
            var offset = scroll?.VerticalOffset ?? 0;
            var anchor = liveRows.Select(row => (Row: row, Container: Rows.ContainerFromItem(row) as FrameworkElement))
                .Where(item => item.Container is not null)
                .Select(item => (item.Row.Key, Y: item.Container!.TransformToVisual(Rows).TransformPoint(default).Y, Height: item.Container.ActualHeight))
                .FirstOrDefault(item => item.Y + item.Height > 0);
            if (!ReferenceEquals(Rows.ItemsSource, liveRows)) Rows.ItemsSource = liveRows;
            KeyedRows.Reconcile(liveRows, displayed, row => row.Key!);
            Rows.SelectedItem = selectedKey is null ? null : liveRows.FirstOrDefault(row => row.Key == selectedKey);
            if (scroll is not null)
            {
                Rows.UpdateLayout();
                var retained = liveRows.FirstOrDefault(row => row.Key == anchor.Key);
                var container = retained is null ? null : Rows.ContainerFromItem(retained) as FrameworkElement;
                var adjustment = container is null ? 0 : container.TransformToVisual(Rows).TransformPoint(default).Y - anchor.Y;
                scroll.ChangeView(null, Math.Max(0, container is null ? offset : scroll.VerticalOffset + adjustment), null, true);
            }
        }
        else Rows.ItemsSource = displayed;
        UpdateSelection();
        var header = new[] { NameHeader, FirstHeader, SecondHeader, ThirdHeader }[sort].Content;
        Status.Text = $"{status}\n{displayed.Length} of {source.Count} observations · {header}: {(descending ? "descending" : "ascending")}{(displayed.Length == 0 ? source.Count == 0 ? " · No observations returned" : " · No matching observations" : "")}";
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelection();
    private void UpdateSelection()
    {
        Detail.Text = Rows.SelectedItem is InventoryRow row ? row.Description : "Select an observation for full, copyable details.";
        ActionButton.IsEnabled = Rows.SelectedItem is InventoryRow { Value: not null };
    }
    private void ActionClicked(object sender, RoutedEventArgs args) { if (Rows.SelectedItem is InventoryRow row) ActionRequested?.Invoke(row); }
    private void BackClicked(object sender, RoutedEventArgs args) => BackRequested?.Invoke();
}
