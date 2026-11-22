using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using Odyle.Core;

// Pins what the decoder makes of the game's stream (#85): seeded synthetic traffic, never a
// capture, goes through GameStreams, the decoder alone and TcpReceiveStream alone, and every
// event, status and decoded byte count is written to a trace. The trace's hash was taken from
// the decoder before the allocation work; a refactor must reproduce it byte for byte. The traffic
// mixes snapshots, updates, identities, character switches, LZ4 bundles (nested and flagged),
// F0 to FE flags before readable and other opcodes, rosters held until the identity, locations,
// PvP frames with PvP stats switched on and off, heartbeats, padding and frames above 64 KB; it
// arrives in segments split anywhere, reordered and held, duplicated, retransmitted across the
// bytes already read, lost (resync) or conflicting, from the SYN or joined midstream.
static class CaptureChecks
{
    const uint Odyle = ExperimentalOdyleDecoder.OdyleResource, Counter = 80000001;
    const ulong Own = 606, Other = 707, Mate = 5069, Foe = 9001, OwnCharacter = 0x0900_0000_0003_6c9a, MateCharacter = 0x090e_0000_0003_b300;
    static readonly byte[] Tags = [0x00, 0x04, 0x08, 0x0c];
    static readonly byte[] PartyKey = [0xba, 0x0b, 0x06, 0x00, 0xb2, 0xdf, 0xf7, 0x23], ForceKey = [0x59, 0x1a, 0x00, 0x00, 0xb2, 0xdf, 0xf7, 0x23];
    // Taken from the decoder of 0.36.1 (91ed207), before any of #85's changes.
    const string PinnedTrace = "85C2852DD23DC562A4521F44D77556CCF877C3094A90AD3873A87C9B0BCAFCFF";
    const int PinnedLines = 125231;
    public static void Run()
    {
        Pinned();
        Flows();
        IPv4Packets();
        IPv6Packets();
        Console.WriteLine("Pinned decoder trace, connection address, IPv4 and IPv6 packet checks passed.");
    }
    // Connections are compared as numbers: an IPv4 packet and the IPv4-mapped row of a dual-mode
    // socket name the same connection, a real IPv6 address does not.
    static void Flows()
    {
        var game = new TcpFlow("10.0.0.2", 50000, "203.0.113.5", 13328);
        Expect(game == new TcpFlow("::ffff:10.0.0.2", 50000, "::ffff:203.0.113.5", 13328) && game.Local.IsIPv4 && game.Remote.ToString() == "203.0.113.5", "an IPv4-mapped address is the IPv4 address");
        Expect(game == new TcpFlow(FlowAddress.FromIPv4([10, 0, 0, 2]), 50000, FlowAddress.FromIPv4([203, 0, 113, 5]), 13328) && game.GetHashCode() == new TcpFlow("10.0.0.2", 50000, "203.0.113.5", 13328).GetHashCode(), "addresses from packet bytes equal parsed ones");
        var six = new TcpFlow("2001:db8::2", 50000, "2001:db8:ffff::5", 13328);
        Expect(six != game && !six.Local.IsIPv4 && six.Remote.ToString() == "2001:db8:ffff::5" && six == new TcpFlow(FlowAddress.FromIPv6(System.Net.IPAddress.Parse("2001:db8::2").GetAddressBytes()), 50000, FlowAddress.Parse("2001:db8:ffff::5"), 13328), "an IPv6 address is its own");
        Expect(new TcpFlow("::5", 1, "::6", 2) != new TcpFlow("0.0.0.5", 1, "0.0.0.6", 2), "an IPv4-compatible IPv6 address is not IPv4");
        Expect(game != game with { LocalPort = 50001 } && new[] { six, game }.Contains(new TcpFlow("10.0.0.2", 50000, "203.0.113.5", 13328)), "ports count and arrays find flows by value");
    }
    // The parser moved out of the capture loop (#85): for every IPv4 packet it answers exactly what
    // the loop's inline parser of 0.36.1 answered (LegacyParse, copied verbatim), over valid packets
    // of every link type and random damage to them.
    static void IPv4Packets()
    {
        byte[] client = [10, 0, 0, 2], server = [203, 0, 113, 5];
        var data = "game bytes"u8.ToArray();
        foreach (var link in new[] { 1, 0, 108, 12, 101 })
        {
            var packet = IPv4(link, server, client, 13328, 50000, 0x01020304, 0x18, data);
            Expect(TcpPacket.TryParse(packet, link, out var segment) && segment.Flow == new TcpFlow("10.0.0.2", 50000, "203.0.113.5", 13328) && segment.Sequence == 0x01020304 && segment.Flags == 0x18
                && packet.AsSpan(segment.PayloadOffset, segment.PayloadLength).SequenceEqual(data), $"an IPv4 segment on link type {link}");
        }
        Expect(TcpPacket.TryParse(IPv4(1, server, client, 13328, 50000, 7, 0x12, [], vlan: true, ipOptions: 8, tcpOptions: 12, padding: 6), 1, out var tagged) && tagged.PayloadLength == 0 && tagged.Flags == 0x12, "VLAN, IP and TCP options, Ethernet padding");
        Expect(!TcpPacket.TryParse(IPv4(1, server, client, 13328, 50000, 7, 0x18, data, fragment: 0x2000), 1, out _) && !TcpPacket.TryParse(IPv4(1, server, client, 13328, 50000, 7, 0x18, data, fragment: 0x0010), 1, out _), "fragments are not read");
        Expect(!TcpPacket.TryParse(IPv4(1, server, client, 13328, 50000, 7, 0x18, data), 113, out _), "an unsupported link type is not read");
        var random = new Random(20261122); var read = 0;
        for (var round = 0; round < 20000; round++)
        {
            var link = new[] { 1, 0, 108, 12, 101 }[random.Next(5)];
            var payload = new byte[random.Next(0, 100)]; random.NextBytes(payload);
            var packet = IPv4(link, server, client, (ushort)random.Next(65536), (ushort)random.Next(65536), (uint)random.Next(), (byte)random.Next(256), payload,
                vlan: link == 1 && random.Next(4) == 0, ipOptions: random.Next(3) == 0 ? random.Next(1, 11) * 4 : 0, tcpOptions: random.Next(3) == 0 ? random.Next(1, 11) * 4 : 0, fragment: random.Next(10) == 0 ? (ushort)random.Next(65536) : (ushort)0, padding: random.Next(3) == 0 ? random.Next(1, 30) : 0);
            for (var damage = random.Next(4); damage > 0; damage--) packet[random.Next(Math.Min(packet.Length, 80))] = (byte)random.Next(256);
            if (random.Next(6) == 0) packet = packet[..random.Next(packet.Length + 1)];
            var legacy = LegacyParse(packet, link);
            var parsed = TcpPacket.TryParse(packet, link, out var segment);
            if (parsed && !segment.Flow.Local.IsIPv4) continue; // An IPv6 packet, which 0.36.1 never read.
            Expect(parsed == legacy is not null, $"IPv4 packet {round}: read {parsed}, before {legacy is not null}");
            if (legacy is not { } old) continue;
            read++;
            Expect(segment.Flow.Local.ToString() == old.Destination && segment.Flow.LocalPort == old.DestinationPort && segment.Flow.Remote.ToString() == old.Source && segment.Flow.RemotePort == old.SourcePort
                && segment.Sequence == old.Sequence && segment.Flags == old.Flags && packet.AsSpan(segment.PayloadOffset, segment.PayloadLength).SequenceEqual(old.Payload), $"IPv4 packet {round} reads as before");
        }
        Expect(read is > 5000 and < 15000, $"the damaged packets are read and refused alike ({read} of 20000 read)");
    }
    // The capture loop's parser of 0.36.1 (PacketCapture.Receive), verbatim but for returning instead of continuing.
    static (string Destination, ushort DestinationPort, string Source, ushort SourcePort, uint Sequence, byte Flags, byte[] Payload)? LegacyParse(byte[] bytes, int linkType)
    {
        var length = bytes.Length;
        var ip = linkType == 1 ? 14 : linkType is 0 or 108 ? 4 : 0;
        if (linkType == 1) { if (length < 14) return null; var type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(12)); if (type is 0x8100 or 0x88a8) { if (length < 18) return null; type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16)); ip += 4; } if (type != 0x0800) return null; }
        if (length < ip + 20 || bytes[ip] >> 4 != 4 || bytes[ip + 9] != 6 || (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ip + 6)) & 0x3fff) != 0) return null;
        var ipLength = (bytes[ip] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ip + 2)); var tcp = ip + ipLength;
        if (ipLength < 20 || total < ipLength + 20 || ip + total > length) return null;
        var tcpLength = (bytes[tcp + 12] >> 4) * 4; if (tcpLength < 20 || ipLength + tcpLength > total) return null;
        var source = new System.Net.IPAddress(bytes.AsSpan(ip + 12, 4)).ToString(); var destination = new System.Net.IPAddress(bytes.AsSpan(ip + 16, 4)).ToString();
        var flags = bytes[tcp + 13];
        return (destination, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tcp + 2)), source, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tcp)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(tcp + 4)), flags, bytes.AsSpan(tcp + tcpLength, total - ipLength - tcpLength).ToArray());
    }
    // The link header: Ethernet (with a VLAN tag if asked), the loopback family or nothing for raw IP.
    static byte[] Link(int link, ushort type, bool vlan) => link switch
    {
        1 => [.. new byte[] { 2, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 2 }, .. vlan ? new byte[] { 0x81, 0x00, 0x00, 0x05 } : [], (byte)(type >> 8), (byte)type],
        0 or 108 => type == 0x86dd ? [23, 0, 0, 0] : [2, 0, 0, 0],
        _ => [],
    };
    static byte[] Tcp(ushort sourcePort, ushort destinationPort, uint sequence, byte flags, int options)
    {
        var tcp = new byte[20 + options]; BinaryPrimitives.WriteUInt16BigEndian(tcp, sourcePort); BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), sequence); tcp[12] = (byte)((20 + options) / 4 << 4); tcp[13] = flags; tcp[14] = 0xff;
        for (var i = 20; i < tcp.Length; i++) tcp[i] = 1;
        return tcp;
    }
    static byte[] IPv4(int link, byte[] source, byte[] destination, ushort sourcePort, ushort destinationPort, uint sequence, byte flags, byte[] payload, bool vlan = false, int ipOptions = 0, int tcpOptions = 0, ushort fragment = 0, int padding = 0)
    {
        var tcp = Tcp(sourcePort, destinationPort, sequence, flags, tcpOptions);
        var ip = new byte[20 + ipOptions]; ip[0] = (byte)(0x40 | (20 + ipOptions) / 4); BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)(ip.Length + tcp.Length + payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(6), fragment); ip[8] = 64; ip[9] = 6; source.CopyTo(ip, 12); destination.CopyTo(ip, 16);
        return [.. Link(link, 0x0800, vlan), .. ip, .. tcp, .. payload, .. new byte[padding]];
    }
    // The game over IPv6 (#94): the fixed header, the extension headers that may come before TCP, and
    // everything else refused; then a login read end to end from IPv6 packets.
    static void IPv6Packets()
    {
        const string client = "2001:db8::2", server = "2001:db8:ffff::5";
        var game = new TcpFlow(client, 50000, server, 13328); var data = "game bytes"u8.ToArray();
        bool Reads(byte[] packet, int link = 1) => TcpPacket.TryParse(packet, link, out var segment) && segment.Flow == game && segment.Sequence == 0x0a0b0c0d && segment.Flags == 0x18 && packet.AsSpan(segment.PayloadOffset, segment.PayloadLength).SequenceEqual(data);
        byte[] Packet((byte Type, int Length, ushort Fragment)[]? extensions = null, int link = 1, bool vlan = false, int padding = 0, int? claimed = null, byte last = 6, int tcpOptions = 0) =>
            IPv6(link, server, client, 13328, 50000, 0x0a0b0c0d, 0x18, data, extensions ?? [], vlan, padding, claimed, last, tcpOptions);
        foreach (var link in new[] { 1, 0, 108, 12, 101 }) Expect(Reads(Packet(link: link), link), $"an IPv6 segment on link type {link}");
        Expect(Reads(Packet(vlan: true, padding: 10, tcpOptions: 12)), "VLAN, TCP options and Ethernet padding");
        Expect(Reads(Packet([(0, 8, 0)])) && Reads(Packet([(60, 16, 0)])) && Reads(Packet([(43, 24, 0)])) && Reads(Packet([(51, 24, 0)])) && Reads(Packet([(44, 8, 0)])), "hop-by-hop, destination options, routing, authentication and an atomic fragment header");
        Expect(Reads(Packet([(0, 8, 0), (60, 8, 0), (43, 16, 0), (44, 8, 0x0006), (51, 12, 0), (60, 2040, 0)])), "a chain of extension headers before TCP");
        Expect(!Reads(Packet([(44, 8, 0x0001)])) && !Reads(Packet([(44, 8, 0x0008)])) && !Reads(Packet([(44, 8, 0xfff8)])), "a first, middle or last fragment is not read");
        Expect(!Reads(Packet([(0, 8, 0)], last: 50)) && !Reads(Packet(last: 17)) && !Reads(Packet([(60, 8, 0)], last: 59)), "ESP, UDP and no next header are not read");
        Expect(!Reads(Packet([.. Enumerable.Repeat(((byte)60, 8, (ushort)0), 9)])) && Reads(Packet([.. Enumerable.Repeat(((byte)60, 8, (ushort)0), 8)])), "at most eight extension headers");
        var full = Packet([(0, 16, 0)]);
        Expect(!Reads(Packet(claimed: 0)) && !Reads(Packet(claimed: 40 + data.Length)) && !Reads(full[..^1]) && !Reads(full[..(14 + 40 + 4)]) && !Reads(full[..(14 + 39)]), "a jumbogram, a length beyond the capture and truncated headers are not read");
        var badTcp = Packet(); badTcp[14 + 40 + 12] = 0x40; Expect(!Reads(badTcp), "a TCP header shorter than 20 bytes is not read");
        var four = Packet(); four[14] = 0x45; Expect(!TcpPacket.TryParse(four, 1, out _), "an IPv6 ethertype with an IPv4 header is not read");
        // Random damage to IPv6 packets never throws, and whatever is read lies inside the packet.
        var random = new Random(94);
        for (var round = 0; round < 20000; round++)
        {
            var link = new[] { 1, 0, 108, 12, 101 }[random.Next(5)];
            var extensions = Enumerable.Range(0, random.Next(0, 5)).Select(_ => (new byte[] { 0, 43, 44, 51, 60 }[random.Next(5)], random.Next(1, 6) * 8, (ushort)(random.Next(4) == 0 ? random.Next(65536) : 0))).ToArray();
            var payload = new byte[random.Next(0, 80)]; random.NextBytes(payload);
            var packet = IPv6(link, server, client, 13328, (ushort)random.Next(65536), (uint)random.Next(), (byte)random.Next(256), payload, extensions, link == 1 && random.Next(4) == 0, random.Next(3) == 0 ? random.Next(1, 20) : 0, null, 6, random.Next(3) == 0 ? random.Next(1, 11) * 4 : 0);
            for (var damage = random.Next(4); damage > 0; damage--) packet[random.Next(packet.Length)] = (byte)random.Next(256);
            if (random.Next(6) == 0) packet = packet[..random.Next(packet.Length + 1)];
            if (TcpPacket.TryParse(packet, link, out var segment)) Expect(segment.PayloadOffset >= 0 && segment.PayloadLength >= 0 && segment.PayloadOffset + segment.PayloadLength <= packet.Length, $"IPv6 packet {round} stays inside the packet");
        }
        // A login over IPv6, split into packets: parsed, then read like any other connection.
        var statuses = new List<CaptureStatus>(); var candidates = new List<int>(); var named = new List<string>();
        var streams = new GameStreams(decoder => { decoder.Candidate += c => candidates.Add(c.Total); decoder.IdentityObserved += i => named.Add(i.CharacterName); }, (kind, _) => statuses.Add(kind));
        byte[] login = [.. F(Identity(Own, "Hero")), .. F(Bundle(F([0x0b, 0x61, 1, .. Stat(0x0c, Odyle, 100, 50)]))),0x06, 0x00, 0x36, .. F(Update(0, Stat(0x08, Odyle, 0, 140)))];
        const uint isn = 4000; var packets = new List<byte[]> { IPv6(1, server, client, 13328, 50000, isn, 0x12, [], [], false, 0, null, 6, 0) };
        for (var at = 0; at < login.Length; at += 17) packets.Add(IPv6(1, server, client, 13328, 50000, isn + 1 + (uint)at, 0x18, login[at..Math.Min(login.Length, at + 17)], at % 34 == 0 ? [(0, 8, 0)] : [], false, 0, null, 6, 0));
        (packets[2], packets[3]) = (packets[3], packets[2]);
        foreach (var packet in packets)
        {
            Expect(TcpPacket.TryParse(packet, 1, out var segment) && segment.Flow == game, "every packet of the IPv6 login parses");
            streams.Feed(segment.Flow, segment.Sequence, segment.Flags, packet.AsSpan(segment.PayloadOffset, segment.PayloadLength));
        }
        Expect(candidates.SequenceEqual([150, 140]) && named.SequenceEqual(["Hero"]) && statuses.SequenceEqual([CaptureStatus.Listening]), "a login over IPv6 is read: " + string.Join(",", candidates));
    }
    // Extension headers in order, each (type, length in bytes, fragment field), then `last` (TCP is 6).
    static byte[] IPv6(int link, string source, string destination, ushort sourcePort, ushort destinationPort, uint sequence, byte flags, byte[] payload, (byte Type, int Length, ushort Fragment)[] extensions, bool vlan, int padding, int? claimed, byte last, int tcpOptions)
    {
        var chain = new List<byte>();
        for (var i = 0; i < extensions.Length; i++)
        {
            var (type, length, fragment) = extensions[i]; var next = i + 1 < extensions.Length ? extensions[i + 1].Type : last;
            var header = new byte[type == 44 ? 8 : length]; header[0] = next;
            if (type == 44) BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), fragment);
            else header[1] = (byte)(type == 51 ? length / 4 - 2 : length / 8 - 1);
            chain.AddRange(header);
        }
        byte[] upper = [.. Tcp(sourcePort, destinationPort, sequence, flags, tcpOptions), .. payload];
        var ip = new byte[40]; ip[0] = 0x60; BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)(claimed ?? chain.Count + upper.Length));
        ip[6] = extensions.Length > 0 ? extensions[0].Type : last; ip[7] = 64;
        System.Net.IPAddress.Parse(source).GetAddressBytes().CopyTo(ip, 8); System.Net.IPAddress.Parse(destination).GetAddressBytes().CopyTo(ip, 24);
        return [.. Link(link, 0x86dd, vlan), .. ip, .. chain, .. upper, .. new byte[padding]];
    }
    static void Pinned()
    {
        var culture = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var trace = new Trace();
            for (var run = 0; run < 120; run++) ThroughStreams(trace, run);
            for (var run = 0; run < 40; run++) DecoderAlone(trace, run);
            for (var run = 0; run < 40; run++) ReassemblyAlone(trace, run);
            if (Environment.GetEnvironmentVariable("ODYLE_CAPTURE_TRACE") is { Length: > 0 } path) File.WriteAllText(path, trace.Text.ToString());
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace.Text.ToString())));
            // The traffic reaches every path the trace is meant to pin.
            foreach (var (kind, least) in new (string, int)[] { ("candidate", 500), ("identity", 200), ("switch", 50), ("frame", 20000), ("group", 50), ("teammates", 50), ("location", 200), ("pvp", 20), ("pvpframe", 100), ("resyncing", 20), ("resumed", 10), ("error", 5), ("chunk", 1000), ("bundled", 200) })
                Expect(trace.Count(kind) >= least, $"the pinned traffic reaches {kind} ({trace.Count(kind)} < {least}): {trace.Counts}");
            Expect(hash == PinnedTrace && trace.Lines == PinnedLines, $"the decoder's output changed: trace {hash} with {trace.Lines} lines, pinned {PinnedTrace} with {PinnedLines}");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    sealed class Trace
    {
        public readonly StringBuilder Text = new();
        private readonly Dictionary<string, int> counts = new();
        public int Lines;
        public void Add(int run, string kind, string value)
        {
            Text.Append(run).Append(' ').Append(kind).Append(' ').Append(value).Append('\n'); Lines++;
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        public int Count(string kind) => counts.GetValueOrDefault(kind);
        public string Counts => string.Join(" ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value));
        // Every event of one decoder.
        public void Attach(ExperimentalOdyleDecoder decoder, int run, Func<bool> pvp)
        {
            decoder.Clock = () => DateTimeOffset.UnixEpoch.AddDays(20000);
            decoder.ReadPvp = pvp;
            decoder.Candidate += c => Add(run, "candidate", $"{c} {c.Base} {c.Bonus}");
            decoder.CharacterChanged += id => Add(run, "switch", id.ToString());
            decoder.IdentityObserved += i => Add(run, "identity", i.ToString());
            decoder.SnapshotWithoutOdyle += () => Add(run, "missing", "");
            decoder.FrameObserved += (opcode, size) => { Add(run, "frame", $"{opcode:X4} {size}"); if (opcode == 0xbbbb) Add(run, "bundled", ""); };
            decoder.UpdateObserved += u => Add(run, "update", u.ToString());
            decoder.LayoutObserved += l => Add(run, "layout", l.ToString());
            decoder.SnapshotObserved += s => Add(run, "snapshot", $"{s.PayloadBytes} {s.Records} {s.Read} {string.Join(",", s.Fields.Select(f => f.ToString()))}");
            decoder.LocationObserved += l => Add(run, "location", l.ToString());
            decoder.GroupObserved += g => Add(run, "group", g.ToString());
            decoder.TeammatesObserved += names => Add(run, "teammates", string.Join(",", names));
            decoder.PvpObserved += e => Add(run, "pvp", e.ToString());
            decoder.PvpFrameObserved += r => Add(run, "pvpframe", r.ToString());
        }
    }
    // Segments of a connection through GameStreams, as a capture worker feeds them.
    static void ThroughStreams(Trace trace, int run)
    {
        var random = new Random(20261122 + run); var pvp = run % 3 == 0;
        var streams = new GameStreams(decoder => trace.Attach(decoder, run, () => pvp), (kind, message) => trace.Add(run, kind.ToString().ToLowerInvariant(), message));
        var flow = new TcpFlow("10.0.0.2", (ushort)(50000 + run), "203.0.113.5", 13328);
        var content = Traffic(random);
        var isn = (uint)random.Next() * 2u; var midstream = run % 7 == 3;
        void Feed(uint sequence, byte flags, ReadOnlySpan<byte> payload) => trace.Add(run, "fed", $"{sequence - isn} {flags:X2} {payload.Length} {streams.Feed(flow, sequence, flags, payload)} {streams.IsBlocked(flow)}");
        var segments = new List<(int At, int Length)>();
        for (var at = midstream ? random.Next(1, Math.Min(4000, content.Length / 2)) : 0; at < content.Length;)
        {
            var length = Math.Min(content.Length - at, random.Next(10) == 0 ? random.Next(1, 9) : random.Next(1, 1500));
            segments.Add((at, length)); at += length;
        }
        if (!midstream) Feed(isn, 0x12, []);
        uint Sequence(int at) => unchecked(isn + 1 + (uint)at);
        var delayed = new List<(int Due, int Segment)>(); var lost = false;
        for (var i = 0; i < segments.Count; i++)
        {
            if (random.Next(30) == 0) pvp = !pvp;
            var (at, length) = segments[i]; var roll = random.Next(200);
            if (roll < 16 && i + 1 < segments.Count) delayed.Add((i + random.Next(1, 6), i));
            else if (roll < 18) { lost = true; trace.Add(run, "lost", i.ToString()); }
            else Feed(Sequence(at), 0x18, content.AsSpan(at, length));
            if (roll is >= 18 and < 28 && i > 0) { var (before, size) = segments[random.Next(Math.Max(0, i - 8), i)]; Feed(Sequence(before), 0x18, content.AsSpan(before, size)); }
            if (roll is >= 28 and < 36 && i > 0) { var start = segments[i - 1].At + segments[i - 1].Length / 2; Feed(Sequence(start), 0x18, content.AsSpan(start, at + length - start)); }
            if (roll == 36 && length > 1) { var bad = content.AsSpan(at, length).ToArray(); bad[length / 2] ^= 0x5a; Feed(Sequence(at), 0x18, bad); }
            foreach (var due in delayed.Where(d => d.Due <= i).ToArray()) { delayed.Remove(due); var (late, size) = segments[due.Segment]; Feed(Sequence(late), 0x18, content.AsSpan(late, size)); }
            if (lost && random.Next(4) == 0) { lost = false; streams.Maintain([flow], DateTimeOffset.UtcNow.AddSeconds(4)); trace.Add(run, "maintained", streams.Count.ToString()); }
        }
        foreach (var (_, segment) in delayed) Feed(Sequence(segments[segment].At), 0x18, content.AsSpan(segments[segment].At, segments[segment].Length));
        if (run % 2 == 0) Feed(Sequence(content.Length), 0x11, []);
        trace.Add(run, "connections", streams.Count.ToString());
    }
    // The decoder fed directly in pieces of any size; a framing error resyncs it, as GameStreams does.
    static void DecoderAlone(Trace trace, int run)
    {
        var random = new Random(7000 + run); var pvp = run % 2 == 0; var content = Traffic(random);
        var decoder = new ExperimentalOdyleDecoder(run % 5 != 4); trace.Attach(decoder, 1000 + run, () => pvp);
        for (var at = 0; at < content.Length;)
        {
            var length = Math.Min(content.Length - at, random.Next(3) == 0 ? 1 : random.Next(1, 3000));
            try { decoder.Feed(content.AsSpan(at, length)); }
            catch (InvalidDataException error) { trace.Add(1000 + run, "error", error.Message); decoder.Resync(); }
            trace.Add(1000 + run, "synchronized", decoder.Synchronized.ToString());
            at += length;
        }
    }
    // Reassembly alone: which bytes each segment releases, in which pieces.
    static void ReassemblyAlone(Trace trace, int run)
    {
        var random = new Random(9000 + run); var tcp = new TcpReceiveStream(); var data = new byte[60000]; random.NextBytes(data);
        var isn = (uint)random.Next() * 3u; if (run % 2 == 0) tcp.Start(isn);
        var cursor = 0;
        for (var step = 0; step < 400 && cursor < data.Length - 1; step++)
        {
            var at = Math.Clamp(cursor + (random.Next(3) == 0 ? random.Next(-3000, 6000) : 0), 0, data.Length - 1); var length = Math.Min(data.Length - at, random.Next(10) == 0 ? random.Next(1, 4) : random.Next(1, 1400));
            var payload = data.AsSpan(at, length).ToArray();
            if (random.Next(60) == 0) payload[0] ^= 0xff;
            if (random.Next(150) == 0) { tcp.Reset(); trace.Add(2000 + run, "reset", ""); }
            try
            {
                var chunks = tcp.Feed(unchecked(isn + 1 + (uint)at), payload).ToList();
                if (at <= cursor) cursor = Math.Max(cursor, at + length);
                foreach (var chunk in chunks) trace.Add(2000 + run, "chunk", $"{chunk.Length} {Convert.ToHexString(SHA256.HashData(chunk))[..12]}");
                trace.Add(2000 + run, "fed", $"{at} {length} {chunks.Count} {tcp.GapSince is not null}");
            }
            catch (InvalidDataException error) { trace.Add(2000 + run, "error", error.Message); tcp.Reset(); }
        }
    }
    // A connection's frames: the roster before the identity first, then a random mix.
    static byte[] Traffic(Random random)
    {
        var bytes = new List<byte>();
        bytes.AddRange(F(PartyRoster((Mate, MateCharacter, "Mate"), (Own, OwnCharacter, "Hero"))));
        bytes.AddRange(F(Identity(Own, "Hero")));
        var count = random.Next(150, 400);
        for (var i = 0; i < count; i++) bytes.AddRange(Piece(random, 0));
        return bytes.ToArray();
    }
    static byte Flag(Random random) => (byte)random.Next(0xf0, 0xff);
    static byte[] Piece(Random random, int depth)
    {
        switch (random.Next(24))
        {
            case 0: return [0x06, 0x00, 0x36];
            case 1: { var tail = new byte[8]; random.NextBytes(tail); return [0x0e, 0x00, 0x36, .. tail]; }
            case 2: return depth == 0 ? [0] : F([0x44, 0x01, 0]);
            case 3: case 4: return F(RandomSnapshot(random));
            case 5: case 6: return F(Update((byte)random.Next(2), Stat(Tags[random.Next(4)], random.Next(4) == 0 ? Counter : Odyle, (uint)random.Next(0, 3000), (uint)random.Next(0, 3000))));
            case 7: return F(random.Next(6) == 0 ? Identity(Other, "Switched") : Identity(Own, "Hero"));
            case 8: return F([Flag(random), .. Readable(random)]);
            case 9: { var body = new byte[random.Next(1, 40)]; random.NextBytes(body); return F([Flag(random), (byte)random.Next(0x01, 0xf0), (byte)random.Next(0x62, 0x90), .. body]); }
            case 10: case 11:
                {
                    if (depth >= 2) return F(RandomSnapshot(random));
                    var inner = new List<byte>(F([0xbb, 0xbb]));
                    var n = random.Next(1, 6); for (var k = 0; k < n; k++) inner.AddRange(Piece(random, depth + 1));
                    var bundle = Bundle(inner.ToArray());
                    return F(random.Next(3) == 0 ? [Flag(random), .. bundle] : bundle);
                }
            case 12:
                return F(random.Next(5) switch
                {
                    0 => PartyRoster((Mate, MateCharacter, "Mate"), (Own, OwnCharacter, "Hero")),
                    1 => ForceRoster((Own, OwnCharacter, "Hero"), (Mate, MateCharacter, "Mate"), (Foe, 0x0901_0000_0000_0001, "Rival")),
                    2 => [0x1f, 0x92, 1],
                    3 => [0x2e, 0x96, .. ForceKey, 2],
                    _ => PartyRoster((Mate, MateCharacter, "Mate")),
                });
            case 13:
                {
                    byte[] frame = random.Next(4) switch { 0 => MapLoad((uint)random.Next(600000, 600100), 100.5f, -20.25f, 30, 1.5f), 1 => Arrival(5, 6, 7), 2 => Effect(Own, 1, 2, 3), _ => Effect(Other, 4, 5, 6) };
                    return F(random.Next(4) == 0 ? [Flag(random), .. frame] : frame);
                }
            case 14:
                {
                    byte[] frame = random.Next(4) switch { 0 => Player(Foe, "Rival", 2304), 1 => Credit(Foe, Own, "Hero", 2304), 2 => Damage(Foe, Own), _ => HitNotice(Own, Foe) };
                    return F(random.Next(3) == 0 ? [Flag(random), .. frame] : frame);
                }
            case 15 when depth == 0 && random.Next(15) == 0: { var large = new byte[random.Next(66000, 90000)]; random.NextBytes(large); large[0] = 0x7a; large[1] = 0x01; return F(large); }
            default:
                {
                    var body = new byte[random.Next(0, random.Next(10) == 0 ? 2000 : 200)]; random.NextBytes(body);
                    byte first = (byte)random.Next(0x01, 0xf0), second = (byte)random.Next(256);
                    return F([first, second == 0x61 ? (byte)0x62 : second, .. body]);
                }
        }
    }
    static byte[] Readable(Random random) => random.Next(4) switch
    {
        0 => RandomSnapshot(random),
        1 => Update(0, Stat(0x08, Odyle, 0, (uint)random.Next(0, 2000))),
        2 => Identity(Own, "Hero"),
        _ => Bundle(F(Update(1, Stat(0x0c, Odyle, (uint)random.Next(0, 200), (uint)random.Next(0, 2000))))),
    };
    static byte[] RandomSnapshot(Random random)
    {
        var records = new List<byte[]>(); var count = random.Next(0, 45); var odyleAt = random.Next(-1, count);
        for (var i = 0; i < count; i++) records.Add(i == odyleAt ? Stat(Tags[random.Next(4)], Odyle, (uint)random.Next(0, 2000), (uint)random.Next(0, 3000)) : Stat(Tags[random.Next(4)], 10000001 + (uint)i * 1000, (uint)random.Next(), (uint)random.Next(1000)));
        if (random.Next(8) == 0) records.Insert(random.Next(records.Count + 1), [0x10, 1, 2, 3, 4, 5]);
        return [0x0b, 0x61, .. V((uint)records.Count), .. records.SelectMany(r => r)];
    }
    static byte[] Stat(byte tag, uint resource, uint value = 0, uint bonus = 0)
    {
        var id = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(id, resource);
        var bytes = new List<byte> { tag }; bytes.AddRange(id);
        if ((tag & 4) != 0) bytes.AddRange(V(value));
        if ((tag & 8) != 0) bytes.AddRange(V(bonus));
        return bytes.ToArray();
    }
    static byte[] Update(byte direction, byte[] record) => [0x0c, 0x61, direction, .. record, .. direction == 1 ? new byte[] { 1, 20, 0, 0, 0 } : [0]];
    static byte[] Identity(ulong id, string name)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(tail, 2304); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), 25);
        return [0x33, 0x36, .. V(id), 0, 0, 0, 0, 1, (byte)text.Length, .. text, .. tail];
    }
    static byte[] PartyRoster(params (ulong Entity, ulong Character, string Name)[] members) =>
        [0x00, 0x92, 0, .. PartyKey, .. U64(members[0].Character), 5, 0, 0, 0, 2, 1, 0, 2, (byte)members.Length, .. members.SelectMany((m, i) => Member((byte)(i + 1), m.Entity, m.Character, m.Name)), 1, 4];
    static byte[] ForceRoster(params (ulong Entity, ulong Character, string Name)[] members) =>
        [0x02, 0x96, 0, .. ForceKey, .. U64(members[0].Character), 0, 20, 0, 0, 0, 2, 1, 0, 1, .. PartyKey, 1, 5, 0, 0, 0, 3, (byte)members.Length, .. members.SelectMany((m, i) => Member((byte)(i + 1), m.Entity, m.Character, m.Name))];
    static byte[] Member(byte slot, ulong entity, ulong character, string name)
    {
        var id = Encoding.ASCII.GetBytes($"{entity:X8}-0000-4000-8000-{character:X12}"[..36]); var text = Encoding.UTF8.GetBytes(name);
        var entityBytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(entityBytes, (uint)entity);
        return [3, slot, .. entityBytes, 0, 9, 0, 9, 36, .. id, .. U64(character), (byte)text.Length, .. text, 24, 0, 0, 0, .. new byte[24], 7, 6, .. "Legion"u8, 2, 0, 0, 0];
    }
    static byte[] MapLoad(uint map, float x, float y, float z, float heading)
    {
        var p = new byte[46]; BinaryPrimitives.WriteUInt32LittleEndian(p, 3); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), map); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8), 2_611_155);
        Floats(p.AsSpan(16), x, y, z, heading); p[32] = 2; return [0x21, 0x36, .. p];
    }
    static byte[] Arrival(float x, float y, float z) { var p = new byte[24]; Floats(p.AsSpan(6), x, y, z); p[18] = 3; return [0x23, 0x36, .. p]; }
    static byte[] Effect(ulong entity, float x, float y, float z)
    {
        var middle = new byte[21]; middle[0] = 0x36; BinaryPrimitives.WriteUInt32LittleEndian(middle.AsSpan(1), 231); BinaryPrimitives.WriteUInt32LittleEndian(middle.AsSpan(5), 200);
        BinaryPrimitives.WriteUInt64LittleEndian(middle.AsSpan(13), 1_792_859_851_517);
        var position = new byte[12]; Floats(position, x, y, z);
        return [0x2a, 0x38, .. V(entity), 1, 0x11, .. middle, .. V(entity), 1, 0, .. position];
    }
    static void Floats(Span<byte> target, params float[] values) { for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], values[i]); }
    static byte[] Player(ulong entity, string name, ushort server)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[10]; BinaryPrimitives.WriteUInt16LittleEndian(tail, server); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), 24);
        return [0x45, 0x36, .. V(entity), 0, 0, 0, 0, 1, (byte)text.Length, .. text, .. tail];
    }
    static byte[] Credit(ulong victim, ulong killer, string name, ushort server)
    {
        var text = Encoding.UTF8.GetBytes(name); var id = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(id, server);
        return [0x04, 0x8d, .. V(victim), 7, 0, 0, 0, .. V(killer), .. id, (byte)text.Length, .. text, 0, 0];
    }
    static byte[] HitNotice(ulong actor, ulong target) => [0x03, 0x38, .. V(actor), 0, .. V(target), 0x15, 0xcd, 0x5b, 0x07, 1, 9, 9, 9, 9];
    static byte[] Damage(ulong target, ulong actor) => [0x04, 0x38, .. V(target), 6, 0, .. V(actor), 0x15, 0xcd, 0x5b, 0x07, 1, 3, 9, 9];
    static byte[] Bundle(byte[] frames)
    {
        var encoded = new byte[LZ4Codec.MaximumOutputSize(frames.Length)]; var length = LZ4Codec.Encode(frames, encoded);
        var bundle = new byte[6 + length]; bundle[0] = bundle[1] = 255;
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(2), frames.Length); encoded.AsSpan(0, length).CopyTo(bundle.AsSpan(6));
        return bundle;
    }
    static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
    static byte[] V(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 127); value >>= 7; bytes.Add((byte)(b | (value > 0 ? 128 : 0))); } while (value > 0);
        return bytes.ToArray();
    }
    static byte[] F(byte[] content) => [.. V((ulong)content.Length + 4), .. content];
    static void Expect(bool condition, string name) { if (!condition) throw new Exception("Capture check failed: " + name); }
}
