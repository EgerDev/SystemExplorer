using System.Runtime.InteropServices;
using SystemExplorer.Core;
using SystemExplorer.Windows;

internal static class ProcessInspectionSmoke
{
    internal static async Task Run(ProcessIdentity self, CancellationToken token)
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Check(Marshal.SizeOf<WindowsProcessInspectionProvider.ModuleEntry>() == 1080, "MODULEENTRY32W x64 layout mismatch.");
        Check(Marshal.SizeOf<WindowsProcessInspectionProvider.MemoryInformation>() == 48 &&
            Marshal.OffsetOf<WindowsProcessInspectionProvider.MemoryInformation>("RegionSize").ToInt32() == 24, "MEMORY_BASIC_INFORMATION x64 layout mismatch.");
        var provider = new WindowsProcessInspectionProvider();
        var modules = await provider.CaptureAsync(self, ProcessInspectionKind.Modules, token);
        Check(modules.Status == InspectionStatus.Available && modules.Modules.Count > 1, "Self module inventory must be available: " + modules.Detail);
        Check(modules.Modules.Any(m => m.Name.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)), "Module inventory must include the actual executable.");
        Check(modules.Modules.All(m => m.BaseAddress > 0 && m.SizeBytes > 0) && modules.Regions.Count == 0, "Invalid module projection.");
        var allocation = Marshal.AllocHGlobal(4096);
        try
        {
            var address = unchecked((ulong)allocation.ToInt64());
            var memory = await provider.CaptureAsync(self, ProcessInspectionKind.MemoryRegions, token);
            Check(memory.Status == InspectionStatus.Available, "Self memory regions must be available: " + memory.Detail);
            Check(memory.Regions.Any(r => r.BaseAddress <= address && address - r.BaseAddress < r.SizeBytes && r.State == 0x1000), "Allocated test address must be in an observed committed region.");
            Check(memory.Regions.All(r => r.SizeBytes > 0) && memory.Modules.Count == 0, "Invalid region projection.");
            Check(memory.Regions.Where(r => r.State == 0x10000).All(r => r.Protection is null && r.Type is null && r.AllocationBase is null), "Undefined free-region fields must not be projected.");
            Check(memory.Regions.Where(r => r.State == 0x2000).All(r => r.Protection is null), "Reserved-region protection is undefined.");
            Check(memory.Regions.Zip(memory.Regions.Skip(1)).All(pair => pair.First.BaseAddress + pair.First.SizeBytes <= pair.Second.BaseAddress), "Memory regions must progress without overlap.");
        }
        finally { Marshal.FreeHGlobal(allocation); }
        foreach (var kind in Enum.GetValues<ProcessInspectionKind>())
        {
            var wrong = await provider.CaptureAsync(self with { CreationTimeFileTime = self.CreationTimeFileTime + 1 }, kind, token);
            Check(wrong.Status == InspectionStatus.IdentityMismatch && wrong.Modules.Count == 0 && wrong.Regions.Count == 0, "A reused/mismatched identity must never return data.");
            var unknown = await provider.CaptureAsync(new(self.Pid, null), kind, token);
            Check(unknown.Status == InspectionStatus.Unsupported, "Unknown creation time must not fall back to PID.");
            var bounded = await new WindowsProcessInspectionProvider(1, TimeSpan.FromSeconds(3)).CaptureAsync(self, kind, token);
            Check(bounded.Status == InspectionStatus.Truncated && bounded.Modules.Count + bounded.Regions.Count == 1, "Small bound must explicitly truncate.");
            var expired = await new WindowsProcessInspectionProvider(1, TimeSpan.Zero).CaptureAsync(self, kind, token);
            Check(expired.Status == InspectionStatus.TimedOut && expired.Modules.Count + expired.Regions.Count == 0, "Expired budget must return explicit timeout.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await provider.CaptureAsync(self, ProcessInspectionKind.Modules, cancelled.Token); throw new InvalidOperationException("Expected inspection cancellation."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: read-only modules/regions, native layouts, actual executable/allocated address, nullable fields, stable identity rejection, truncation, timeout and cancellation.");
    }
}
