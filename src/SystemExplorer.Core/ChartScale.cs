namespace SystemExplorer.Core;

public static class ChartScale
{
    public static double Next(double current, double observedMaximum, bool binaryUnits = false, double minimum = 1)
    {
        if (!double.IsFinite(observedMaximum) || observedMaximum < 0) return current > 0 && double.IsFinite(current) ? current : minimum;
        var maximum = Math.Max(minimum, observedMaximum);
        if (double.IsFinite(current) && current >= maximum && observedMaximum >= current / 2) return current;
        var unit = 1d;
        if (binaryUnits)
            while (maximum / unit >= 1024 && unit < Math.Pow(1024, 6)) unit *= 1024;
        var nice = Round(maximum / unit) * unit;
        if (binaryUnits && nice / unit >= 1024 && unit < Math.Pow(1024, 6))
        {
            unit *= 1024;
            nice = Round(maximum / unit) * unit;
        }
        return double.IsFinite(nice) ? Math.Max(minimum, nice) : double.MaxValue;
    }

    private static double Round(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var fraction = value / magnitude;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * magnitude;
    }
}
