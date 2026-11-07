using System.Globalization;

namespace SystemExplorer.Core;

public readonly record struct ProcessSortKey(string Name, double? Cpu, ulong? Memory, int Pid, int Position)
{
    private readonly double? displayedCpu = Cpu is { } value ? StickyProcessSort.DisplayedCpu(value) : null;
    public double? Cpu
    {
        get => displayedCpu;
        init => displayedCpu = value is { } cpu ? StickyProcessSort.DisplayedCpu(cpu) : null;
    }
}

public static class StickyProcessSort
{
    public static double DisplayedCpu(double value) => double.Parse(value.ToString("F1", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static int Compare(ProcessSortKey left, ProcessSortKey right, string sort, bool descending)
    {
        var comparison = sort switch
        {
            "Name" => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
            "CPU" => CpuKey(left.Cpu, descending).CompareTo(CpuKey(right.Cpu, descending)),
            "PID" => left.Pid.CompareTo(right.Pid),
            _ => (left.Memory ?? (descending ? 0 : ulong.MaxValue)).CompareTo(right.Memory ?? (descending ? 0 : ulong.MaxValue))
        };
        if (comparison != 0) return descending ? -comparison : comparison;
        comparison = left.Position.CompareTo(right.Position);
        return comparison != 0 ? comparison : left.Pid.CompareTo(right.Pid);
    }

    private static double CpuKey(double? value, bool descending) => value ?? (descending ? -1 : double.MaxValue);
}
