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
        decoder.Candidate += candidates.Add; decoder.CharacterChanged += () => changed++;
        var update = Frame(new byte[] { 0x0c, 0x61, 0, 8, 1, 7, 20, 0 });
        decoder.Feed(update); Assert(candidates.Count == 0, "dynamic update without baseline is unknown");
        var snapshot = Frame(new byte[] { 0x0b, 0x61, 0x0c, 1, 7, 100, 50 });
        decoder.Feed(snapshot.AsSpan(0, 3)); Assert(candidates.Count == 0, "fragment incomplete");
        decoder.Feed(snapshot.AsSpan(3)); Assert(candidates.Last().Total == 150 && !candidates.Last().IsVerified, "sum remains unverified");
        decoder.Feed(update); Assert(candidates.Last().Total == 120, "dynamic update needs remembered base");
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
