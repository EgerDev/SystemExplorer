namespace SystemExplorer.Core;

public static class MetricFormatting
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    public static string Bytes(ulong? bytes) => bytes is { } value ? Format(value, false) : "—";
    public static string ByteSize(double value) => double.IsFinite(value) && value >= 0 ? Format(value, false) : "—";
    public static string Rate(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? Format(number, true) : "—";

    private static string Format(double value, bool rate)
    {
        if (value == 0) return rate ? "0 B/s" : "0 B";
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        var format = unit >= 3 ? "F2" : unit > 0 ? "F1" : value is > 0 and < 0.01 ? "G3" : "0.##";
        return $"{value.ToString(format)} {Units[unit]}{(rate ? "/s" : "")}";
    }
}
