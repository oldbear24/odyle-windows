using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using Odyle.Core;

static class PacketChecks
{
    // Synthetic protocol fixtures exercise transport and confidence behavior; they are not
    // captures. The stat fixtures rebuild the shapes the owner's decoder log recorded on
    // Global (EU Lumiel, 2026-10-14 and 15): record layout, resource ids and values.
    const uint Odyle = ExperimentalOdyleDecoder.OdyleResource, Counter = 80000001;
    static readonly byte[] Tags = [0x00, 0x04, 0x08, 0x0c];
    public static void Run()
    {
        Tcp();
        Records();
        Snapshots();
        Updates();
        Identities();
        Framing();
        Fuzz();
        Streams();
        Console.WriteLine("Synthetic TCP, framing, heartbeat sync, LZ4, stat record, identity, candidate, fuzz and connection table checks passed.");
    }
    static void Tcp()
    {
        var tcp = new TcpReceiveStream();
        Assert(tcp.Feed(100, new byte[] { 1, 2 }).Single().SequenceEqual(new byte[] { 1, 2 }), "initial segment");
        Assert(!tcp.Feed(104, new byte[] { 5, 6 }).Any() && tcp.GapSince is not null, "hold gap");
        Assert(tcp.Feed(102, new byte[] { 3, 4 }).SelectMany(x => x).SequenceEqual(new byte[] { 3, 4, 5, 6 }) && tcp.GapSince is null, "reorder");
        Assert(!tcp.Feed(100, new byte[] { 1, 2 }).Any(), "duplicate ignored");
        Reject(() => tcp.Feed(101, new byte[] { 9, 3 }), "conflicting retransmission");
        Reject(() => tcp.Feed(700000, new byte[] { 1 }), "bounded gap");
        tcp.Reset(); tcp.Feed(uint.MaxValue - 1, new byte[] { 1, 2 });
        Assert(tcp.Feed(0, new byte[] { 3 }).Single()[0] == 3, "sequence rollover");
        tcp.Reset(); tcp.Feed(100, new byte[] { 1 }); tcp.Feed(104, new byte[] { 5, 6 });
        Reject(() => tcp.Feed(103, new byte[] { 4, 9 }), "pending overlap");
    }
    // [tag][u32 LE resource id][varint base if tag has 4][varint bonus if tag has 8].
    static void Records()
    {
        // 60000001 is 01 87 93 03 on the wire. The adapter before v2 matched "0c 01" and read the
        // varint 87 93 03 as owner 51591, and 80000001 (01 B4 C4 04) as owner 74292.
        Assert(Stat(0x0c, Odyle, 1, 2)[1..5].SequenceEqual(new byte[] { 0x01, 0x87, 0x93, 0x03 }) && Stat(0x0c, Counter, 3, 1)[1..5].SequenceEqual(new byte[] { 0x01, 0xb4, 0xc4, 0x04 }), "resource ids on the wire");
        foreach (var (tag, value, bonus) in new (byte, uint, uint)[] { (0x0c, 15, 940), (0x08, 0, 390), (0x04, 120, 0), (0x00, 0, 0) })
        {
            var bytes = Stat(tag, Odyle, value, bonus); var offset = 0;
            Assert(StatRecord.TryRead(bytes, ref offset, out var record) && record == new StatRecord(tag, Odyle, value, bonus) && offset == bytes.Length, $"tag {tag:X2} record");
        }
        var at = 0;
        Assert(!StatRecord.TryRead(new byte[] { 0x10, 1, 0x87, 0x93, 3, 5 }, ref at, out _) && at == 0, "an unknown tag is not a record");
        Assert(!StatRecord.TryRead(new byte[] { 0x0c, 1, 0x87, 0x93, 3, 0x85 }, ref at, out _) && at == 0, "a truncated value is not a record");
        Assert(!StatRecord.TryRead(new byte[] { 0x04, 1, 0x87, 0x93, 3, 0xff, 0xff, 0xff, 0xff, 0xff, 1 }, ref at, out _) && at == 0, "an oversized value is not a record");
        Assert(!ExperimentalOdyleDecoder.TryVarint(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 1 }, ref at, out _) && at == 0, "an oversized varint answers false instead of throwing");
    }
    static void Snapshots()
    {
        var decoder = new ExperimentalOdyleDecoder(); var seen = new List<PacketCandidate>(); var observed = new List<OdyleSnapshotObservation>(); var missing = 0;
        decoder.Candidate += seen.Add; decoder.SnapshotObserved += observed.Add; decoder.SnapshotWithoutOdyle += () => missing++;
        // The snapshot after a teleport: Odyle 15 + 940 and the second counter 3 + 1 among other stats.
        decoder.Feed(Frame(Snapshot(Filler(0), Stat(0x0c, Odyle, 15, 940), Filler(1), Stat(0x0c, Counter, 3, 1), Filler(2))));
        Assert(seen.Single() is { Total: 955, Base: 15, Bonus: 940, Source: CandidateSource.Snapshot, ParserVersion: ExperimentalOdyleDecoder.Version } && observed.Single() is { Records: 5, Read: true, Fields: [{ Tag: 0x0c, Base: 15, Bonus: 940 }] } && missing == 0, "a two-field snapshot reads resource 60000001 only");
        // The login snapshot with no base: tag 08, whatever byte comes before it.
        decoder.Feed(Frame(Snapshot(Stat(0x04, 10000001, 7), Stat(0x08, Odyle, 0, 390))));
        Assert(seen.Last() is { Total: 390, Base: 0, Bonus: 390 }, "0 + 390 (bonus only)");
        decoder.Feed(Frame(Snapshot(Filler(3), Stat(0x0c, Odyle, 90, 990))));
        Assert(seen.Last() is { Total: 1080, Base: 90, Bonus: 990 }, "90 + 990");
        // No bonus (tag 04) and nothing at all (tag 00) are readings too.
        decoder.Feed(Frame(Snapshot(Stat(0x04, Odyle, 120), Filler(4))));
        Assert(seen.Last() is { Total: 120, Base: 120, Bonus: 0 }, "base only (tag 04)");
        decoder.Feed(Frame(Snapshot(Filler(5), Stat(0x00, Odyle))));
        Assert(seen.Last() is { Total: 0, Base: 0, Bonus: 0 } && seen.Count == 5 && missing == 0, "0 / 0 (tag 00)");
        // A snapshot understood whole without the record: the character has no Odyle yet. A lone
        // record of the second counter is never taken for it.
        decoder.Feed(Frame(Snapshot(Filler(6), Stat(0x0c, Counter, 3, 1), Filler(7))));
        Assert(seen.Count == 5 && missing == 1 && observed.Last() is { Records: 3, Read: false, Fields.Length: 0 }, "a lone 80000001 snapshot is not Odyle; the snapshot says Odyle is missing");
        // A layout with an unknown tag is not walked; the Odyle record is then found by its id.
        decoder.Feed(Frame(Snapshot(Filler(8), [0x10, 1, 2, 3, 4, 5], Stat(0x0c, Odyle, 105, 1110))));
        Assert(seen.Last() is { Total: 1215 } && observed.Last() is { Records: null, Read: true }, "the id search reads an unwalked layout");
        decoder.Feed(Frame(Snapshot([0x10, 1, 2, 3, 4, 5], Stat(0x0c, Counter, 2, 1))));
        Assert(seen.Count == 6 && missing == 1, "an unwalked layout without the record says nothing about Odyle");
        // A count that does not match the records is not walked either.
        decoder.Feed(Frame([0x0b, 0x61, 9, .. Stat(0x0c, Counter, 3, 1)]));
        Assert(seen.Count == 6 && missing == 1 && observed.Last().Records is null, "a wrong record count is no proof of a missing record");
        // Two disagreeing records, a total above 1,000,000 and the id after a byte that is no tag are not read.
        decoder.Feed(Frame([0x0b, 0x61, 9, .. Stat(0x0c, Odyle, 1, 2), 0x77, .. Stat(0x0c, Odyle, 3, 4)]));
        decoder.Feed(Frame(Snapshot(Stat(0x0c, Odyle, 900000, 200000))));
        decoder.Feed(Frame([0x0b, 0x61, 0x99, 0x01, 0x87, 0x93, 0x03, 5, 5]));
        decoder.Feed(Frame([0x0b, 0x61]));
        Assert(seen.Count == 6 && missing == 1, "ambiguous, oversized, untagged and empty snapshots are not read");
    }
    static void Updates()
    {
        var decoder = new ExperimentalOdyleDecoder(); var seen = new List<PacketCandidate>(); var updates = new List<OdyleUpdateObservation>();
        decoder.Candidate += seen.Add; decoder.UpdateObserved += updates.Add;
        // No snapshot needed (a reader started after login): the regeneration tick, +15 to base
        // every three hours, repeats base and bonus.
        decoder.Feed(Frame(Update(0, Stat(0x0c, Odyle, 15, 895))));
        Assert(seen.Single() is { Total: 910, Base: 15, Bonus: 895, Source: CandidateSource.Update } && updates.Single() is { Resource: Odyle, Tag: 0x0c, Base: 15, Bonus: 895, TrailerBytes: 1, Direction: 0 }, "a regeneration tick is a complete reading");
        // A spend drains base before bonus: 15 + 895 less 40 leaves 0 + 870, a bonus-only record.
        decoder.Feed(Frame(Update(0, Stat(0x08, Odyle, 0, 870))));
        Assert(seen.Last() is { Total: 870, Base: 0, Bonus: 870 }, "a spend update");
        // An increase ends with a flag byte and a 32-bit delta.
        decoder.Feed(Frame(Update(1, Stat(0x08, Odyle, 0, 990))));
        decoder.Feed(Frame(Update(1, Stat(0x0c, Odyle, 90, 1000))));
        decoder.Feed(Frame(Update(0, Stat(0x04, Odyle, 105))));
        Assert(seen.Select(c => c.Total).SequenceEqual([910, 870, 990, 1090, 105]), "increases and a base-only update");
        // The second counter's updates (logged as 3 + 1, then 2 + 1) are never Odyle, and are logged with their id.
        decoder.Feed(Frame(Update(0, Stat(0x0c, Counter, 3, 1)))); decoder.Feed(Frame(Update(0, Stat(0x0c, Counter, 2, 1))));
        Assert(seen.Count == 5 && updates.TakeLast(2).All(u => u is { Resource: Counter, Base: > 0 }), "updates of 80000001 are not read");
        // Unknown trailers and directions, totals above 1,000,000 and other stats' records are not readings.
        decoder.Feed(Frame(Update(1, Stat(0x08, Odyle, 0, 20), [0])));
        decoder.Feed(Frame([0x0c, 0x61, 2, .. Stat(0x08, Odyle, 0, 20), 0]));
        decoder.Feed(Frame(Update(0, Stat(0x0c, Odyle, 900000, 200000))));
        decoder.Feed(Frame(Update(0, Stat(0x04, 0x0a0b0c0a, 7))));
        decoder.Feed(Frame([0x0c, 0x61, 0, 0x0c, 1, 0x87]));
        Assert(seen.Count == 5, "unknown layouts and other stats are not read");
        decoder.Feed(Frame(Update(0, Stat(0x08, Odyle, 0, 20))));
        Assert(seen.Last().Total == 20, "other stats never disturb the Odyle reading");
    }
    static void Identities()
    {
        var decoder = new ExperimentalOdyleDecoder(); var changes = new List<ulong>(); var named = new List<GameIdentity>(); var seen = new List<PacketCandidate>();
        decoder.CharacterChanged += changes.Add; decoder.IdentityObserved += named.Add; decoder.Candidate += seen.Add;
        decoder.Feed(Frame(Snapshot(Stat(0x08, Odyle, 0, 390))));
        decoder.Feed(Frame(Identity(42, "TestHero"))); decoder.Feed(Frame(Identity(42, "TestHero")));
        Assert(changes.Count == 0 && named.Count == 2 && named[0] is { Id: 42, CharacterName: "TestHero", Server: "Lumiel", Class: 25 }, "the first and a repeated own identity are no switch");
        // A short, unreadable or other 3336 is no identity: it neither switches nor stops the reading.
        decoder.Feed(Frame([0x33, 0x36])); decoder.Feed(Frame([0x33, 0x36, 43, 0, 0, 0, 0, 0])); decoder.Feed(Frame(Identity(43, "$tutorial"))); decoder.Feed(Frame(Identity(43, "Other", job: 99)));
        decoder.Feed(Frame(Update(0, Stat(0x08, Odyle, 0, 380))));
        Assert(changes.Count == 0 && named.Count == 2 && seen.Last().Total == 380, "short or unreadable 3336 frames change nothing");
        decoder.Feed(Frame(Identity(43, "Switched")));
        Assert(changes.SequenceEqual([43UL]) && named.Last().CharacterName == "Switched", "a valid identity with another id is a switch");
        decoder.Feed(Frame(Identity(43, "Switched"))); Assert(changes.Count == 1, "its repeat is not another switch");
        decoder.Feed(Frame(Identity(44, "Brawler", job: 46)));
        Assert(named.Last() is { CharacterName: "Brawler", Class: 46 } && changes.Last() == 44, "a Brawler is named");
        // A resync keeps the character the decoder knew.
        decoder.Resync(); decoder.Feed([0x06, 0x00, 0x36, .. Frame(Identity(44, "Brawler", job: 46))]);
        Assert(decoder.Synchronized && changes.Count == 2, "the same character after a resync is no switch");
    }
    static void Framing()
    {
        var decoder = new ExperimentalOdyleDecoder(); var seen = new List<PacketCandidate>(); decoder.Candidate += seen.Add;
        var snapshot = Frame(Snapshot(Stat(0x0c, Odyle, 100, 50)));
        decoder.Feed(snapshot.AsSpan(0, 3)); Assert(seen.Count == 0, "fragment incomplete");
        decoder.Feed(snapshot.AsSpan(3)); Assert(seen.Single().Total == 150, "fragments join");
        decoder.Feed(Frame(Bundle(snapshot))); Assert(seen.Count == 2 && seen.Last().Total == 150, "LZ4 frame");
        // A bundle restoring more than 8 MB is skipped whole; the frames after it are read.
        var big = new byte[6 + 40000]; big[0] = big[1] = 0xff; BinaryPrimitives.WriteInt32LittleEndian(big.AsSpan(2), 9 * 1048576);
        decoder.Feed([.. Frame(big), .. snapshot]); Assert(seen.Count == 3, "an oversized bundle is skipped, not fatal");
        Reject(() => new ExperimentalOdyleDecoder().Feed(new byte[] { 255, 255, 255, 255, 255, 1 }), "oversized framing");
        Reject(() => new ExperimentalOdyleDecoder().Feed(Frame(new byte[] { 255, 255, 255, 255, 127, 0 })), "a size no LZ4 block can restore");
        // Midstream: a heartbeat is trusted once the frame after it starts with a valid length.
        var midstream = new ExperimentalOdyleDecoder(startsAligned: false); var midSeen = new List<PacketCandidate>(); midstream.Candidate += midSeen.Add;
        midstream.Feed(snapshot); Assert(midSeen.Count == 0 && !midstream.Synchronized, "no reading before a frame boundary");
        midstream.Feed(new byte[] { 0x06, 0x00 }); midstream.Feed(new byte[] { 0x36 });
        Assert(!midstream.Synchronized, "a heartbeat waits for the next frame's length");
        midstream.Feed(Frame([0xf4, .. Snapshot(Stat(0x08, Odyle, 0, 390))]));
        Assert(midSeen.Single() is { Base: 0, Bonus: 390, Total: 390 } && midstream.Synchronized, "flagged 0 plus 390 after a heartbeat");
        var falseMatch = new ExperimentalOdyleDecoder(startsAligned: false); var falseSeen = new List<PacketCandidate>(); falseMatch.Candidate += falseSeen.Add;
        falseMatch.Feed(new byte[] { 0x06, 0x00, 0x36, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01 });
        Assert(!falseMatch.Synchronized, "heartbeat bytes before an impossible length are not trusted");
        falseMatch.Feed([0x0e, 0x00, 0x36, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, .. Frame(Snapshot(Stat(0x08, Odyle, 0, 70)))]);
        Assert(falseMatch.Synchronized && falseSeen.Single().Total == 70, "the long heartbeat and zero padding synchronise");
    }
    // Random input never makes the stat, identity or framing code throw anything but the
    // framing error a connection resyncs on; random layouts read exactly their Odyle record.
    static void Fuzz()
    {
        var random = new Random(20261015);
        for (var round = 0; round < 400; round++)
        {
            var records = new List<byte[]>(); int? expected = null; var count = random.Next(0, 80); var odyleAt = random.Next(-1, count);
            for (var i = 0; i < count; i++)
            {
                var tag = Tags[random.Next(4)];
                if (i == odyleAt) { var value = (uint)random.Next(0, 2000); var bonus = (uint)random.Next(0, 5000); records.Add(Stat(tag, Odyle, value, bonus)); expected = (int)(((tag & 4) != 0 ? value : 0) + ((tag & 8) != 0 ? bonus : 0)); }
                else { var resource = (uint)random.Next(); records.Add(Stat(tag, resource == Odyle ? Counter : resource, (uint)random.Next(), (uint)random.Next())); }
            }
            var decoder = new ExperimentalOdyleDecoder(); int? read = null; var missing = false;
            decoder.Candidate += c => read = c.Total; decoder.SnapshotWithoutOdyle += () => missing = true;
            decoder.Feed(Frame(Snapshot([.. records])));
            Assert(read == expected && missing == (expected is null), "random stat layout " + round);
        }
        byte[][] opcodes = [[0x0b, 0x61], [0x0c, 0x61], [0x33, 0x36]];
        var framed = new ExperimentalOdyleDecoder();
        for (var round = 0; round < 3000; round++)
        {
            var body = new byte[random.Next(0, 300)]; random.NextBytes(body);
            // Often plant a tag and the Odyle id to reach the record paths.
            if (body.Length > 6 && random.Next(2) == 0) { var at = random.Next(0, body.Length - 5); body[at] = Tags[random.Next(4)]; BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 1), Odyle); }
            framed.Feed(Frame([.. opcodes[random.Next(opcodes.Length)], .. body]));
        }
        for (var round = 0; round < 2000; round++)
        {
            var bytes = new byte[random.Next(0, 400)]; random.NextBytes(bytes);
            if (bytes.Length > 3 && random.Next(3) == 0) { bytes[0] = 0x06; bytes[1] = 0; bytes[2] = 0x36; }
            try { new ExperimentalOdyleDecoder(random.Next(2) == 0).Feed(bytes); } catch (InvalidDataException) { }
        }
        var streams = new GameStreams(_ => { }, (_, _) => { }); uint sequence = 1;
        for (var round = 0; round < 2000; round++)
        {
            var segment = new byte[random.Next(0, 200)]; random.NextBytes(segment);
            streams.Feed(new TcpFlow("10.0.0.2", (ushort)(50100 + round / 250), "203.0.113.5", 13328), sequence, (byte)(random.Next(10) == 0 ? 0x02 : 0x18), segment);
            sequence += random.Next(4) == 0 ? (uint)random.Next(0, 1000) : (uint)segment.Length;
        }
    }
    // The connection table of one capture interface: only bytes that reach a decoder count as
    // game traffic; a lost segment or bytes that do not frame resync that connection, and too
    // many resyncs block it until the game reconnects.
    static void Streams()
    {
        var statuses = new List<(CaptureStatus Kind, string Message)>(); var candidates = new List<PacketCandidate>(); var changes = new List<ulong>();
        var streams = new GameStreams(decoder => { decoder.Candidate += candidates.Add; decoder.CharacterChanged += changes.Add; }, (kind, message) => statuses.Add((kind, message)));
        var game = new TcpFlow("10.0.0.2", 50000, "203.0.113.5", 13328); var snapshot = Frame(Snapshot(Stat(0x0c, Odyle, 100, 50))); var identity = Frame(Identity(42, "TestHero"));
        Assert(streams.Feed(game, 1000, 0x02, []) == 0 && statuses.Last().Kind == CaptureStatus.Listening, "a new connection waits for the snapshot");
        Assert(streams.Feed(game, 1001, 0x18, snapshot) == snapshot.Length && candidates.Single().Total == 150, "decoded bytes count as game traffic");
        Assert(streams.Feed(game, 1001, 0x18, snapshot) == 0, "a retransmission adds nothing");
        var next = 1001u + (uint)snapshot.Length; streams.Feed(game, next, 0x18, identity); next += (uint)identity.Length;
        Assert(streams.Feed(game, next + 10, 0x18, snapshot) == 0, "data after a lost segment waits and does not count");
        streams.Maintain([game], DateTimeOffset.UtcNow.AddSeconds(1)); Assert(statuses.Last().Kind == CaptureStatus.Listening, "a short gap is waited out");
        streams.Maintain([game], DateTimeOffset.UtcNow.AddSeconds(4));
        Assert(!streams.IsBlocked(game) && statuses.Last() is { Kind: CaptureStatus.Midstream, Message: var lost } && lost.Contains("lost") && lost.Contains("resyncing"), "a persistent gap resyncs the connection instead of blocking it");
        // Reading picks up at the next heartbeat, and the character the decoder knew is still known.
        byte[] resumed = [0x06, 0x00, 0x36, .. Frame(Update(0, Stat(0x08, Odyle, 0, 140))), .. identity];
        var later = next + 5000;
        Assert(streams.Feed(game, later, 0x18, resumed) == resumed.Length && candidates.Last().Total == 140 && changes.Count == 0 && statuses.Last().Kind == CaptureStatus.Resumed, "after a resync the next heartbeat resumes reading without a switch");
        later += (uint)resumed.Length;
        Assert(streams.Feed(game, later - 3, 0x18, new byte[] { 9, 9, 9 }) == 0 && !streams.IsBlocked(game) && statuses.Last().Kind == CaptureStatus.Midstream, "a conflicting retransmission resyncs");
        // Two heartbeats, then a length no frame can have.
        var unframed = new byte[] { 0x06, 0x00, 0x36, 0x06, 0x00, 0x36, 0x01, 0x00 };
        Assert(streams.Feed(game, 1, 0x18, unframed) == 0 && !streams.IsBlocked(game) && statuses.Last() is { Kind: CaptureStatus.Midstream, Message: var unreadable } && unreadable.StartsWith("Unreadable"), "bytes that do not frame resync");
        streams.Feed(game, 1, 0x18, unframed); streams.Feed(game, 1, 0x18, unframed);
        Assert(!streams.IsBlocked(game), "five resyncs in a minute are tolerated");
        streams.Feed(game, 1, 0x18, unframed);
        Assert(streams.IsBlocked(game) && statuses.Last() is { Kind: CaptureStatus.Problem, Message: var damaged } && damaged.StartsWith("Unsupported"), "a sixth resync within a minute blocks the connection and says so");
        Assert(streams.Feed(game, 1, 0x18, snapshot) == 0 && candidates.Last().Total == 140, "a blocked connection is not decoded and counts no bytes");
        streams.Feed(game, 1, 0x11, []); Assert(streams.Count == 0, "FIN ends the connection");
        var relog = game with { LocalPort = 50001 };
        streams.Feed(relog, 5000, 0x02, []);
        Assert(streams.Feed(relog, 5001, 0x18, snapshot) == snapshot.Length && candidates.Last().Total == 150 && statuses.Last().Kind == CaptureStatus.Listening, "a new connection is read again");
        var midstream = game with { LocalPort = 50002 };
        Assert(streams.Feed(midstream, 9000, 0x18, new byte[] { 1, 2, 3 }) == 3 && statuses.Last() is { Kind: CaptureStatus.Midstream, Message: var late } && late.Contains("within a few minutes"), "a connection joined after login waits for a heartbeat and the game's identity");
        streams.Maintain([midstream], DateTimeOffset.UtcNow); Assert(streams.Count == 1 && !streams.IsBlocked(midstream), "connections the game closed are forgotten");
        Assert(streams.Feed(game with { LocalPort = 50003 }, 1, 0x10, []) == 0 && streams.Count == 1, "an empty ACK opens no connection");
    }
    // One stat record; the tag decides which values are written.
    static byte[] Stat(byte tag, uint resource, uint value = 0, uint bonus = 0)
    {
        var id = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(id, resource);
        var bytes = new List<byte> { tag }; bytes.AddRange(id);
        if ((tag & 4) != 0) bytes.AddRange(Varint(value));
        if ((tag & 8) != 0) bytes.AddRange(Varint(bonus));
        return bytes.ToArray();
    }
    // Other stats around the Odyle record, of every tag; one value is far above Odyle's range.
    static byte[] Filler(int n) => Stat(Tags[n % 4], 10000001 + (uint)n * 1000, (uint)(n * 37), n == 2 ? 5000000u : (uint)(n * 11 + 1));
    static byte[] Snapshot(params byte[][] records) => [0x0b, 0x61, .. Varint((uint)records.Length), .. records.SelectMany(r => r)];
    // Direction 1 (an increase) ends with a flag byte and a 32-bit delta, direction 0 with one byte.
    static byte[] Update(byte direction, byte[] record, byte[]? trailer = null) => [0x0c, 0x61, direction, .. record, .. trailer ?? (direction == 1 ? [1, 20, 0, 0, 0] : [0])];
    // Own-character 3336: varint id, u32 mask, u8 mask, u8 name length, name, u16 server, u32 class.
    static byte[] Identity(ulong id, string name, ushort server = 2304, uint job = 25)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(tail, server); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), job);
        return [0x33, 0x36, .. Varint(id), 0, 0, 0, 0, 1, (byte)text.Length, .. text, .. tail];
    }
    static byte[] Bundle(byte[] frames)
    {
        var encoded = new byte[LZ4Codec.MaximumOutputSize(frames.Length)]; var length = LZ4Codec.Encode(frames, encoded);
        var bundle = new byte[6 + length]; bundle[0] = bundle[1] = 255;
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(2), frames.Length); encoded.AsSpan(0, length).CopyTo(bundle.AsSpan(6));
        return bundle;
    }
    static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 127); value >>= 7; bytes.Add((byte)(b | (value > 0 ? 128 : 0))); } while (value > 0);
        return bytes.ToArray();
    }
    static byte[] Frame(byte[] content) => [.. Varint((ulong)content.Length + 4), .. content];
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("Packet check failed: " + name); }
    static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection: " + name); }
}
