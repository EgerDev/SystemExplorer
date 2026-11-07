using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using SystemExplorer.Core;
using EtwEvent = Microsoft.Diagnostics.Tracing.TraceEvent;

namespace SystemExplorer.Windows;

public sealed partial class WindowsActivityTraceRecorder
{
    private readonly DecodeDiagnostics decodeDiagnostics = new();
    private const string UnknownName = "(unresolved)";
    internal sealed class DecodeDiagnostics
    {
        private readonly Dictionary<(Guid Provider, int Id, int Version, string Reason), long> counts = [];
        private long unlisted;
        internal void Add(Guid provider, int id, int version, string reason)
        {
            var key = (provider, id, version, reason);
            if (counts.TryGetValue(key, out long count)) counts[key] = count + 1;
            else if (counts.Count < 16) counts[key] = 1;
            else unlisted++;
        }
        internal string Summary => counts.Count == 0 ? "" : " Decode failures (metadata only): " +
            string.Join("; ", counts.Select(pair => $"provider={pair.Key.Provider}, id={pair.Key.Id}, version={pair.Key.Version}, reason={pair.Key.Reason}, count={pair.Value}")) +
            (unlisted == 0 ? "." : $"; unlisted failures={unlisted}.");
    }
    private void Malformed(EtwEvent data, string reason)
    {
        state.ReportDiagnostics(malformedSkipped: 1);
        decodeDiagnostics.Add(data.ProviderGuid, (int)data.ID, data.Version, reason);
    }
    private static object Required(EtwEvent data, string field) => data.PayloadByName(field) ?? throw new FormatException("Missing ETW field.");
    private static int Int(EtwEvent data, string field) => Convert.ToInt32(Required(data, field), CultureInfo.InvariantCulture);
    private static ulong Unsigned(EtwEvent data, string field) => Required(data, field) switch
    {
        long value => unchecked((ulong)value), int value => unchecked((uint)value),
        IntPtr value => unchecked((ulong)value.ToInt64()), UIntPtr value => value.ToUInt64(),
        var value => Convert.ToUInt64(value, CultureInfo.InvariantCulture)
    };
    private static string? Text(EtwEvent data, string field) => data.PayloadByName(field) as string;
    private void Map(Dictionary<ulong, string> map, ulong key, string? value)
    {
        if (key == 0 || string.IsNullOrEmpty(value)) return;
        if (map.ContainsKey(key) || map.Count < nameMapCap) map[key] = value;
        else state.ReportDiagnostics(capsHit: true);
    }
    private string Name(string? value)
    {
        if (!string.IsNullOrEmpty(value) && !value.StartsWith(UnknownName, StringComparison.Ordinal)) return value;
        state.ReportDiagnostics(unresolvedNames: 1); return UnknownName;
    }

    private void OnEvent(EtwEvent data)
    {
        try
        {
            var at = new DateTimeOffset(data.TimeStamp.ToUniversalTime());
            if (at.UtcTicks < state.StartedUtc.UtcTicks || at.UtcTicks > Interlocked.Read(ref cutoff)) return;
            if (data.ProviderGuid == ProcessProvider) ProcessEvent(data, at);
            else if (data.ProviderGuid == FileProvider) FileEvent(data, at);
            else if (data.ProviderGuid == RegistryProvider) RegistryEvent(data, at);
            else if (data.ProviderGuid == NetworkProvider) NetworkEvent(data, at);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException or IndexOutOfRangeException)
        { Malformed(data, ex.GetType().Name); }
    }

