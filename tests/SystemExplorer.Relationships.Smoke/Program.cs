using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SystemExplorer.Core;
using SystemExplorer.Relationships;

int checks = 0;
void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

void Invalid(Action action, string description)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(description);
}

async Task Cancelled(Func<CancellationToken, Task> operation)
{
    using var source = new CancellationTokenSource();
    source.Cancel();
    try { await operation(source.Token); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException("Pre-cancelled operation was not cancelled.");
}

Invalid(() => EndpointTable.Parse(new byte[3], true, false, default), "Short header accepted.");
byte[] invalidCount = new byte[4];
BinaryPrimitives.WriteUInt32LittleEndian(invalidCount, uint.MaxValue);
Invalid(() => EndpointTable.Parse(invalidCount, true, true, default), "Overflowing count accepted.");
foreach ((bool tcp, bool ipv6, int stride) in new[] { (true, false, 24), (true, true, 56), (false, false, 12), (false, true, 28) })
{
    var bytes = new byte[4 + stride];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
    int local = 4 + (tcp && !ipv6 ? 4 : 0);
    (ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback).GetAddressBytes().CopyTo(bytes, local);
    int port = 4 + (ipv6 ? 20 : tcp ? 8 : 4);
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(port), 54321);
    if (ipv6) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(local + 16), 7);
    if (tcp) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (ipv6 ? 48 : 0)), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + stride - 4), 1234);
    var row = EndpointTable.Parse(bytes, tcp, ipv6, default).Single();
    Require(row.LocalPort == 54321 && row.OwningPid == 1234 && row.LocalAddress == (ipv6 ? "::1%7" : "127.0.0.1"), "Endpoint byte order/layout failed.");
    Require(row.RemoteAddress is null && row.RemotePort is null, "UDP/listener remote must be null.");
    Invalid(() => EndpointTable.Parse(bytes[..^1], tcp, ipv6, default), "Truncated row accepted.");
    if (tcp)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (ipv6 ? 48 : 0)), 5);
        int remoteOffset = 4 + (ipv6 ? 24 : 12);
        (ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback).GetAddressBytes().CopyTo(bytes, remoteOffset);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + (ipv6 ? 44 : 16)), 12345);
        var connected = EndpointTable.Parse(bytes, true, ipv6, default).Single();
        Require(connected.State == "Established" && connected.RemotePort == 12345 && connected.RemoteAddress == (ipv6 ? "::1" : "127.0.0.1"), "TCP remote decoding failed.");
    }
}

Require(WindowsRelationshipProvider.IdentityMatches(new(42, 100), 100, 101), "Matching identity rejected.");
Require(!WindowsRelationshipProvider.IdentityMatches(new(42, 99), 100, 101), "Reused PID accepted.");
Require(!WindowsRelationshipProvider.IdentityMatches(new(42, 102), 102, 101), "Post-capture process accepted.");
Require(!WindowsRelationshipProvider.IdentityMatches(new(42, null), 100, 101), "Unstable identity accepted.");
IntPtr buffer = Marshal.AllocHGlobal(8);
try
{
    for (int i = 0; i < 8; i++) Marshal.WriteByte(buffer, i, 65);
    Invalid(() => WindowsRelationshipProvider.ReadServiceString(buffer, 8, IntPtr.Add(buffer, 8)), "Out-of-range service string accepted.");
    Invalid(() => WindowsRelationshipProvider.ReadServiceString(buffer, 8, buffer), "Unterminated service string accepted.");
    Marshal.WriteInt16(buffer, 0, 0);
    Require(WindowsRelationshipProvider.ReadServiceString(buffer, 8, buffer) == "", "Empty service string rejected.");
}
finally { Marshal.FreeHGlobal(buffer); }

