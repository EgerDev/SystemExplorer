using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed partial class InventoryView : UserControl
{
    private const string FrozenFilter = "Filter this frozen inventory…";
    private const string LiveFilter = "Filter these live observations…";
    private const string NoSelection = "Select an observation for full, copyable details.";
    private IReadOnlyList<InventoryRow> source = [];
    private readonly ObservableCollection<InventoryRow> liveRows = new();
    private readonly InventoryColumns columns = new();
    private readonly string[] labels = ["Name", "", "", ""];
    private string status = "Collecting a bounded read-only observation…";
    private int sort;
    private bool descending;
    private bool live;
    private bool messageOnly;
    private bool ready;
    private Action<int>? targetChanged;
    public event Action<InventoryRow>? ActionRequested;
    public event Action? BackRequested;
    public InventoryView() { InitializeComponent(); ready = true; }

    /// <param name="live">True when the caller refreshes these rows in place; false (default) for a frozen capture.</param>
    public void ShowRows(string message, IEnumerable<InventoryRow> rows, string name, string first, string second, string third, string? action = null, bool back = false, int initialSort = 0, bool reverse = false, bool preserveState = false, bool live = false)
    {
        source = rows.Select(row => ReferenceEquals(row.Columns, columns) ? row : row with { Columns = columns }).ToArray();
        status = message; this.live = live; messageOnly = false;
        if (!preserveState) { sort = initialSort; descending = reverse; }
        labels[0] = name; labels[1] = first; labels[2] = second; labels[3] = third;
        var headers = new[] { NameHeader, FirstHeader, SecondHeader, ThirdHeader };
        for (var i = 0; i < headers.Length; i++)
        {
            headers[i].Content = new TextBlock { Text = labels[i], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            ToolTipService.SetToolTip(headers[i], labels[i].Length == 0 ? null : $"Sort by {labels[i]}");
            headers[i].Visibility = labels[i].Length == 0 && i > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateColumns();
        Search.PlaceholderText = live ? LiveFilter : FrozenFilter;
        Search.Visibility = Headers.Visibility = Rows.Visibility = DetailScroll.Visibility = Visibility.Visible;
        ActionButton.Content = action; ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        if (!preserveState) Search.Text = "";
        Rebuild(preserveState);
    }

    /// <summary>Shows only a status message (loading, cancelled, unavailable) without an empty table or observation counts.</summary>
    public void ShowMessage(string message, bool back = false)
    {
        source = []; status = message; messageOnly = true; sort = 0; descending = false;
        Rows.ItemsSource = null; liveRows.Clear();
        Search.Text = "";
        Search.Visibility = Headers.Visibility = Rows.Visibility = DetailScroll.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
        Back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = message;
    }

    /// <summary>Replaces the status line while keeping rows, filter, sort, selection and scroll untouched.</summary>
    public void UpdateStatus(string message)
    {
        status = message;
        if (messageOnly) Status.Text = message; else UpdateStatusText(Rows.Items.Count);
    }

    public void SetTargets(IReadOnlyList<string> labels, Action<int> changed)
    {
        targetChanged = changed; Targets.ItemsSource = labels;
        Targets.Visibility = Visibility.Visible; Targets.SelectedIndex = labels.Count > 0 ? 0 : -1;
    }
    private void TargetChanged(object sender, SelectionChangedEventArgs args) { if (ready && Targets.SelectedIndex >= 0) targetChanged?.Invoke(Targets.SelectedIndex); }
    private void FilterChanged(object sender, TextChangedEventArgs args) { if (ready && !messageOnly) Rebuild(true); }
    private void SortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var column)) return;
        descending = column == sort && !descending; sort = column; Rebuild(true);
    }
    private void HeadersSizeChanged(object sender, SizeChangedEventArgs args) => UpdateColumns();
    private void UpdateColumns()
    {
        var visible = labels.Skip(1).Select(label => label.Length > 0).ToArray();
        var width = InventoryLayout.MetricWidth(Headers.ActualWidth - Headers.Padding.Left - Headers.Padding.Right, visible.Count(v => v));
        if (width <= 0) width = 112;
        columns.Update(width, visible[0], visible[1], visible[2]);
        for (var i = 0; i < visible.Length; i++)
            Headers.ColumnDefinitions[i + 1].Width = new GridLength(visible[i] ? width : 0);
    }
    private void Rebuild(bool preserveState = false)
    {
        var filtered = source.Where(r => r.Description.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        decimal? Number(InventoryRow row) => sort switch { 0 => row.NameKey, 1 => row.Number1, 2 => row.Number2, _ => row.Number3 };
        string Text(InventoryRow row) => sort switch { 1 => row.First, 2 => row.Second, 3 => row.Third, _ => row.Name };
        var numeric = source.Any(r => Number(r) is not null);
        var ordered = InventoryOrdering.Order(filtered, Text, Number, numeric, descending);
        var displayed = ordered.ToArray();
        if (source.Count > 0 && KeyedRows.HasUniqueKeys(source, row => row.Key))
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
        else
        {
            var selected = preserveState ? Rows.SelectedItem as InventoryRow : null;
            liveRows.Clear();
            Rows.ItemsSource = displayed;
            var retained = selected is null ? null : displayed.FirstOrDefault(row => ReferenceEquals(row, selected)) ?? displayed.FirstOrDefault(row => row == selected);
            if (retained is not null) { Rows.SelectedItem = retained; Rows.ScrollIntoView(retained); }
        }
        UpdateSelection();
        UpdateStatusText(displayed.Length);
    }
    private void UpdateStatusText(int displayed)
    {
        var header = labels[sort].Length == 0 ? labels[0] : labels[sort];
        var mode = live ? "Live; updates in place" : "Frozen at capture";
        var empty = displayed == 0 ? source.Count == 0 ? " · No observations returned" : " · No matching observations" : "";
        Status.Text = $"{status}\n{displayed} of {source.Count} observations · {header}: {(descending ? "descending" : "ascending")} · {mode}{empty}";
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
        Detail.Text = Rows.SelectedItem is InventoryRow row ? row.Description : NoSelection;
        ActionButton.IsEnabled = Rows.SelectedItem is InventoryRow { Value: not null };
    }
    private void ActionClicked(object sender, RoutedEventArgs args) { if (Rows.SelectedItem is InventoryRow row) ActionRequested?.Invoke(row); }
    private void BackClicked(object sender, RoutedEventArgs args) => BackRequested?.Invoke();
}
