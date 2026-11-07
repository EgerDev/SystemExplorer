using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using SystemExplorer.Core;
using Windows.Foundation;

namespace SystemExplorer.App.Views;

public sealed partial class ResourceTrendView : UserControl
{
    private ResourceTrend? trend;
    private readonly ChartPlot plot;
    private double maximum;
    private string metric = "";

    public ResourceTrendView()
    {
        InitializeComponent();
        plot = new ChartPlot(Plot);
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Update(ResourceTrend? next, string label, bool rate = false, bool bytes = false)
    {
        var metric = $"{label}:{rate}:{bytes}";
        if (this.metric == metric && SameTrend(trend, next)) return;
        if (this.metric != metric) maximum = 0;
        this.metric = metric;
        trend = next;
        maximum = rate ? ChartScale.Next(maximum, next?.MaximumObserved ?? double.NaN, binaryUnits: true) : next?.Maximum ?? 100;
        string Value(double value) => bytes ? MetricFormatting.ByteSize(value) : rate ? Display.Rate(value) :
            value == Math.Truncate(value) ? $"{value:F0}%" : $"{value:F1}%";
        var description = next is { SampleCount: > 0, MinimumObserved: { } min, MaximumObserved: { } max }
            ? $"{label} history: {next.SampleCount} observed samples, range {Value(min)} to {Value(max)}. Axis 0 to {Value(maximum)}. Gridlines at zero, half and full scale. Gaps are not connected."
            : $"{label}: no measured history available.";
        ScaleLabel.Text = $"Max {Value(maximum)}";
        AutomationProperties.SetName(ScaleLabel, $"{label} maximum scale {Value(maximum)}; gridlines at zero, half and full scale");
        HistoryDescription.Text = next is { SampleCount: > 1 } ? "" : next is { SampleCount: 1 } ? "1 observed sample" : "No measured history";
        AutomationProperties.SetName(HistoryDescription, description);
        AutomationProperties.SetName(this, description);
        ToolTipService.SetToolTip(this, description);
        Draw();
    }

    private static bool SameTrend(ResourceTrend? left, ResourceTrend? right) => ReferenceEquals(left, right) ||
        left is not null && right is not null && left.Maximum == right.Maximum && left.SampleCount == right.SampleCount &&
        left.MinimumObserved == right.MinimumObserved && left.MaximumObserved == right.MaximumObserved &&
        left.Segments.Count == right.Segments.Count && left.Segments.Zip(right.Segments).All(pair => pair.First.SequenceEqual(pair.Second));

    private void PlotSizeChanged(object sender, SizeChangedEventArgs args) => Draw();

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void Draw()
    {
        if (plot is null) return;
        var width = Math.Max(0, Plot.ActualWidth - 4);
        var height = Math.Max(0, Plot.ActualHeight - 4);
        var ratio = maximum > 0 ? (trend?.Maximum ?? 0) / maximum : 0;
        var segments = trend?.Segments.Select(segment => new PlotSegment(0, segment.Select(point =>
            new Point(2 + point.X * width, 2 + (1 - Math.Clamp(point.Y * ratio, 0, 1)) * height)).ToArray())).ToArray() ?? [];
        plot.Render(segments, _ => Foreground, ScaleLabel.Foreground);
    }
}
