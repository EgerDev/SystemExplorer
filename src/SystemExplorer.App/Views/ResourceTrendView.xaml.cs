using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace SystemExplorer.App.Views;

public sealed partial class ResourceTrendView : UserControl
{
    private ResourceTrend? trend;

    public ResourceTrendView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Update(ResourceTrend? next, string label, bool rate = false)
    {
        trend = next;
        string Value(double value) => rate ? Display.Rate(value) : $"{value:F1}%";
        var description = next is { SampleCount: > 0, MinimumObserved: { } min, MaximumObserved: { } max }
            ? $"{label} history: {next.SampleCount} observed samples, range {Value(min)} to {Value(max)}. Axis 0 to {Value(next.Maximum)}. Gaps are not connected."
            : $"{label}: no measured history available.";
        HistoryDescription.Text = next is { SampleCount: > 1 } ? "" : next is { SampleCount: 1 } ? "1 observed sample" : "No measured history";
        AutomationProperties.SetName(HistoryDescription, description);
        AutomationProperties.SetName(this, description);
        ToolTipService.SetToolTip(this, description);
        Draw();
    }

    private void PlotSizeChanged(object sender, SizeChangedEventArgs args) => Draw();

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void Draw()
    {
        Plot.Children.Clear();
        if (trend is null || Plot.ActualWidth <= 0 || Plot.ActualHeight <= 0) return;
        var width = Math.Max(0, Plot.ActualWidth - 4);
        var height = Math.Max(0, Plot.ActualHeight - 4);
        foreach (var segment in trend.Segments)
        {
            if (segment.Count == 1)
            {
                var dot = new Ellipse { Width = 4, Height = 4, Fill = Foreground };
                Canvas.SetLeft(dot, segment[0].X * width);
                Canvas.SetTop(dot, (1 - segment[0].Y) * height);
                Plot.Children.Add(dot);
            }
            else if (segment.Count > 1)
            {
                var line = new Polyline { Stroke = Foreground, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                foreach (var point in segment)
                    line.Points.Add(new global::Windows.Foundation.Point(2 + point.X * width, 2 + (1 - point.Y) * height));
                Plot.Children.Add(line);
            }
        }
    }
}