using var current = Process.GetCurrentProcess();
var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc());
var supplied = new[] { new ProcessSnapshot { Identity = identity, Name = "Smoke process" } };
using var provider = new WindowsRelationshipProvider();
var tcp4 = new TcpListener(IPAddress.Loopback, 0);
using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
TcpListener? tcp6 = null;
UdpClient? udp6 = null;
try
{
    tcp4.Start();
    if (Socket.OSSupportsIPv6)
    {
        tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        tcp6.Server.DualMode = false;
        tcp6.Start();
        udp6 = new UdpClient(AddressFamily.InterNetworkV6);
        udp6.Client.DualMode = false;
        udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
    }
    var snapshot = await provider.CaptureAsync(supplied, default);
    void Own(string protocol, IPEndPoint endpoint)
    {
        var rows = snapshot.Connections.Where(e => e.Protocol == protocol && e.LocalPort == endpoint.Port && e.LocalAddress == endpoint.Address.ToString() && e.OwningPid == current.Id).ToArray();
        Require(rows.Length > 0 && rows.All(e => e.OwnerIdentity == identity), "Own exact endpoint or creation identity not observed.");
        if (protocol == "UDP") Require(rows.All(e => e.RemoteAddress is null && e.RemotePort is null), "UDP remote fabricated.");
    }
    Own("TCP", (IPEndPoint)tcp4.LocalEndpoint);
    Own("UDP", (IPEndPoint)udp4.Client.LocalEndPoint!);
    if (tcp6 is not null && udp6 is not null)
    {
        Own("TCP", (IPEndPoint)tcp6.LocalEndpoint);
        Own("UDP", (IPEndPoint)udp6.Client.LocalEndPoint!);
    }
    else Console.WriteLine("IPv6 live case unsupported by this host.");
    Require(snapshot.Services.Count > 0, "No services observed on this Windows host.");
    Require(snapshot.Services.All(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.State)), "Invalid service observations.");
    Require(snapshot.Services.All(s => s.StartType is null), "Unqueried service start type fabricated.");
    Require(snapshot.Limitations.Count > 0, "Observation limitations missing.");
    var mismatch = new[] { supplied[0] with { Identity = identity with { CreationTimeFileTime = identity.CreationTimeFileTime + 1 } } };
    var rejected = await provider.CaptureAsync(mismatch, default);
    Require(rejected.Connections.Where(c => c.OwningPid == current.Id).All(c => c.OwnerIdentity is null), "Mismatched live creation time joined.");
    await Cancelled(t => provider.CaptureAsync(supplied, t));
    await Cancelled(t => provider.GetStartupItemsAsync(t));
    await Cancelled(t => provider.GetPublisherAsync(Environment.ProcessPath!, t));
    var startup = await provider.GetStartupItemsAsync(default);
    Require(startup.All(s => !string.IsNullOrEmpty(s.Source) && !string.IsNullOrEmpty(s.Scope)), "Startup provenance missing.");
    var unsigned = await provider.GetPublisherAsync(typeof(WindowsRelationshipProvider).Assembly.Location, default);
    Require(unsigned.Publisher is null && !unsigned.VerificationStatus.StartsWith("Verified", StringComparison.Ordinal), "Unsigned assembly claimed trusted.");
    var remote = await provider.GetPublisherAsync(@"\\invalid.example\share\file.exe", default);
    Require(remote.VerificationStatus.StartsWith("Unsupported", StringComparison.Ordinal), "Remote file not rejected before access.");
    var system = await provider.GetPublisherAsync(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), default);
    Require(!string.IsNullOrEmpty(system.VerificationStatus), "System file verification returned no status.");
    Require(system.Publisher is null || system.VerificationStatus.StartsWith("Verified", StringComparison.Ordinal), "Publisher claimed without verified signature.");
    Console.WriteLine($"Observed endpoint rows={snapshot.Connections.Count}; services={snapshot.Services.Count}; startup entries={startup.Count}; limitations={snapshot.Limitations.Count}; IPv6={tcp6 is not null}.");
    Console.WriteLine($"System binary trust result: {system.VerificationStatus}");
}
finally
{
    tcp4.Stop();
    tcp6?.Stop();
    udp6?.Dispose();
}
provider.Dispose();
try { await provider.CaptureAsync(supplied, default); throw new InvalidOperationException("Disposed provider accepted capture."); }
catch (ObjectDisposedException) { checks++; }
Console.WriteLine($"PASS: {checks} checks (deterministic parser/identity checks and real Windows observations).");
