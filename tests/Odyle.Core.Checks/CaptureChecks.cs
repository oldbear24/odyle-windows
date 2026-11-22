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
        Console.WriteLine("Pinned decoder trace checks passed.");
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
