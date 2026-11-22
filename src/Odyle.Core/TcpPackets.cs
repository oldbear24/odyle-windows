using System.Buffers.Binary;
using System.Net;

namespace Odyle.Core;
// An address of a game connection as 16 bytes, so a packet costs no string. An IPv4 address is
// kept in its IPv4-mapped form (::ffff:a.b.c.d): the row of a dual-mode socket in the IPv6
// connection table and the IPv4 packets of that connection then name the same address.
public readonly record struct FlowAddress(ulong High, ulong Low)
{
    private const ulong Mapped = 0x0000_ffff_0000_0000;
    public static FlowAddress FromIPv4(ReadOnlySpan<byte> address) => new(0, Mapped | BinaryPrimitives.ReadUInt32BigEndian(address));
    public static FlowAddress FromIPv6(ReadOnlySpan<byte> address) => new(BinaryPrimitives.ReadUInt64BigEndian(address), BinaryPrimitives.ReadUInt64BigEndian(address[8..]));
    public static FlowAddress From(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? FromIPv4(address.GetAddressBytes()) : FromIPv6(address.GetAddressBytes());
    public static FlowAddress Parse(string text) => From(IPAddress.Parse(text));
    public bool IsIPv4 => High == 0 && (Low & 0xffff_ffff_0000_0000) == Mapped;
    public IPAddress ToIPAddress()
    {
        if (IsIPv4) { Span<byte> four = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(four, (uint)Low); return new IPAddress(four); }
        Span<byte> bytes = stackalloc byte[16]; BinaryPrimitives.WriteUInt64BigEndian(bytes, High); BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
        return new IPAddress(bytes);
    }
    public override string ToString() => ToIPAddress().ToString();
}
// One TCP connection of the game as this PC receives it: Local is this PC, Remote the game server.
public readonly record struct TcpFlow(FlowAddress Local, ushort LocalPort, FlowAddress Remote, ushort RemotePort)
{
    public TcpFlow(string local, ushort localPort, string remote, ushort remotePort) : this(FlowAddress.Parse(local), localPort, FlowAddress.Parse(remote), remotePort) { }
}
// One TCP segment of a captured packet: its connection seen from this PC (the packet's destination
// is Local), sequence number, flag byte, and where its payload lies in the packet.
public readonly record struct TcpSegment(TcpFlow Flow, uint Sequence, byte Flags, int PayloadOffset, int PayloadLength);
// The Ethernet, VLAN, IP and TCP headers of one captured packet. Link types: Ethernet (1), BSD
// loopback with its 4-byte family (0, 108) and raw IP (12, 101). Fragments are never read.
public static class TcpPacket
{
    public static bool IsSupportedLink(int linkType) => linkType is 1 or 0 or 108 or 12 or 101;
    public static bool TryParse(ReadOnlySpan<byte> packet, int linkType, out TcpSegment segment)
    {
        segment = default;
        if (!IsSupportedLink(linkType)) return false;
        var length = packet.Length;
        var ip = linkType == 1 ? 14 : linkType is 0 or 108 ? 4 : 0;
        if (linkType == 1)
        {
            if (length < 14) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[12..]);
            if (type is 0x8100 or 0x88a8) { if (length < 18) return false; type = BinaryPrimitives.ReadUInt16BigEndian(packet[16..]); ip += 4; }
            if (type != 0x0800) return false;
        }
        return IPv4(packet, ip, out segment);
    }
    private static bool IPv4(ReadOnlySpan<byte> packet, int ip, out TcpSegment segment)
    {
        segment = default; var length = packet.Length;
        if (length < ip + 20 || packet[ip] >> 4 != 4 || packet[ip + 9] != 6 || (BinaryPrimitives.ReadUInt16BigEndian(packet[(ip + 6)..]) & 0x3fff) != 0) return false;
        var ipLength = (packet[ip] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(packet[(ip + 2)..]); var tcp = ip + ipLength;
        if (ipLength < 20 || total < ipLength + 20 || ip + total > length) return false;
        var tcpLength = (packet[tcp + 12] >> 4) * 4;
        if (tcpLength < 20 || ipLength + tcpLength > total) return false;
        var flow = new TcpFlow(FlowAddress.FromIPv4(packet.Slice(ip + 16, 4)), BinaryPrimitives.ReadUInt16BigEndian(packet[(tcp + 2)..]), FlowAddress.FromIPv4(packet.Slice(ip + 12, 4)), BinaryPrimitives.ReadUInt16BigEndian(packet[tcp..]));
        segment = new(flow, BinaryPrimitives.ReadUInt32BigEndian(packet[(tcp + 4)..]), packet[tcp + 13], tcp + tcpLength, total - ipLength - tcpLength);
        return true;
    }
}
