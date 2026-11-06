using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SystemExplorer.Core;
using Windows.Foundation;
using Windows.System;

namespace SystemExplorer.App.Views;

public sealed partial class ProcessHistoryChart : UserControl
{
    private ProcessHistorySnapshot? history;
    private DateTimeOffset? selectedAt;
    private readonly List<Canvas> plots = [];
    private readonly List<TextBlock> axes = [];
    private readonly List<(Shape Shape, int Style)> swatches = [];
    private readonly TextBlock time = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock readout = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private sealed record Series(string Label, Func<ProcessHistoryPoint, double?> Value, int Style);
    private static readonly Series[][] series =
    [
        [new("CPU (machine normalized)", p => p.CpuPercent, 0)],
        [new("Private commit", p => p.PrivateBytes, 0), new("Working set", p => p.WorkingSetBytes, 1), new("Private WS", p => p.PrivateWorkingSetBytes, 2)],
        [new("Read", p => p.ReadBytesPerSecond, 0), new("Write", p => p.WriteBytesPerSecond, 1)]
    ];

    public ProcessHistoryChart()
    {
        InitializeComponent();
        for (int lane = 0; lane < series.Length; lane++)
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var item in series[lane])
            {
                var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                var swatch = new Line { X1 = 0, X2 = 20, Y1 = 7, Y2 = 7, Width = 20, Height = 14, StrokeThickness = 2 };
                SetStyle(swatch, item.Style);
                swatches.Add((swatch, item.Style));
                entry.Children.Add(swatch);
                entry.Children.Add(new TextBlock { Text = item.Label, FontSize = 12 });
                legend.Children.Add(entry);
            }
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(legend);
            Lanes.Children.Add(heading);
            var axis = new TextBlock { FontSize = 12 };
            axes.Add(axis);
            Grid.SetColumn(axis, 1);
            heading.Children.Add(axis);
            var plot = new Canvas { Height = 40, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            plot.SizeChanged += (_, _) => Draw();
            plot.PointerMoved += PlotPointerMoved;
            plot.PointerPressed += (_, _) => Focus(FocusState.Pointer);
            plots.Add(plot);
            Lanes.Children.Add(plot);
        }
        Lanes.Children.Add(time);
        Lanes.Children.Add(readout);
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Update(ProcessHistorySnapshot? next)
    {
        if (history?.Identity != next?.Identity) selectedAt = null;
        history = next;
        var points = history?.Points;
        if (points is { Count: > 0 })
        {
            if (selectedAt is null) selectedAt = points[^1].At;
            else if (!points.Any(p => p.At == selectedAt))
                selectedAt = points.MinBy(p => Math.Abs((p.At - selectedAt.Value).Ticks))!.At;
        }
        Draw();
    }

    private Brush Brush(int style) => style switch { 0 => Accent.Foreground, 1 => Ink.Foreground, _ => Secondary.Foreground };
    private void SetStyle(Shape shape, int style)
    {
        shape.Stroke = Brush(style);
        shape.StrokeDashArray = style switch { 1 => new DoubleCollection { 4, 2 }, 2 => new DoubleCollection { 1, 2 }, _ => new DoubleCollection() };
    }
    private static double? Valid(ProcessHistoryPoint p, Series s) => p.Observed && s.Value(p) is { } value && double.IsFinite(value) && value >= 0 ? value : null;
    private static string Value(int lane, double? value) => value is not { } v ? "Unavailable" : lane switch
    {
        0 => Display.Percent(v), 1 => DiagnosticData.Bytes((ulong)v), _ => DiagnosticData.Rate(v)
    };
    private double X(ProcessHistoryPoint p, double width)
    {
        var points = history!.Points;
        var span = (points[^1].At - points[0].At).TotalSeconds;
        return 2 + (span <= 0 ? 0 : (p.At - points[0].At).TotalSeconds / span) * Math.Max(0, width - 4);
    }
    private void Draw()
    {
        foreach (var swatch in swatches) SetStyle(swatch.Shape, swatch.Style);
        var points = history?.Points ?? [];
        for (int lane = 0; lane < plots.Count; lane++)
        {
            var plot = plots[lane];
            plot.Children.Clear();
            var values = points.SelectMany(p => series[lane].Select(s => Valid(p, s))).OfType<double>().ToArray();
            double max = values.Length == 0 ? 0 : values.Max();
            axes[lane].Text = points.Count == 0 ? "No samples" : values.Length == 0 ? "Unavailable" : $"0 – {Value(lane, max)}";
            if (plot.ActualWidth <= 0) continue;
            foreach (var s in series[lane])
            {
                Polyline? line = null;
                foreach (var p in points)
                {
                    if (p.BreakBefore || !p.Observed || Valid(p, s) is null) line = null;
                    if (Valid(p, s) is not { } v) continue;
                    var point = new Point(X(p, plot.ActualWidth), 2 + (max <= 0 ? 1 : 1 - v / max) * Math.Max(0, plot.ActualHeight - 4));
                    if (line is null)
                    {
                        line = new Polyline { StrokeThickness = 2 };
                        SetStyle(line, s.Style);
                        plot.Children.Add(line);
                    }
                    line.Points.Add(point);
                    var dot = new Ellipse { Width = 3, Height = 3, Fill = Brush(s.Style) };
                    Canvas.SetLeft(dot, point.X - 1.5); Canvas.SetTop(dot, point.Y - 1.5);
                    plot.Children.Add(dot);
                }
            }
            if (points.FirstOrDefault(p => p.At == selectedAt) is { } selected)
            {
                var x = X(selected, plot.ActualWidth);
                plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = plot.ActualHeight, Stroke = Ink.Foreground, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 } });
            }
        }
        time.Text = points.Count == 0 ? "Time: No samples" : $"Time: {points[0].At.LocalDateTime:HH:mm:ss} → {points[^1].At.LocalDateTime:HH:mm:ss} (elapsed time; gaps not connected)";
        var sample = points.FirstOrDefault(p => p.At == selectedAt);
        readout.Text = sample is null ? "No samples. Select a stable process to retain history." :
            $"{sample.At.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} · {(sample.Observed ? "Observed" : "Not observed (not confirmed exit)")}{(sample.BreakBefore ? " · Gap before sample" : "")}\n" +
            string.Join("\n", series.Select((items, lane) => string.Join(" · ", items.Select(s =>
                $"{s.Label}: {(!sample.Observed ? "Not observed" : Valid(sample, s) is { } v ? lane == 0 ? $"{v:G17}%" : lane == 1 ? $"{v:G17} B" : $"{v:G17} B/s" : "Unavailable")}")))) +
            "\nLeft/Right: sample · Home/End: endpoints. Private commit is not physical RAM; I/O is generic process I/O, not disk/network attribution.";
        AutomationProperties.SetName(this, readout.Text);
    }

    private void PlotPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (history?.Points is not { Count: > 0 } points || sender is not Canvas plot) return;
        var x = args.GetCurrentPoint(plot).Position.X;
        selectedAt = points.MinBy(p => Math.Abs(X(p, plot.ActualWidth) - x))!.At;
        Draw();
    }
    private void ChartKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (history?.Points is not { Count: > 0 } points) return;
        int index = points.ToList().FindIndex(p => p.At == selectedAt);
        int next = args.Key switch { VirtualKey.Left => index - 1, VirtualKey.Right => index + 1, VirtualKey.Home => 0, VirtualKey.End => points.Count - 1, _ => -2 };
        if (next == -2) return;
        selectedAt = points[Math.Clamp(next, 0, points.Count - 1)].At;
        args.Handled = true;
        Draw();
    }
}
