using System.Buffers.Binary;
using System.Net;
using SystemExplorer.Core;

namespace SystemExplorer.Relationships;

internal static class EndpointTable
{
    internal static IReadOnlyList<ConnectionSnapshot> Parse(ReadOnlySpan<byte> table, bool tcp, bool ipv6,
        CancellationToken cancellationToken)
    {
        if (table.Length < 4) throw new InvalidDataException("Endpoint table header is truncated.");
        int rowSize = (tcp, ipv6) switch { (true, true) => 56, (true, false) => 24, (false, true) => 28, _ => 12 };
        uint count = U32(table, 0);
        if (count > (table.Length - 4) / rowSize) throw new InvalidDataException("Endpoint table count exceeds its buffer.");
        var result = new List<ConnectionSnapshot>((int)count);
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = table.Slice(4 + i * rowSize, rowSize);
            int localOffset = tcp && !ipv6 ? 4 : 0;
            int portOffset = ipv6 ? 20 : tcp ? 8 : 4;
            uint pid = U32(row, rowSize - 4);
            if (pid > int.MaxValue) throw new InvalidDataException("Endpoint PID cannot be represented by the contract.");
            uint state = tcp ? U32(row, ipv6 ? 48 : 0) : 0;
            bool remoteMeaningful = tcp && state != 2;
            result.Add(new ConnectionSnapshot
            {
                Protocol = tcp ? "TCP" : "UDP",
                LocalAddress = Address(row, localOffset, ipv6),
                LocalPort = Port(row, portOffset),
                RemoteAddress = remoteMeaningful ? Address(row, ipv6 ? 24 : 12, ipv6) : null,
                RemotePort = remoteMeaningful ? Port(row, ipv6 ? 44 : 16) : null,
                OwningPid = (int)pid,
                State = tcp ? TcpState(state) : null
            });
        }
        return result;
    }

    private static uint U32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(offset, 4));
    private static int Port(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16BigEndian(value.Slice(offset, 2));
    private static string Address(ReadOnlySpan<byte> value, int offset, bool ipv6) => ipv6
        ? new IPAddress(value.Slice(offset, 16), BinaryPrimitives.ReadUInt32BigEndian(value.Slice(offset + 16, 4))).ToString()
        : new IPAddress(value.Slice(offset, 4)).ToString();

    private static string TcpState(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => $"Unknown ({state})"
    };
}
