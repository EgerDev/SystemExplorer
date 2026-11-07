sealed class LegacyProcessColumns
{
    private readonly double[] preferred = [260, 132, 76, 100, 80, 0, 0, 72];
    public readonly double[] Widths = [260, 132, 76, 0, 0, 0, 0, 72];
    private static readonly double[] Minimums = [180, 126, 68, 88, 72, 0, 0, 64];
    private static readonly double[] Maximums = [600, 220, 160, 200, 160, 320, 220, 160];
    private double viewport = 572;
    public void FitViewport(double width)
    {
        if (!double.IsFinite(width) || width <= 32) return;
        viewport = width;
        for (var i = 0; i < Widths.Length; i++) Widths[i] = preferred[i];
        if (width < 740) Widths[3] = 0;
        if (width < 840) Widths[4] = 0;
        var available = Math.Max(0, width - 32);
        var excess = Widths.Sum() - available;
        foreach (var i in new[] { 0, 3, 4, 1, 2, 7 })
        {
            if (excess <= 0) break;
            var reduction = Math.Min(excess, Math.Max(0, Widths[i] - Minimums[i]));
            Widths[i] -= reduction;
            excess -= reduction;
        }
        if (excess < 0) Widths[0] -= excess;
    }
    public void Resize(int index, double width)
    {
        if (index < 0 || index >= Widths.Length || !double.IsFinite(width)) return;
        var next = Math.Clamp(width, Minimums[index], Maximums[index]);
        if (next == Widths[index]) return;
        if (index == 0)
        {
            var remaining = next - Widths[0];
            foreach (var other in new[] { 1, 3, 4, 2, 7 })
            {
                if (Widths[other] == 0) continue;
                var changed = Math.Clamp(Widths[other] - remaining, Minimums[other], Maximums[other]);
                remaining -= Widths[other] - changed;
                preferred[other] = changed;
                if (Math.Abs(remaining) < 0.1) break;
            }
        }
        preferred[index] = next;
        FitViewport(viewport);
    }
}
