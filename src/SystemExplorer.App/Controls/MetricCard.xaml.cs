using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SystemExplorer.App.Controls;

public sealed partial class MetricCard : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(MetricCard), new PropertyMetadata(""));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricCard), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricCard), new PropertyMetadata("--"));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(MetricCard), new PropertyMetadata(""));
    public static readonly DependencyProperty TrendProperty = DependencyProperty.Register(nameof(Trend), typeof(UIElement), typeof(MetricCard), new PropertyMetadata(null));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public UIElement? Trend { get => (UIElement?)GetValue(TrendProperty); set => SetValue(TrendProperty, value); }

    public string AccessibleValue(string label, string value) => $"{label}: {value}";
    public Visibility DetailVisibility(string detail) => string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TrendVisibility(UIElement? trend) => trend is null ? Visibility.Collapsed : Visibility.Visible;

    public MetricCard() => InitializeComponent();
}
