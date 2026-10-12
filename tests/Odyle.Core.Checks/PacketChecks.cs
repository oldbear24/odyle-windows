using System.Buffers.Binary;
using K4os.Compression.LZ4;
using Odyle.Core;

static class PacketChecks
{
    // Synthetic protocol fixtures exercise transport and confidence behavior;
    // they are not captures or evidence of compatibility with AION2 Global.
    public static void Run()
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

        var decoder = new ExperimentalOdyleDecoder(); var candidates = new List<PacketCandidate>(); var changed = 0;
        var observations = new List<OdyleUpdateObservation>(); decoder.UpdateObserved += observations.Add;
        decoder.Candidate += candidates.Add; decoder.CharacterChanged += () => changed++;
        var update = Frame(new byte[] { 0x0c, 0x61, 0, 8, 1, 7, 20, 0 });
        decoder.Feed(update); Assert(candidates.Count == 0, "dynamic update without baseline is unknown");
        Assert(observations.Single().Mode == 8 && observations.Single().First == 20, "diagnostics never imply a total without baseline");
        var snapshot = Frame(new byte[] { 0x0b, 0x61, 0x0c, 1, 7, 100, 50 });
        decoder.Feed(snapshot.AsSpan(0, 3)); Assert(candidates.Count == 0, "fragment incomplete");
        decoder.Feed(snapshot.AsSpan(3)); Assert(candidates.Last().Total == 150 && !candidates.Last().IsVerified, "sum remains unverified");
        decoder.Feed(update); Assert(candidates.Last().Total == 120, "dynamic update needs remembered base");
        var beforeOtherStat = candidates.Count;
        decoder.Feed(Frame(new byte[] { 0x0c, 0x61, 0, 4, 12, 7, 20, 0 }));
        Assert(candidates.Count == beforeOtherStat, "unrelated stat field never emits Odyle");
        decoder.Feed(update); Assert(candidates.Count == beforeOtherStat + 1 && candidates.Last().Total == 120, "unrelated stat update preserves Odyle baseline");
        var ownIdentity = Frame(new byte[] { 0x33, 0x36, 42, 0, 0, 0, 0, 0 });
        decoder.Feed(ownIdentity); decoder.Feed(ownIdentity); decoder.Feed(update);
        Assert(changed == 0 && candidates.Last().Total == 120, "first and repeated own identity preserve the stat baseline");
        decoder.Feed(Frame(new byte[] { 0x33, 0x36, 43, 0, 0, 0, 0, 0 })); decoder.Feed(update);
        Assert(changed == 1, "different own identity invalidates baseline");
        decoder.Reset(); changed = 0; decoder.Feed(snapshot);
        var count = candidates.Count;
        decoder.Feed(Frame(new byte[] { 0x0c, 0x61, 0, 8, 1, 9, 20, 0 }));
        decoder.Feed(Frame(new byte[] { 0x8d, 0, 3, 7, 50 })); Assert(candidates.Count == count, "other entity and sprint ignored");
        decoder.Feed(Frame(new byte[] { 0x33, 0x36 })); decoder.Feed(update);
        Assert(changed == 1 && candidates.Count == count, "character change invalidates baseline");
        decoder.Reset(); candidates.Clear();
        var encoded = new byte[LZ4Codec.MaximumOutputSize(snapshot.Length)]; var length = LZ4Codec.Encode(snapshot, encoded);
        var compressed = new byte[6 + length]; compressed[0] = compressed[1] = 255;
        BinaryPrimitives.WriteInt32LittleEndian(compressed.AsSpan(2), snapshot.Length); encoded.AsSpan(0, length).CopyTo(compressed.AsSpan(6));
        decoder.Feed(Frame(compressed)); Assert(candidates.Single().Total == 150 && !candidates.Single().IsVerified, "LZ4 frame");
        decoder.Reset(); candidates.Clear();
        decoder.Feed(Frame(new byte[] { 0x0b, 0x61, 0x0c, 1, 7, 100, 50, 0x0c, 1, 8, 100, 30 })); decoder.Feed(update);
        Assert(candidates.Count == 0, "ambiguous snapshot is unknown");
        var invalid = new byte[] { 255, 255, 255, 255, 255, 1 };
        Reject(() => new ExperimentalOdyleDecoder().Feed(invalid), "oversized framing");
        var invalidLz4 = new byte[] { 255, 255, 255, 255, 127, 0 };
        Reject(() => new ExperimentalOdyleDecoder().Feed(Frame(invalidLz4)), "bounded decompression");
        var midstream = new ExperimentalOdyleDecoder(startsAligned: false); var seen = new List<PacketCandidate>();
        midstream.Candidate += seen.Add;
        midstream.Feed(snapshot); Assert(seen.Count == 0, "no reading before a frame boundary");
        midstream.Feed(new byte[] { 0x06, 0x00 }); Assert(seen.Count == 0, "split heartbeat waits");
        midstream.Feed(new byte[] { 0x36 });
        midstream.Feed(Frame(new byte[] { 0xf4, 0x0b, 0x61, 0x0c, 1, 7, 0, 0x86, 3 }));
        Assert(seen.Single().Primary == 0 && seen.Single().Bonus == 390 && seen.Single().Total == 390 && !seen.Single().IsVerified, "flagged 0 plus 390 after heartbeat");
        decoder.Reset(); candidates.Clear();
        decoder.Feed(Frame(new byte[] { 0x0b, 0x61, 0, 8, 1, 7, 0x86, 3 }));
        Assert(candidates.Single().Primary == 0 && candidates.Single().Bonus == 390 && !candidates.Single().IsVerified, "single-component snapshot remains a hypothesis");
        decoder.Reset(); candidates.Clear();
        decoder.Feed(Frame(new byte[] { 0x0b, 0x61, 0, 8, 1, 7, 0x86, 3, 0x0c, 1, 9, 20, 30 }));
        Assert(candidates.Count == 0, "ambiguous mixed snapshot is unknown");
        Console.WriteLine("Synthetic TCP, framing, LZ4, identity and unverified-candidate checks passed; Global compatibility is not established.");
    }
    static byte[] Frame(byte[] content)
    {
        var prefix = new List<byte>(); var length = (uint)content.Length + 4;
        do { var b = (byte)(length & 127); length >>= 7; prefix.Add((byte)(b | (length > 0 ? 128 : 0))); } while (length > 0);
        return prefix.Concat(content).ToArray();
    }
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("Packet check failed: " + name); }
    static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection: " + name); }
}
