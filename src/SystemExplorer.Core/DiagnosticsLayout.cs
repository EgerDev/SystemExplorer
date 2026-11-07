namespace SystemExplorer.Core;

public static class DiagnosticsLayout
{
    public const double MinimumTableHeight = 136;
    public const double PlotHeight = 72;
    public const double ExpandedChartHeight = 164;
    public const double CollapsedChartHeight = 48;

    public static double MaximumSummaryHeight(double height, double footerHeight, double spacing,
        bool chartVisible, bool chartExpanded, double measuredChartHeight = 0) =>
        !double.IsFinite(height) || !double.IsFinite(footerHeight) || !double.IsFinite(spacing) ||
        !double.IsFinite(measuredChartHeight) || height < 0 || footerHeight < 0 || spacing < 0 || measuredChartHeight < 0
            ? 0 : Math.Max(0, height - footerHeight - spacing * (chartVisible ? 3 : 2) - MinimumTableHeight -
                (chartVisible ? Math.Max(chartExpanded ? ExpandedChartHeight : CollapsedChartHeight, measuredChartHeight) : 0));
}
