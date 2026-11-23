using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace SystemExplorer.App.Views;

internal sealed record PlotSegment(int Style, IReadOnlyList<Point> Points);

internal sealed class ChartPlot
{
    private readonly Canvas canvas;
    private readonly List<Polygon> fills = [];
    private readonly List<Polyline> lines = [];
    private readonly List<Ellipse> dots = [];
    private readonly Ellipse headHalo = new() { Width = 12, Height = 12, Opacity = 0.28, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Ellipse headDot = new() { Width = 6, Height = 6, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Line[] grid = [new(), new(), new()];
    private readonly Line marker = new() { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, Visibility = Visibility.Collapsed };
    private readonly RectangleGeometry clip = new();

    public ChartPlot(Canvas canvas)
    {
        this.canvas = canvas;
        canvas.Clip = clip;
        foreach (var line in grid)
        {
            line.StrokeThickness = 1; line.Opacity = 0.18; line.IsHitTestVisible = false;
            line.StrokeDashArray = new DoubleCollection { 3, 3 };
            canvas.Children.Add(line);
        }
        marker.IsHitTestVisible = false;
        Canvas.SetZIndex(marker, 2);
        canvas.Children.Add(marker);
        Canvas.SetZIndex(headHalo, 3);
        Canvas.SetZIndex(headDot, 4);
        canvas.Children.Add(headHalo);
        canvas.Children.Add(headDot);
    }

    public void Render(IReadOnlyList<PlotSegment> segments, Func<int, Brush> brush, Brush gridBrush, bool sampleDots = false)
    {
        var width = Math.Max(0, canvas.ActualWidth);
        var height = Math.Max(0, canvas.ActualHeight);
        clip.Rect = new Rect(0, 0, width, height);
        for (var i = 0; i < grid.Length; i++)
        {
            grid[i].Stroke = gridBrush;
            grid[i].X1 = 2; grid[i].X2 = Math.Max(2, width - 2);
            grid[i].Y1 = grid[i].Y2 = 2 + i * Math.Max(0, height - 4) / 2;
        }
        var dotIndex = 0;
        Point? latestHead = null;
        Brush? latestBrush = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (i == fills.Count)
            {
                var fill = new Polygon { IsHitTestVisible = false };
                fills.Add(fill); canvas.Children.Add(fill);
            }
            if (i == lines.Count)
            {
                var line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
                lines.Add(line); canvas.Children.Add(line);
            }
            var seriesBrush = brush(segment.Style);
            var fillShape = fills[i];
            var shape = lines[i];
            var showFill = segment.Style == 0 && segment.Points.Count > 1 && height > 4;
            fillShape.Visibility = showFill ? Visibility.Visible : Visibility.Collapsed;
            fillShape.Points.Clear();
            if (showFill)
            {
                fillShape.Fill = CreateAreaFill(seriesBrush);
                var baseline = Math.Max(2, height - 2);
                fillShape.Points.Add(new Point(segment.Points[0].X, baseline));
                foreach (var point in segment.Points) fillShape.Points.Add(point);
                fillShape.Points.Add(new Point(segment.Points[^1].X, baseline));
            }

            shape.Visibility = segment.Points.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            shape.Stroke = seriesBrush;
            shape.StrokeDashArray = segment.Style switch { 1 => new DoubleCollection { 4, 2 }, 2 => new DoubleCollection { 1, 2 }, _ => new DoubleCollection() };
            shape.Points.Clear();
            foreach (var point in segment.Points)
            {
                shape.Points.Add(point);
                if (!sampleDots && segment.Points.Count != 1) continue;
                if (dotIndex == dots.Count)
                {
                    var dot = new Ellipse { Width = 3, Height = 3, IsHitTestVisible = false };
                    dots.Add(dot); canvas.Children.Add(dot);
                }
                var item = dots[dotIndex++];
                item.Visibility = Visibility.Visible; item.Fill = seriesBrush;
                Canvas.SetLeft(item, point.X - 1.5); Canvas.SetTop(item, point.Y - 1.5);
            }
            if (segment.Style == 0 && segment.Points.Count > 0)
            {
                latestHead = segment.Points[^1];
                latestBrush = seriesBrush;
            }
        }
        for (var i = segments.Count; i < fills.Count; i++) fills[i].Visibility = Visibility.Collapsed;
        for (var i = segments.Count; i < lines.Count; i++) lines[i].Visibility = Visibility.Collapsed;
        for (var i = dotIndex; i < dots.Count; i++) dots[i].Visibility = Visibility.Collapsed;

        if (latestHead is { } head && latestBrush is not null && width > 8 && height > 8)
        {
            headHalo.Fill = latestBrush;
            headDot.Fill = latestBrush;
            Canvas.SetLeft(headHalo, head.X - 6);
            Canvas.SetTop(headHalo, head.Y - 6);
            Canvas.SetLeft(headDot, head.X - 3);
            Canvas.SetTop(headDot, head.Y - 3);
            headHalo.Visibility = Visibility.Visible;
            headDot.Visibility = Visibility.Visible;
        }
        else
        {
            headHalo.Visibility = Visibility.Collapsed;
            headDot.Visibility = Visibility.Collapsed;
        }
    }

    private static Brush CreateAreaFill(Brush stroke)
    {
        if (stroke is not SolidColorBrush solid) return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        var c = solid.Color;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(56, c.R, c.G, c.B), Offset = 0.0 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(4, c.R, c.G, c.B), Offset = 1.0 });
        return gradient;
    }

    public void Select(double? x, Brush brush)
    {
        marker.Visibility = x is null ? Visibility.Collapsed : Visibility.Visible;
        if (x is not { } value) return;
        marker.Stroke = brush; marker.X1 = marker.X2 = value;
        marker.Y1 = 0; marker.Y2 = canvas.ActualHeight;
    }
}
