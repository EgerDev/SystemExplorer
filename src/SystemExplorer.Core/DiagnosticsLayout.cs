namespace SystemExplorer.Core;

public static class DiagnosticsLayout
{
    public const double MinimumTableHeight = 136;

    public static double MaximumSummaryHeight(double height, double footerHeight, double spacing) =>
        !double.IsFinite(height) || !double.IsFinite(footerHeight) || !double.IsFinite(spacing) ||
        height < 0 || footerHeight < 0 || spacing < 0
            ? 0 : Math.Max(0, height - footerHeight - spacing * 2 - MinimumTableHeight);
}
