using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SystemExplorer.App.Controls;

public sealed partial class SummaryCard : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SummaryCard), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(SummaryCard), new PropertyMetadata("—"));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SummaryCard), new PropertyMetadata(""));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string AccessibleValue(string label, string value) => $"{label}: {value}";
    public SummaryCard() => InitializeComponent();
}
