using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;
using static SystemExplorer.Windows.NativeStorageMethods;

namespace SystemExplorer.Windows;

public sealed class WindowsStorageProvider
{
    public static bool IsElevated
    {
        get
        {
            if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) return false;
            using (token) return GetTokenInformation(token, 20, out var value, 4, out _) && value != 0;
        }
    }
    public Task<IReadOnlyList<StorageVolume>> GetVolumesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<StorageVolume>>(() =>
    {
        var buffer = new char[512];
        var length = GetLogicalDriveStringsW((uint)buffer.Length, buffer);
        if (length == 0 || length >= buffer.Length) throw new Win32Exception();
        var volumes = new List<StorageVolume>();
        foreach (var root in new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = GetDriveTypeW(root);
            if (type is not (2 or 3)) continue;
            var errors = new List<string>();
            var label = new StringBuilder(261); var fs = new StringBuilder(261);
            var info = GetVolumeInformationW(root, label, 261, out _, out _, out var flags, fs, 261);
            var infoError = info ? null : Error(); if (infoError != null) errors.Add(infoError);
            var freeOk = GetDiskFreeSpaceExW(root, out var available, out var total, out var free);
            if (!freeOk) errors.Add(Error());
            var clusterOk = GetDiskFreeSpaceW(root, out var sectors, out var bytes, out _, out _);
            if (!clusterOk) errors.Add(Error());
            StorageReading bus, media, product;
            using (var device = CreateFileW(@"\\.\" + root[..2], 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (device.IsInvalid) { bus = media = product = new(null, Error()); }
                else
                {
                    var input = new byte[12]; var output = new byte[4096];
                    if (DeviceIoControl(device, 0x2D1400, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) && returned >= 36)
                    {
                        var busType = BitConverter.ToUInt32(output, 28);
                        bus = new(StorageBusTypes.Name(busType), null);
                        string Field(int offset)
                        {
                            var start = BitConverter.ToUInt32(output, offset);
                            if (start == 0 || start >= returned) return "";
                            var end = Array.IndexOf(output, (byte)0, (int)start, returned - (int)start);
                            return Encoding.ASCII.GetString(output, (int)start, (end < 0 ? returned : end) - (int)start).Trim();
                        }
                        var description = string.Join(" ", new[] { Field(12), Field(16) }.Where(s => s.Length > 0));
                        product = description.Length > 0 ? new(description, null) : new(null, "Device descriptor did not report vendor/product");
                        input[0] = 7;
                        var seekOk = DeviceIoControl(device, 0x2D1400, input, input.Length, output, output.Length, out returned, IntPtr.Zero) && returned >= 9;
                        var seekError = seekOk ? null : Error();
                        media = StorageBusTypes.Media(seekOk ? output[8] != 0 : null, busType);
                        if (media.Value == null && seekError != null) media = new(null, seekError);
                    }
                    else { bus = media = product = new(null, Error()); }
                    if (media.Value == null)
                    {
                        var number = new byte[12];
                        if (DeviceIoControl(device, 0x2D1080, [], 0, number, number.Length, out var count, IntPtr.Zero) && count >= 12)
                        {
                            var physical = PhysicalDisk(BitConverter.ToUInt32(number, 4));
                            media = physical.Media;
                            if (bus.Value == null && physical.Bus.Value != null) bus = physical.Bus;
                        }
                        else media = new(null, "Seek penalty unavailable; disk-number mapping failed: " + Error());
                    }
                }
            }
            var compression = !info ? new StorageReading(null, infoError) : new((flags & 0x8000) != 0 ? "Volume compressed" : (flags & 0x10) != 0 ? "Volume supports per-file compression" : "Compression not supported", null);
            volumes.Add(new(root, label.ToString(), fs.ToString(), type == 3 ? "Fixed" : "Removable", freeOk ? total : null, freeOk ? free : null,
                freeOk ? available : null, clusterOk ? (long)sectors * bytes : null, media, bus, product, BitLocker(root), compression, errors.Count == 0 ? null : string.Join("; ", errors)));
        }
        return volumes.AsReadOnly();
    }, cancellationToken);

    public WindowsStorageScan StartScan(string root, CancellationToken cancellationToken, StorageScanLimits? limits = null) => new(root, limits ?? new(), cancellationToken);
    public Task<StorageScanResult> ScanAsync(string root, CancellationToken cancellationToken) => StartScan(root, cancellationToken).Completion;
    private static string Error() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
    private static (StorageReading Media, StorageReading Bus) PhysicalDisk(uint number)
    {
        object? locator = null, service = null, rows = null, enumerator = null, underlying = null;
        static void Release(object? o) { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\Microsoft\Windows\Storage");
            rows = ((dynamic)service).ExecQuery($"SELECT MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId='{number}'", "WQL", 0x30);
            enumerator = rows.GetType().InvokeMember("_NewEnum", BindingFlags.GetProperty, null, rows, null);
            underlying = enumerator is ICustomAdapter adapter ? adapter.GetUnderlyingObject() : enumerator;
            var iterator = (IEnumerator)enumerator!;
            if (!iterator.MoveNext()) return (new(null, $"Windows Storage did not report physical disk {number}"), new(null, "Physical disk unavailable"));
            var row = iterator.Current;
            try
            {
                uint media = Convert.ToUInt32(((dynamic)row).MediaType);
                uint bus = Convert.ToUInt32(((dynamic)row).BusType);
                return (media switch
                {
                    3 => new("HDD", null), 4 => new("SSD", null), 5 => new("SCM", null),
                    0 => new(null, "Windows reports media type Unspecified (virtual disks may not expose physical media)"),
                    _ => new(null, $"Windows reports unrecognized media type {media}")
                }, new(StorageBusTypes.Name(bus), null));
            }
            finally { Release(row); }
        }
        catch (Exception ex) { return (new(null, "Windows Storage media unavailable: " + ex.GetBaseException().Message), new(null, "Windows Storage bus unavailable")); }
        finally { if (enumerator is IDisposable d) d.Dispose(); Release(underlying); Release(rows); Release(service); Release(locator); }
    }
    private static StorageReading BitLocker(string root)
    {
        if (!IsElevated) return new(null, "Requires administrator");
        object? locator = null, service = null, rows = null, enumerator = null, underlying = null;
        static void Release(object? o) { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\CIMV2\Security\MicrosoftVolumeEncryption");
            rows = ((dynamic)service).ExecQuery($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{root[..2]}'", "WQL", 0x30);
            enumerator = rows.GetType().InvokeMember("_NewEnum", BindingFlags.GetProperty, null, rows, null);
            underlying = enumerator is ICustomAdapter adapter ? adapter.GetUnderlyingObject() : enumerator;
            var iterator = (IEnumerator)enumerator!;
            if (!iterator.MoveNext()) return new(null, "BitLocker did not report this volume");
            var row = iterator.Current;
            try
            {
                uint status = Convert.ToUInt32(((dynamic)row).ProtectionStatus);
                return new(status switch { 0 => "Off", 1 => "On", 2 => "Unknown", _ => $"Unknown ({status})" }, null);
            }
            finally { Release(row); }
        }
        catch (Exception ex) { return new(null, "BitLocker WMI unavailable (namespace/feature may be absent): " + ex.GetBaseException().Message); }
        finally { if (enumerator is IDisposable d) d.Dispose(); Release(underlying); Release(rows); Release(service); Release(locator); }
    }
}
