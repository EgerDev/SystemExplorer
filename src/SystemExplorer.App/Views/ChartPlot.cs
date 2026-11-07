using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace SystemExplorer.App.Views;

internal sealed record PlotSegment(int Style, IReadOnlyList<Point> Points);

internal sealed class ChartPlot
{
    private readonly Canvas canvas;
    private readonly List<Polyline> lines = [];
    private readonly List<Ellipse> dots = [];
    private readonly Line[] grid = [new(), new(), new()];
    private readonly Line marker = new() { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, Visibility = Visibility.Collapsed };
    private readonly RectangleGeometry clip = new();

    public ChartPlot(Canvas canvas)
    {
        this.canvas = canvas;
        canvas.Clip = clip;
        foreach (var line in grid)
        {
            line.StrokeThickness = 1; line.Opacity = 0.2; line.IsHitTestVisible = false;
            canvas.Children.Add(line);
        }
        marker.IsHitTestVisible = false;
        Canvas.SetZIndex(marker, 1);
        canvas.Children.Add(marker);
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
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (i == lines.Count)
            {
                var line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
                lines.Add(line); canvas.Children.Add(line);
            }
            var shape = lines[i];
            shape.Visibility = segment.Points.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            shape.Stroke = brush(segment.Style);
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
                item.Visibility = Visibility.Visible; item.Fill = brush(segment.Style);
                Canvas.SetLeft(item, point.X - 1.5); Canvas.SetTop(item, point.Y - 1.5);
            }
        }
        for (var i = segments.Count; i < lines.Count; i++) lines[i].Visibility = Visibility.Collapsed;
        for (var i = dotIndex; i < dots.Count; i++) dots[i].Visibility = Visibility.Collapsed;
    }

    public void Select(double? x, Brush brush)
    {
        marker.Visibility = x is null ? Visibility.Collapsed : Visibility.Visible;
        if (x is not { } value) return;
        marker.Stroke = brush; marker.X1 = marker.X2 = value;
        marker.Y1 = 0; marker.Y2 = canvas.ActualHeight;
    }
}