    private void ProcessEvent(EtwEvent data, DateTimeOffset at)
    {
        int id = (int)data.ID;
        if (id is < 1 or > 5) return;
        if ((id == 1 && data.Version > 3) || (id == 2 && data.Version > 2) || (id is 3 or 4 && data.Version > 1) || (id == 5 && data.Version != 0))
        { Malformed(data, "unsupported version"); return; }
        int pid = Int(data, "ProcessID");
        if (id is 3 or 4) { state.RecordThread(pid, id == 3); return; }
        if (id == 1)
        {
            var creation = Required(data, "CreateTime");
            long fileTime = creation is DateTime time ? time.ToUniversalTime().ToFileTimeUtc() : Convert.ToInt64(creation, CultureInfo.InvariantCulture);
            string? image = data.Version == 3 ? ProcessStartV3Image(data) : Text(data, "ImageName");
            image = Name(image);
            state.Record(at, pid, data.ThreadID, TraceOperation.ProcessStart, image,
                processStart: new(pid, Int(data, "ParentProcessID"), fileTime, Path.GetFileName(image), image));
        }
        else if (id == 2)
        {
            int exit = unchecked((int)Unsigned(data, "ExitCode"));
            state.Record(at, pid, data.ThreadID, TraceOperation.ProcessStop, detail: $"exit code {exit}", exitCode: exit);
        }
        else state.Record(at, pid, data.ThreadID, TraceOperation.ImageLoad, Name(Text(data, "ImageName")));
    }

    // Host manifest v3: fixed leading fields (48 bytes), SID, then ImageName.
    // TraceEvent 4.0's dynamic parser stops at the SID. Read only this bounded string.
    private static string ProcessStartV3Image(EtwEvent data)
    {
        if (data.EventDataLength < 56) throw new FormatException("Short process start.");
        int count = Marshal.ReadByte(data.DataStart, 49);
        if (Marshal.ReadByte(data.DataStart, 48) != 1 || count > 15) throw new FormatException("Invalid SID layout.");
        int offset = 56 + 4 * count;
        if (offset >= data.EventDataLength) throw new FormatException("Missing image name.");
        int chars = 0;
        while (offset + chars * 2 + 1 < data.EventDataLength)
        {
            if (Marshal.ReadInt16(data.DataStart, offset + chars * 2) == 0)
                return Marshal.PtrToStringUni(data.DataStart + offset, chars) ?? "";
            chars++;
        }
        throw new FormatException("Unterminated image name.");
    }

    private void FileEvent(EtwEvent data, DateTimeOffset at)
    {
        int id = (int)data.ID;
        if (id is 10 or 11)
        {
            if (data.Version != 0) { Malformed(data, "unsupported version"); return; }
            ulong key = Unsigned(data, "FileKey");
            if (id == 10) Map(fileKeys, key, Text(data, "FileName")); else fileKeys.Remove(key);
            return;
        }
        if (id is not (12 or 14 or 15 or 16 or 26 or 27)) return;
        if (data.Version > 1) { Malformed(data, "unsupported version"); return; }
        ulong obj = Unsigned(data, "FileObject");
        if (id == 14) { fileObjects.Remove(obj); return; }
        int tid = data.Version == 1 ? Int(data, "IssuingThreadId") : unchecked((int)Unsigned(data, "ThreadId"));
        string? path;
        TraceOperation operation;
        ulong bytes = 0;
        if (id == 12)
        {
            path = Text(data, "FileName"); fileObjects.Remove(obj); Map(fileObjects, obj, path);
            operation = TraceOperation.FileCreate;
        }
        else if (id is 26 or 27)
        {
            path = Text(data, "FilePath");
            operation = id == 26 ? TraceOperation.FileDelete : TraceOperation.FileRename;
            if (id == 27) { fileObjects.Remove(obj); fileKeys.Remove(Unsigned(data, "FileKey")); }
        }
        else
        {
            path = fileObjects.GetValueOrDefault(obj) ?? fileKeys.GetValueOrDefault(Unsigned(data, "FileKey"));
            bytes = Unsigned(data, "IOSize"); operation = id == 15 ? TraceOperation.FileRead : TraceOperation.FileWrite;
        }
        state.Record(at, data.ProcessID, tid, operation, Name(path), bytes);
    }

