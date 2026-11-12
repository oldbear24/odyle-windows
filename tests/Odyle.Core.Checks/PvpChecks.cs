using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4;
using Odyle.Core;

// PvP stats: other players' records (45 36), kill credits (04 8D) and the own character's hits
// (03 38, 04 38), read only while the account has PvP stats on; what the tracker makes of them
// (kills, assists, deaths), the events waiting for the server and the wire contract of POST
// /api/odyle/pvp. Synthetic frames in the layouts public decoders document, never captures.
static class PvpChecks
{
    const ulong Own = 606, Foe = 5069, Mate = 7001, Other = 9100, Mob = 12000;
    const ushort OwnServer = 2304, FoeServer = 1304;
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T20:00:00Z");
    public static async Task Run()
    {
        Frames();
        Decoding();
        Tracking();
        Outbox();
        await Wire();
        Console.WriteLine("PvP frame, opt-in, tracking, outbox and wire checks passed.");
    }
    static void Frames()
    {
        Check(GamePvp.Player(Player(Foe, "Vexa")[2..], out var entity, out var name, out var server) && (entity, name, server) == (Foe, "Vexa", 0), "another player's record without a server");
        Check(GamePvp.Player(Player(Foe, "Vexa", FoeServer)[2..], out _, out name, out server) && (name, server) == ("Vexa", FoeServer), "another player's record with its server");
        Check(GamePvp.Player(Player(Foe, "Vexa", FoeServer, job: 99)[2..], out _, out _, out server) && server == 0, "a server is taken only with a class after it");
        Check(GamePvp.Player(Player(Foe, "Šárka")[2..], out _, out name, out _) && name == "Šárka", "a name in another script");
        foreach (var odd in new byte[][] { Player(0, "Vexa"), Player(Foe, "Ve xa"), Player(Foe, "1234"), Player(Foe, "Vexa")[..9], [0x45, 0x36, .. Varint(Foe), 0, 0, 0, 0, 0, 4, .. "Vexa"u8], [0x45, 0x36], [0x45, 0x36, .. Varint(Foe), 0, 0, 0, 0, 1, 0] })
            Check(!GamePvp.Player(odd.AsSpan(2), out _, out _, out _), "a record without an id, a name or its layout is not read");

        Check(GamePvp.Kill(Credit(Own, Foe, "Vexa", FoeServer)[2..], out var victim, out var killer, out var by, out var byServer) && (victim, killer, by, byServer) == (Own, Foe, "Vexa", FoeServer), "a kill credit names the victim and the killer");
        Check(GamePvp.Kill(Credit(Own, Mob)[2..], out victim, out killer, out by, out _) && (victim, killer, by) == (Own, Mob, ""), "a monster's kill has no player's name");
        Check(GamePvp.Kill(Credit(Own, Foe, "Vexa", 999)[2..], out _, out _, out by, out _) && by == "" && GamePvp.Kill(Credit(Own, Foe, "Ve-xa", FoeServer)[2..], out _, out _, out by, out _) && by == "", "a killer without a Global server or a readable name is no player");
        Check(!GamePvp.Kill([.. Varint(Mob), 0, 0, 0, 0, 0], out _, out _, out _, out _) && !GamePvp.Kill(Credit(0, Foe, "Vexa", FoeServer)[2..], out _, out _, out _, out _) && !GamePvp.Kill(Varint(Own), out _, out _, out _, out _), "a despawning summon and short credits are not kills");

        Check(GamePvp.Hit(HitNotice(Own, Foe), out var actor, out var target) && (actor, target) == (Own, Foe), "a hit notice says who hit whom");
        Check(GamePvp.Hit(Damage(Foe, Own), out actor, out target) && (actor, target) == (Own, Foe) && GamePvp.Hit(Damage(Foe, Own, chained: true), out actor, out target) && (actor, target) == (Own, Foe), "a damage record says who was hit by whom");
        Check(!GamePvp.Hit(HitNotice(Own, Own), out _, out _) && !GamePvp.Hit(Damage(Own, Own), out _, out _) && !GamePvp.Hit(Damage(Foe, Own, layout: 0), out _, out _) && !GamePvp.Hit(Damage(Foe, Own, layout: 8), out _, out _)
            && !GamePvp.Hit([0x03, 0x38, .. Varint(Own), 1, .. Varint(Foe)], out _, out _) && !GamePvp.Hit([0x03, 0x38], out _, out _) && !GamePvp.Hit([0x02, 0x38, .. Varint(Own), 0, .. Varint(Foe)], out _, out _), "a cast, a self-hit and other layouts are no hits");
        // Random bodies behind these opcodes never throw.
        var random = new Random(23);
        for (var i = 0; i < 4000; i++)
        {
            var body = new byte[random.Next(0, 120)]; random.NextBytes(body);
            GamePvp.Player(body, out _, out _, out _); GamePvp.Kill(body, out _, out _, out _, out _);
            GamePvp.Hit([0x03, 0x38, .. body], out _, out _); GamePvp.Hit([0x04, 0x38, .. body], out _, out _);
        }
    }
    static void Decoding()
    {
        var on = false; var now = Start;
        var decoder = new ExperimentalOdyleDecoder { ReadPvp = () => on, Clock = () => now };
        var events = new List<PvpEvent>(); var reads = new List<PvpRead>();
        decoder.PvpObserved += events.Add; decoder.PvpFrameObserved += reads.Add;
        decoder.Feed(Frame(Identity(Own, "Hero")));
        // Off: nothing of these frames is read, not even counted.
        decoder.Feed(Frame(Player(Foe, "Vexa"))); decoder.Feed(Frame(Credit(Own, Foe, "Vexa", FoeServer))); decoder.Feed(Frame(HitNotice(Own, Foe)));
        Check(events.Count == 0 && reads.Count == 0, "without the opt-in no PvP frame is read");
        on = true;
        decoder.Feed(Frame(Credit(Own, Foe, "Vexa", FoeServer)));
        Check(events.Single() == new PvpEvent(PvpKind.Death, "Vexa", FoeServer, "", 0, Own, now) && reads.SequenceEqual([PvpRead.Death]), "a player killing the own character is a death");
        now += TimeSpan.FromSeconds(1); decoder.Feed(Frame(Credit(Own, Foe, "Vexa", FoeServer)));
        Check(events.Count == 1 && reads.Last() == PvpRead.Credit, "the same death said again is counted once");
        // A kill: the victim was named by its record, inside a bundle and behind a transport flag.
        now += TimeSpan.FromMinutes(1);
        decoder.Feed(Frame(Bundle(Frame(Player(Other, "Nyra", FoeServer))))); decoder.Feed(Frame([0xf4, .. Credit(Other, Own, "Hero", OwnServer)]));
        Check(events.Last() == new PvpEvent(PvpKind.Kill, "Nyra", FoeServer, "", 0, Own, now) && reads.TakeLast(2).SequenceEqual([PvpRead.Player, PvpRead.Kill]), "the own character killing a named player is a kill");
        // An assist: the own character hit the player a mate then killed.
        now += TimeSpan.FromMinutes(1);
        decoder.Feed(Frame(Player(Foe, "Vexa"))); decoder.Feed(Frame(Damage(Foe, Own))); now += TimeSpan.FromSeconds(10);
        decoder.Feed(Frame(Credit(Foe, Mate, "Kael", OwnServer)));
        Check(events.Last() == new PvpEvent(PvpKind.Assist, "Vexa", FoeServer, "Kael", OwnServer, Own, now) && reads.TakeLast(3).SequenceEqual([PvpRead.Player, PvpRead.OwnHit, PvpRead.Assist]), "a mate killing a player the own character hit is an assist; the victim keeps the server its kill credit gave");
        // Others' hits, monsters and unreadable frames count nothing.
        var count = events.Count; now += TimeSpan.FromMinutes(1);
        decoder.Feed(Frame(HitNotice(Mate, Foe))); decoder.Feed(Frame(Credit(Mob, Own, "Hero", OwnServer))); decoder.Feed(Frame(Credit(Own, Mob))); decoder.Feed(Frame([0x45, 0x36, 1, 2])); decoder.Feed(Frame([0x04, 0x8d, 0]));
        Check(events.Count == count && reads.TakeLast(4).SequenceEqual([PvpRead.Credit, PvpRead.Credit, PvpRead.PlayerUnread, PvpRead.CreditUnread]), "a monster killed, a death by a monster and unreadable frames are no events");
        // A world load forgets who was in view; turning the opt-in off forgets too.
        decoder.Feed(Frame(Player(Other, "Nyra"))); decoder.Feed(Frame(MapLoad())); decoder.Feed(Frame(Credit(Other, Own, "Hero", OwnServer)));
        Check(events.Count == count, "after a world load the earlier entity ids name nobody");
        decoder.Feed(Frame(Player(Other, "Nyra"))); on = false; decoder.Feed(Frame([0x00, 0x36, 1, 2, 3, 4, 5, 6, 7, 8])); on = true; decoder.Feed(Frame(Credit(Other, Own, "Hero", OwnServer)));
        Check(events.Count == count, "turning PvP stats off forgets the players seen");
        // Another character on the connection starts over.
        decoder.Feed(Frame(Player(Other, "Nyra"))); decoder.Feed(Frame(Identity(Mate, "Alt"))); decoder.Feed(Frame(Credit(Other, Mate, "Alt", OwnServer)));
        Check(events.Count == count, "a switch to another character forgets what the previous one saw");
        decoder.Feed(Frame(Player(Other, "Nyra"))); decoder.Feed(Frame(Credit(Other, Mate, "Alt", OwnServer)));
        Check(events.Last() is { Kind: PvpKind.Kill, Name: "Nyra", Owner: Mate }, "the new character's kills are its own");
        // The trace counts by kind, never by name.
        var trace = new DecoderTrace(); foreach (var read in reads) trace.Pvp(read);
        using var json = JsonDocument.Parse(trace.ToJson("0.33.0", ExperimentalOdyleDecoder.Version));
        var counted = json.RootElement.GetProperty("pvp");
        Check(counted.GetProperty("kill").GetInt64() == 2 && counted.GetProperty("death").GetInt64() == 1 && counted.GetProperty("assist").GetInt64() == 1 && counted.GetProperty("ownHit").GetInt64() == 1 && counted.GetProperty("creditUnread").GetInt64() == 1
            && !trace.ToJson("0.33.0", "a").Contains("Vexa") && !trace.ToJson("0.33.0", "a").Contains("Nyra"), "the trace counts PvP frames by what they were read as, without names");
    }
    static void Tracking()
    {
        PvpTracker Fresh() { var t = new PvpTracker(); t.Own(Own, "Hero", OwnServer); return t; }
        var tracker = Fresh(); var now = Start;
        Check(new PvpTracker().Kill(Own, Foe, "Vexa", FoeServer, now) is null, "before the game names the own character nothing counts");
        // Deaths.
        Check(tracker.Kill(Own, Mob, "", 0, now) is null && tracker.Kill(Own, Own, "Hero", OwnServer, now) is null, "a monster and the character itself are no PvP deaths");
        Check(tracker.Kill(Own, Foe, "Vexa", FoeServer, now) is { Kind: PvpKind.Death, Name: "Vexa", Server: FoeServer, By: "" }, "a death names the killer");
        Check(tracker.Kill(Own, Foe, "Vexa", FoeServer, now + PvpTracker.RepeatWindow) is { Kind: PvpKind.Death }, "a death after the repeat window counts again");
        // Kills: by the own entity id or, for a kill credited by name, the own name and server.
        Check(tracker.Kill(Foe, Own, "Hero", OwnServer, now) is { Kind: PvpKind.Kill, Name: "Vexa", Server: FoeServer }, "the killer of an earlier death is known when killed later");
        tracker = Fresh();
        Check(tracker.Kill(Foe, Own, "Hero", OwnServer, now) is null, "a victim never seen named is not counted");
        tracker.Player(Foe, "Vexa", 0);
        Check(tracker.Kill(Foe, 4242, "Hero", OwnServer, now) is { Kind: PvpKind.Kill, Name: "Vexa", Server: 0 }, "a kill credited to the own name and server is the own character's");
        tracker.Player(Other, "Nyra", 0);
        Check(tracker.Kill(Other, 4242, "Hero", 2305, now) is null, "a player of the same name on another server is someone else");
        // Assists: a hit at most 15 seconds before, a killer of the own side.
        tracker = Fresh(); tracker.Player(Foe, "Vexa", 0);
        Check(tracker.Hit(Own, Foe, now) && !tracker.Hit(Mate, Foe, now) && !tracker.Hit(Own, Own, now), "only the own character's hits on others are remembered");
        Check(tracker.Kill(Foe, Mate, "Kael", OwnServer, now + PvpTracker.AssistWindow) is { Kind: PvpKind.Assist, Name: "Vexa", By: "Kael", ByServer: OwnServer }, "a hit 15 seconds before the kill is an assist");
        Check(tracker.Kill(Foe, Mate, "Kael", OwnServer, now + TimeSpan.FromMinutes(1)) is null, "the hit counts for one kill only");
        now += TimeSpan.FromMinutes(10); tracker.Hit(Own, Foe, now);
        Check(tracker.Kill(Foe, Mate, "Kael", OwnServer, now + PvpTracker.AssistWindow + TimeSpan.FromSeconds(1)) is null, "a hit longer ago is no assist");
        now += TimeSpan.FromMinutes(10); tracker.Hit(Own, Foe, now);
        Check(tracker.Kill(Foe, Mate, "Kael", OwnServer, now) is { Kind: PvpKind.Assist }, "a new hit is a new assist");
        tracker.Player(Other, "Lumina", OwnServer); tracker.Hit(Own, Other, now);
        Check(tracker.Kill(Other, Foe, "Vexa", FoeServer, now) is null, "a touched ally killed by the other side is no assist");
        now += TimeSpan.FromMinutes(10); tracker.Hit(Own, Mob, now); tracker.Hit(Own, Foe, now);
        Check(tracker.Kill(Mob, Mate, "Kael", OwnServer, now) is null && tracker.Kill(Foe, Mob, "", 0, now) is null, "a monster killed or killing is no assist");
        // A record without a server keeps the one known; another name on the id replaces it.
        tracker = Fresh(); tracker.Player(Foe, "Vexa", FoeServer); tracker.Player(Foe, "Vexa", 0);
        Check(tracker.Kill(Foe, Own, "", 0, now) is { Server: FoeServer }, "a later record without a server keeps the known one");
        tracker.Player(Foe, "Nyra", 0);
        Check(tracker.Kill(Foe, Own, "", 0, now + TimeSpan.FromMinutes(1)) is { Name: "Nyra", Server: 0 }, "another name on the entity id replaces the player");
        // The players in view are bounded.
        tracker = Fresh(); for (ulong i = 0; i < PvpTracker.PlayerLimit + 50; i++) tracker.Player(20000 + i, "P" + i, 0);
        Check(tracker.Players == PvpTracker.PlayerLimit && tracker.Kill(20000, Own, "", 0, now) is null && tracker.Kill(20000 + PvpTracker.PlayerLimit + 49, Own, "", 0, now) is { Kind: PvpKind.Kill }, "at most 4,096 players are remembered, the oldest forgotten first");
        tracker.Forget(); Check(tracker.Players == 0 && tracker.Kill(Own, Foe, "Vexa", FoeServer, now) is { Kind: PvpKind.Death }, "forgetting keeps the own character");
        tracker.Reset(); Check(tracker.Kill(Own, Foe, "Vexa", FoeServer, now + TimeSpan.FromMinutes(1)) is null, "a reset forgets the own character too");
    }
    static void Outbox()
    {
        var box = new PvpOutbox(); var now = Start;
        box.Add(new(PvpKind.Kill, "Vexa", FoeServer, "", 0, Own, now)); box.Add(new(PvpKind.Assist, "Nyra", 0, "Kael", OwnServer, Own, now.AddSeconds(5))); box.Add(new(PvpKind.Death, "Vexa", 2999, "", 0, Mate, now));
        var next = box.Next(Own, now.AddSeconds(30));
        Check(next.Length == 2 && next[0] is { Kind: "kill", Name: "Vexa", Server: "Kaisinel", Region: "GLOBAL_EU", By: "", Ago: 30 } && next[0].Id.Length == 32 && next[0].Id != next[1].Id
            && next[1] is { Kind: "assist", Name: "Nyra", Server: "", Region: "", By: "Kael", ByServer: "Lumiel", ByRegion: "GLOBAL_EU", Ago: 25 }, "events carry an id, the server and region names and their age");
        Check(box.Count == 2 && GameIdentity.ServerOf(999) == (null, null) && GameIdentity.ServerOf(2399) is (null, "GLOBAL_EU"), "another character's events are dropped; a server this build does not know has no name");
        Check(box.Next(Own, now.AddSeconds(31)).Select(r => r.Id).SequenceEqual(next.Select(r => r.Id)), "an event not yet stored is sent again under the same id");
        box.Sent(next[..1]); Check(box.Next(Own, now.AddSeconds(40)).Single().Kind == "assist", "a stored event leaves the outbox");
        Check(box.Next(Own, now + PvpOutbox.Keep + TimeSpan.FromSeconds(6)).Length == 0 && box.Count == 0, "an event waits an hour at most");
        for (var i = 0; i < PvpOutbox.Limit + 30; i++) box.Add(new(PvpKind.Kill, "P" + i, 0, "", 0, Own, now));
        Check(box.Count == PvpOutbox.Limit && box.Next(Own, now).Length == PvpOutbox.Batch && box.Next(Own, now)[0].Name == "P30", "at most 200 events wait, the oldest dropped, 20 sent at a time");
        box.Clear(); Check(box.Count == 0, "the outbox clears");
    }
    static async Task Wire()
    {
        var requests = new List<(string Route, string Body)>(); var status = HttpStatusCode.OK; var answer = """{"enabled":true,"stored":1,"serverNow":"x"}""";
        using var api = new PartyApi("https://party.example", "token", null, new Stub(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync()));
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }));
        var stored = await api.PvpAsync("s", "c", [new("0123456789abcdef0123456789abcdef", "assist", "Vexa", "Yustiel", "GLOBAL_EU", "Kael", "Lumiel", "GLOBAL_EU", 4)]);
        using (var sent = JsonDocument.Parse(requests.Single().Body))
        {
            var root = sent.RootElement; var e = root.GetProperty("events")[0];
            Check(requests.Single().Route == "/api/odyle/pvp" && root.GetProperty("sessionId").GetString() == "s" && root.GetProperty("characterId").GetString() == "c" && root.GetProperty("adapter").GetString() == "global-pvp-v1"
                && e.GetProperty("id").GetString() == "0123456789abcdef0123456789abcdef" && e.GetProperty("kind").GetString() == "assist" && e.GetProperty("name").GetString() == "Vexa" && e.GetProperty("server").GetString() == "Yustiel" && e.GetProperty("region").GetString() == "GLOBAL_EU"
                && e.GetProperty("by").GetString() == "Kael" && e.GetProperty("byServer").GetString() == "Lumiel" && e.GetProperty("byRegion").GetString() == "GLOBAL_EU" && e.GetProperty("ago").GetInt32() == 4 && stored is { Enabled: true, Stored: 1 }, "the PvP wire contract");
        }
        answer = """{"enabled":false}""";
        Check(await api.PvpAsync("s", "c", []) is { Enabled: false, Stored: 0 }, "an account with PvP stats off is answered so");
        Check(JsonSerializer.Deserialize<HeartbeatResponse>("""{"serverNow":"x","readerPaused":false,"shareLocation":false,"pvpStats":true}""", new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { PvpStats: true }
            && JsonSerializer.Deserialize<PublisherSession>("""{"sessionId":"s","sequence":0}""", new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { PvpStats: null }, "session and heartbeat answers say whether PvP stats are on; older servers leave it out");
        status = HttpStatusCode.BadRequest; answer = JsonSerializer.Serialize(new { message = PartyApi.PvpAdapterRefused });
        try { await api.PvpAsync("s", "c", []); throw new Exception("PvP check failed: a refused adapter was accepted."); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.PvpAdapterRefused) { }
    }
    sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => answer(request);
    }
    // 45 36: varint entity, u32 mask, u8 mask (bit 0: a name), u8 name length, name, then either u16
    // server and u32 class or, without a server, the class and more.
    static byte[] Player(ulong entity, string name, ushort server = 0, uint job = 24)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[10];
        if (server != 0) { BinaryPrimitives.WriteUInt16LittleEndian(tail, server); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), job); }
        else BinaryPrimitives.WriteUInt32LittleEndian(tail, job);
        return [0x45, 0x36, .. Varint(entity), 0, 0, 0, 0, 1, (byte)text.Length, .. text, .. tail];
    }
    // 04 8D: varint victim, u32, varint killer, then for a player's kill u16 server, u8 name length, name.
    static byte[] Credit(ulong victim, ulong killer, string name = "", ushort server = 0)
    {
        var text = Encoding.UTF8.GetBytes(name); var id = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(id, server);
        return [0x04, 0x8d, .. Varint(victim), 7, 0, 0, 0, .. Varint(killer), .. name.Length > 0 ? [.. id, (byte)text.Length, .. text, 0, 0] : new byte[6]];
    }
    // 03 38: varint actor, 00, varint target, u32 skill and more.
    static byte[] HitNotice(ulong actor, ulong target) => [0x03, 0x38, .. Varint(actor), 0, .. Varint(target), 0x15, 0xcd, 0x5b, 0x07, 1, 9, 9, 9, 9];
    // 04 38: (01 00) varint target, varint switch, varint flag, varint actor, u32 skill and more.
    static byte[] Damage(ulong target, ulong actor, byte layout = 6, bool chained = false) => [0x04, 0x38, .. chained ? new byte[] { 1, 0 } : [], .. Varint(target), layout, 0, .. Varint(actor), 0x15, 0xcd, 0x5b, 0x07, 1, 3, 9, 9];
    static byte[] MapLoad()
    {
        var p = new byte[46]; BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 600010); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(16), 100); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(20), 200); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(24), 30);
        return [0x21, 0x36, .. p];
    }
    static byte[] Identity(ulong id, string name)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(tail, OwnServer); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), 25);
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
    static void Check(bool condition, string message) { if (!condition) throw new Exception("PvP check failed: " + message); }
}
