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
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MetricCard), new PropertyMetadata(false));
    public static readonly DependencyProperty TrendHeightProperty = DependencyProperty.Register(nameof(TrendHeight), typeof(double), typeof(MetricCard), new PropertyMetadata(32d));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public UIElement? Trend { get => (UIElement?)GetValue(TrendProperty); set => SetValue(TrendProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public double TrendHeight { get => (double)GetValue(TrendHeightProperty); set => SetValue(TrendHeightProperty, value); }

    public Thickness ContentPadding(bool compact) => compact ? new(12, 8, 12, 8) : new(16);
    public double ContentSpacing(bool compact) => compact ? 4 : 6;
    public string AccessibleValue(string label, string value) => $"{label}: {value}";
    public Visibility DetailVisibility(string detail) => string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TrendVisibility(UIElement? trend) => trend is null ? Visibility.Collapsed : Visibility.Visible;

    public MetricCard() => InitializeComponent();
}
