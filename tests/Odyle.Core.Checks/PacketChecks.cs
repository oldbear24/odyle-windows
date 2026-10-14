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
        // Decrease form: direction 0, one value, one trailing byte. The value is the new total
        // (reference measurement: base 40 + bonus 600, spend 80, packet value 560).
        var update = Frame(new byte[] { 0x0c, 0x61, 0, 8, 1, 7, 20, 0 });
        var otherOwner = Frame(new byte[] { 0x0c, 0x61, 0, 8, 1, 9, 30, 0 });
        decoder.Feed(update);
        Assert(candidates.Single() is { Total: 20, Source: CandidateSource.Update, OwnerConfirmed: false, Primary: null, Bonus: null }, "a single-value update is a complete total without a snapshot");
        Assert(observations.Single().Mode == 8 && observations.Single().First == 20, "update diagnostics");
        decoder.Feed(otherOwner); Assert(candidates.Count == 1, "a second stat id is ignored while the first is adopted");
        decoder.Feed(Frame(new byte[] { 0x0c, 0x61, 0, 8, 1, 0, 30, 0 })); Assert(candidates.Count == 1, "owner id zero is never adopted");
        var snapshot = Frame(new byte[] { 0x0b, 0x61, 0x0c, 1, 7, 100, 50 });
        decoder.Feed(snapshot.AsSpan(0, 3)); Assert(candidates.Count == 1, "fragment incomplete");
        decoder.Feed(snapshot.AsSpan(3)); Assert(candidates.Last() is { Total: 150, Source: CandidateSource.Snapshot, OwnerConfirmed: true, Primary: 100, Bonus: 50 }, "snapshot sum");
        decoder.Feed(update); Assert(candidates.Last() is { Total: 20, Source: CandidateSource.Update, OwnerConfirmed: true, Primary: null }, "a single-value update replaces the total; its split is unknown");
        // Increase form: direction 1, flag byte and a 32-bit delta; two values repeat base and bonus.
        decoder.Feed(Frame(new byte[] { 0x0c, 0x61, 1, 0x0c, 1, 7, 100, 70, 1, 20, 0, 0, 0 }));
        Assert(candidates.Last() is { Total: 170, Primary: 100, Bonus: 70, Source: CandidateSource.Update, OwnerConfirmed: true }, "a two-value increase carries base and bonus");
        var beforeOtherStat = candidates.Count;
        decoder.Feed(Frame(new byte[] { 0x0c, 0x61, 0, 4, 12, 7, 20, 0 }));
        Assert(candidates.Count == beforeOtherStat, "unrelated stat field never emits Odyle");
        decoder.Feed(update); Assert(candidates.Count == beforeOtherStat + 1 && candidates.Last().Total == 20, "unrelated stat update preserves the Odyle owner");
        decoder.Feed(Frame(new byte[] { 0x0c, 0x61, 1, 8, 1, 7, 20, 0 }));
        Assert(candidates.Count == beforeOtherStat + 1, "an increase without its delta trailer is not a reading");
        var ownIdentity = Frame(new byte[] { 0x33, 0x36, 42, 0, 0, 0, 0, 0 });
        decoder.Feed(ownIdentity); decoder.Feed(ownIdentity); decoder.Feed(update);
        Assert(changed == 0 && candidates.Last() is { Total: 20, OwnerConfirmed: true }, "first and repeated own identity keep the snapshot-confirmed owner");
        decoder.Feed(Frame(new byte[] { 0x33, 0x36, 43, 0, 0, 0, 0, 0 }));
        var beforeSwitch = candidates.Count; decoder.Feed(otherOwner);
        Assert(changed == 1 && candidates.Count == beforeSwitch, "after a character switch only a confirmed owner is read");
        decoder.Feed(update); Assert(candidates.Last() is { Total: 20, OwnerConfirmed: true }, "the confirmed owner keeps reading after a switch");
        decoder.Reset(); changed = 0; decoder.Feed(snapshot);
        var count = candidates.Count;
        decoder.Feed(otherOwner);
        decoder.Feed(Frame(new byte[] { 0x8d, 0, 3, 7, 50 })); Assert(candidates.Count == count, "other entity and sprint ignored");
        decoder.Feed(Frame(new byte[] { 0x33, 0x36 })); decoder.Feed(update);
        Assert(changed == 1 && candidates.Last() is { Total: 20, OwnerConfirmed: true }, "a character change keeps the confirmed Odyle owner");
        // Owners confirmed by another stream or an earlier run are trusted before any snapshot.
        var owners = new KnownOwners(); owners.Add(7);
        var trusted = new ExperimentalOdyleDecoder(owners: owners); var trustedSeen = new List<PacketCandidate>(); trusted.Candidate += trustedSeen.Add;
        trusted.Feed(otherOwner); Assert(trustedSeen.Count == 0, "with a known owner other stat ids are not adopted");
        trusted.Feed(update); Assert(trustedSeen.Single() is { Total: 20, OwnerConfirmed: true }, "a known owner is read before any snapshot");
        var shared = new KnownOwners();
        var provisional = new ExperimentalOdyleDecoder(owners: shared); var provisionalSeen = new List<PacketCandidate>(); provisional.Candidate += provisionalSeen.Add;
        provisional.Feed(otherOwner); Assert(provisionalSeen.Single() is { EntityId: 9, OwnerConfirmed: false }, "the first stat id is adopted provisionally");
        shared.Add(7);
        provisional.Feed(update); Assert(provisionalSeen.Last() is { EntityId: 7, OwnerConfirmed: true }, "a confirmed owner replaces an adopted one");
        provisional.Feed(otherOwner); Assert(provisionalSeen.Count == 2, "the adopted id is dropped once an owner is confirmed");
        decoder.Reset(); candidates.Clear();
        var encoded = new byte[LZ4Codec.MaximumOutputSize(snapshot.Length)]; var length = LZ4Codec.Encode(snapshot, encoded);
        var compressed = new byte[6 + length]; compressed[0] = compressed[1] = 255;
        BinaryPrimitives.WriteInt32LittleEndian(compressed.AsSpan(2), snapshot.Length); encoded.AsSpan(0, length).CopyTo(compressed.AsSpan(6));
        decoder.Feed(Frame(compressed)); Assert(candidates.Single().Total == 150, "LZ4 frame");
        var fresh = new ExperimentalOdyleDecoder(); var freshSeen = new List<PacketCandidate>(); fresh.Candidate += freshSeen.Add;
        fresh.Feed(Frame(new byte[] { 0x0b, 0x61, 0x0c, 1, 7, 100, 50, 0x0c, 1, 8, 100, 30 }));
        Assert(freshSeen.Count == 0, "ambiguous snapshot is unknown");
        fresh.Feed(update); Assert(freshSeen.Single() is { Total: 20, OwnerConfirmed: false }, "live updates are still read after an ambiguous snapshot");
        var invalid = new byte[] { 255, 255, 255, 255, 255, 1 };
        Reject(() => new ExperimentalOdyleDecoder().Feed(invalid), "oversized framing");
        var invalidLz4 = new byte[] { 255, 255, 255, 255, 127, 0 };
        Reject(() => new ExperimentalOdyleDecoder().Feed(Frame(invalidLz4)), "bounded decompression");
        var midstream = new ExperimentalOdyleDecoder(startsAligned: false); var seen = new List<PacketCandidate>();
        midstream.Candidate += seen.Add;
        midstream.Feed(snapshot); Assert(seen.Count == 0 && !midstream.Synchronized, "no reading before a frame boundary");
        midstream.Feed(new byte[] { 0x06, 0x00 }); Assert(seen.Count == 0, "split heartbeat waits");
        midstream.Feed(new byte[] { 0x36 });
        midstream.Feed(Frame(new byte[] { 0xf4, 0x0b, 0x61, 0x0c, 1, 7, 0, 0x86, 3 }));
        Assert(seen.Single().Primary == 0 && seen.Single().Bonus == 390 && seen.Single().Total == 390 && midstream.Synchronized, "flagged 0 plus 390 after heartbeat");
        var afterLogin = new ExperimentalOdyleDecoder(startsAligned: false); var lateSeen = new List<PacketCandidate>(); afterLogin.Candidate += lateSeen.Add;
        afterLogin.Feed(new byte[] { 0x06, 0x00, 0x36 }); afterLogin.Feed(Frame(new byte[] { 0xf4, 0x0c, 0x61, 1, 8, 1, 7, 0x86, 3, 1, 40, 0, 0, 0 }));
        Assert(lateSeen.Single() is { Total: 390, Source: CandidateSource.Update, OwnerConfirmed: false, Primary: null }, "a reader started after login reads the next flagged update");
        decoder.Reset(); candidates.Clear();
        decoder.Feed(Frame(new byte[] { 0x0b, 0x61, 0, 8, 1, 7, 0x86, 3 }));
        Assert(candidates.Single().Primary == 0 && candidates.Single().Bonus == 390 && candidates.Single().ParserVersion == ExperimentalOdyleDecoder.Version, "single-component snapshot carries the adapter name");
        decoder.Reset(); candidates.Clear();
        decoder.Feed(Frame(new byte[] { 0x0b, 0x61, 0, 8, 1, 7, 0x86, 3, 0x0c, 1, 9, 20, 30 }));
        Assert(candidates.Count == 0, "ambiguous mixed snapshot is unknown");
        Streams();
        Console.WriteLine("Synthetic TCP, framing, LZ4, identity, owner, candidate and connection table checks passed.");
    }
    // The connection table of one capture interface: only bytes that reach a decoder count
    // as game traffic, and a connection blocked by a lost segment or a decoding error says
    // so and counts nothing until the game reconnects.
    static void Streams()
    {
        var statuses = new List<(CaptureStatus Kind, string Message)>(); var candidates = new List<PacketCandidate>();
        var streams = new GameStreams(new KnownOwners(), decoder => decoder.Candidate += candidates.Add, (kind, message) => statuses.Add((kind, message)));
        var game = new TcpFlow("10.0.0.2", 50000, "203.0.113.5", 13328); var snapshot = Frame(new byte[] { 0x0b, 0x61, 0x0c, 1, 7, 100, 50 });
        Assert(streams.Feed(game, 1000, 0x02, []) == 0 && statuses.Last().Kind == CaptureStatus.Listening, "a new connection waits for the snapshot");
        Assert(streams.Feed(game, 1001, 0x18, snapshot) == snapshot.Length && candidates.Single().Total == 150, "decoded bytes count as game traffic");
        Assert(streams.Feed(game, 1001, 0x18, snapshot) == 0, "a retransmission adds nothing");
        var next = 1001u + (uint)snapshot.Length;
        Assert(streams.Feed(game, next + 10, 0x18, snapshot) == 0, "data after a lost segment waits and does not count");
        streams.Maintain([game], DateTimeOffset.UtcNow.AddSeconds(1)); Assert(!streams.IsBlocked(game), "a short gap is waited out");
        streams.Maintain([game], DateTimeOffset.UtcNow.AddSeconds(4));
        Assert(streams.IsBlocked(game) && statuses.Last() is { Kind: CaptureStatus.Problem, Message: var lost } && lost.Contains("lost"), "a persistent gap blocks the connection and says so");
        Assert(streams.Feed(game, next, 0x18, new byte[10]) == 0 && streams.Feed(game, next + 10, 0x18, snapshot) == 0 && candidates.Count == 1, "a blocked connection is not decoded and counts no bytes");
        streams.Feed(game, next, 0x11, []); Assert(streams.Count == 0, "FIN ends the connection");
        var relog = game with { LocalPort = 50001 };
        streams.Feed(relog, 5000, 0x02, []);
        Assert(streams.Feed(relog, 5001, 0x18, snapshot) == snapshot.Length && candidates.Count == 2 && statuses.Last().Kind == CaptureStatus.Listening, "a new connection is read again");
        Assert(streams.Feed(relog, 5001, 0x18, snapshot.Select(b => (byte)~b).ToArray()) == 0 && streams.IsBlocked(relog) && statuses.Last() is { Kind: CaptureStatus.Problem, Message: var damaged } && damaged.StartsWith("Unsupported"), "a conflicting retransmission blocks only that connection");
        var midstream = game with { LocalPort = 50002 };
        Assert(streams.Feed(midstream, 9000, 0x18, new byte[] { 1, 2, 3 }) == 3 && statuses.Last().Kind == CaptureStatus.Midstream, "a connection joined after login waits for a heartbeat");
        streams.Maintain([midstream], DateTimeOffset.UtcNow); Assert(streams.Count == 1 && !streams.IsBlocked(midstream), "connections the game closed are forgotten");
        Assert(streams.Feed(game with { LocalPort = 50003 }, 1, 0x10, []) == 0 && streams.Count == 1, "an empty ACK opens no connection");
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
