using System.Runtime.InteropServices;

namespace SystemExplorer.Windows;

internal enum DxgiInventoryStatus { Available, Unsupported, Failed }
internal sealed record DxgiAdapter(string Description, ulong Luid, ulong DedicatedVideoMemory, ulong SharedSystemMemory, uint Flags);
internal sealed record DxgiAdapterResult(DxgiInventoryStatus Status, int HResult, IReadOnlyList<DxgiAdapter> Adapters, bool Truncated);

internal static class DxgiAdapterReader
{
    private const int NotFound = unchecked((int)0x887A0002);
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint VendorId, DeviceId, SubSysId, Revision;
        internal UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal uint LuidLow;
        internal int LuidHigh;
        internal uint Flags;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static DxgiAdapterResult Read()
    {
        var adapters = new List<DxgiAdapter>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var hr = CreateDXGIFactory1(FactoryId, out factory);
            if (hr < 0) return new(DxgiInventoryStatus.Failed, hr, adapters, false);
            if (factory == IntPtr.Zero) return new(DxgiInventoryStatus.Failed, unchecked((int)0x80004003), adapters, false);
            var enumerate = Method<EnumAdapters1>(factory, 12);
            for (uint index = 0; index <= 16; index++)
            {
                IntPtr adapter = IntPtr.Zero;
                try
                {
                    hr = enumerate(factory, index, out adapter);
                    if (hr == NotFound) return new(DxgiInventoryStatus.Available, 0, adapters, false);
                    if (hr < 0) return new(DxgiInventoryStatus.Failed, hr, adapters, false);
                    if (index == 16) return new(DxgiInventoryStatus.Available, 0, adapters, true);
                    if (adapter == IntPtr.Zero) return new(DxgiInventoryStatus.Failed, unchecked((int)0x80004003), adapters, false);
                    hr = Method<GetDesc1>(adapter, 10)(adapter, out var description);
                    if (hr < 0) return new(DxgiInventoryStatus.Failed, hr, adapters, false);
                    adapters.Add(new(description.Description, ((ulong)(uint)description.LuidHigh << 32) | description.LuidLow,
                        description.DedicatedVideoMemory.ToUInt64(), description.SharedSystemMemory.ToUInt64(), description.Flags));
                }
                finally { if (adapter != IntPtr.Zero) Marshal.Release(adapter); }
            }
            return new(DxgiInventoryStatus.Available, 0, adapters, true);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(DxgiInventoryStatus.Unsupported, error.HResult, adapters, false);
        }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
    }
}