    private void RegistryEvent(EtwEvent data, DateTimeOffset at)
    {
        int id = (int)data.ID;
        if (id is not (1 or 2 or 3 or 5 or 6 or 13)) return;
        if (data.Version != 0) { Malformed(data, "unsupported version"); return; }
        ulong obj = Unsigned(data, "KeyObject");
        if (id == 13) { registryKeys.Remove(obj); return; }
        string? path; string? detail = null;
        uint status = unchecked((uint)Unsigned(data, "Status"));
        if (id is 1 or 2)
        {
            string? relative = Text(data, "RelativeName");
            ulong baseObject = Unsigned(data, "BaseObject");
            string? baseName = Text(data, "BaseName");
            if (string.IsNullOrEmpty(baseName)) baseName = registryKeys.GetValueOrDefault(baseObject);
            if (relative?.StartsWith('\\') == true) path = relative;
            else if (!string.IsNullOrEmpty(baseName)) path = baseName.TrimEnd('\\') + (string.IsNullOrEmpty(relative) ? "" : "\\" + relative);
            else if (baseObject == 0) path = relative;
            else
            {
                path = UnknownName; detail = "relative to key opened before trace";
            }
            if (status == 0) { registryKeys.Remove(obj); Map(registryKeys, obj, path); }
        }
        else
        {
            path = Text(data, "KeyName");
            if (string.IsNullOrEmpty(path)) path = registryKeys.GetValueOrDefault(obj);
        }
        var operation = id switch
        {
            1 => TraceOperation.RegistryCreate, 2 => TraceOperation.RegistryOpen, 3 => TraceOperation.RegistryDeleteKey,
            5 => TraceOperation.RegistrySetValue, _ => TraceOperation.RegistryDeleteValue
        };
        if (status != 0) detail = $"status 0x{status:X8}" + (detail is null ? "" : "; " + detail);
        state.Record(at, data.ProcessID, data.ThreadID, operation, Name(path), detail: detail);
        if (id == 3 && status == 0) registryKeys.Remove(obj);
    }

    private void NetworkEvent(EtwEvent data, DateTimeOffset at)
    {
        int id = (int)data.ID;
        var operation = id switch
        {
            12 or 28 => TraceOperation.NetConnect, 15 or 31 => TraceOperation.NetAccept,
            10 or 26 or 42 or 58 => TraceOperation.NetSend, 11 or 27 or 43 or 59 => TraceOperation.NetReceive,
            13 or 29 => TraceOperation.NetDisconnect, _ => (TraceOperation)(-1)
        };
        if ((int)operation < 0) return;
        if (data.Version != 0) { Malformed(data, "unsupported version"); return; }
        int pid = Int(data, "PID");
        string source = Address(Required(data, "saddr")), destination = Address(Required(data, "daddr"));
        int sourcePort = Port(Int(data, "sport")), destinationPort = Port(Int(data, "dport"));
        bool ipv6 = id is 26 or 27 or 28 or 29 or 31 or 58 or 59;
        string endpoint = ipv6 ? $"[{source}]:{sourcePort} -> [{destination}]:{destinationPort}" : $"{source}:{sourcePort} -> {destination}:{destinationPort}";
        string protocol = (id >= 42 ? "UDP" : "TCP") + (ipv6 ? "v6" : "v4");
        state.Record(at, pid, data.ThreadID, operation, endpoint, Unsigned(data, "size"), protocol);
    }
    private static int Port(int port) => ((port & 0xff) << 8) | ((port >> 8) & 0xff);
    private static string Address(object value) => value switch
    {
        byte[] bytes when bytes.Length == 16 => new IPAddress(bytes).ToString(),
        IPAddress address => address.ToString(),
        int number => new IPAddress(unchecked((uint)number)).ToString(),
        uint number => new IPAddress(number).ToString(),
        _ => throw new FormatException("Unsupported network address.")
    };
}
