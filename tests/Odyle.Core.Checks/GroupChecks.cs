using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4;
using Odyle.Core;

// Parties and forces: the own party's roster (00 92) and end (1F 92), the own force's roster (02 96),
// a player leaving it (22 96) and its end (2E 96), read only for the own entity, what the tracker
// keeps of them, and the wire contract of POST /api/odyle/game-group. Synthetic frames in the
// layouts seen on Global, never captures. Then the bands the lists show (GameBands), the same
// cases as the dashboard's tests/party.test.cjs.
static class GroupChecks
{
    const ulong Own = 606, Mate = 5069, OwnCharacter = 0x0900_0000_0003_6c9a, MateCharacter = 0x090e_0000_0003_b300;
    static readonly byte[] PartyKey = [0xba, 0x0b, 0x06, 0x00, 0xb2, 0xdf, 0xf7, 0x23], ForceKey = [0x59, 0x1a, 0x00, 0x00, 0xb2, 0xdf, 0xf7, 0x23];
    const string Party = "ba0b0600b2dff723", Force = "591a0000b2dff723";
    public static async Task Run()
    {
        Frames();
        Tracking();
        await Wire();
        Bands();
        Console.WriteLine("Party and force frame, tracking, wire and band checks passed.");
    }
    static void Bands()
    {
        // Rows as the lists hand them over: the player and the group (null unless live). A shape
        // lists the rows in order, a band's as player:party/force.
        (string, GameGroup?) P(string player, string? party = null, string? force = null) => (player, party is null && force is null ? null : new GameGroup(party, force));
        (Band Own, ((string, GameGroup?) Item, Band Band)[] Rows) Arrange(GameGroup? own, params (string, GameGroup?)[] rows) => GameBands.Arrange(rows, r => r, "me", own);
        string Shape((Band Own, ((string, GameGroup?) Item, Band Band)[] Rows) arranged) => string.Join(" ", arranged.Rows.Select(r => r.Item.Item1 + (r.Band.Any ? $":{r.Band.Party}/{r.Band.Force}" : "")));
        // Two players of one party, a third of the same force in another party, one outside both.
        (string, GameGroup?)[] room = [P("Anna", "p1", "f1"), P("Bram"), P("Cleo", "p1", "f1"), P("Dax", "p2", "f1")];
        Check(Shape(Arrange(null, room)) == "Anna:p1/f1 Cleo:p1/f1 Dax:/f1 Bram", "a party stands together inside its force, the force's other players after it");
        Check(Shape(Arrange(null, room[1], room[3], room[0], room[2])) == "Bram Dax:/f1 Anna:p1/f1 Cleo:p1/f1", "a band takes the place of its first row");
        Check(Shape(Arrange(null, room[..3])) == "Anna:p1/ Cleo:p1/ Bram" && Shape(Arrange(null, room[0], room[1])) == "Anna Bram", "a force whose players all share one party is that party; one player alone makes no band");
        Check(Shape(Arrange(null, P("Anna", "p1"), P("Cleo", "p1", "f1"), P("Dax", force: "f1"))) == "Anna:p1/f1 Cleo:p1/f1 Dax:/f1", "a party without a force yet takes the force one of its players reported");
        Check(Shape(Arrange(null, P("Anna", "p1"), P("Cleo"), P("Cleo", "p1"))) == "Anna:p1/ Cleo:p1/ Cleo" && Shape(Arrange(null, P("Cleo", "p1"), P("Cleo", "p2"), P("Anna", "p2"))) == "Cleo Cleo Anna",
            "a player's first row with a group speaks for them");
        // You count as listed first: your band leads, and says it is yours.
        var yours = Arrange(new("p1", "f1"), P("Bram"), P("Zed", "p9"), P("Anna", "p1", "f1"), P("Dax", "p2", "f1"));
        Check(Shape(yours) == "Anna:p1/f1 Dax:/f1 Bram Zed" && yours.Own == new Band("p1", "f1", true, true) && yours.Rows[0].Band is { YourParty: true, YourForce: true } && yours.Rows[1].Band is { YourParty: false, YourForce: true },
            "your party and force come first and are marked yours");
        var others = Arrange(new("p1"), P("Bram"), P("Tor", "p3"), P("Vex", "p3"));
        Check(Shape(others) == "Bram Tor:p3/ Vex:p3/" && others.Own == Band.None && !others.Rows[1].Band.YourParty, "others' party is a band that is not yours");
        Check(Shape(Arrange(null, P("me", "p1"), P("Anna", "p1"))) == "me Anna" && Arrange(null).Rows.Length == 0, "your own rows never speak for you");
        // The lists frame them: a force around its parties and its other players, a party around its players (* yours).
        string Frames(Nested<(string, GameGroup?)>[] nested) => string.Join(" ", nested.Select(n => n.IsBand ? $"{n.Kind}{(n.Yours ? "*" : "")}[{Frames(n.Children!)}]" : n.Item.Item1));
        var framed = GameBands.Nest(Arrange(null, room).Rows);
        Check(Frames(framed) == "force[party[Anna Cleo] Dax] Bram" && framed[0].Rows == 3 && framed[1].Rows == 1, "a party's frame sits inside its force's, with the force's other players");
        Check(Frames(GameBands.Nest(yours.Rows)) == "force*[party*[Anna] Dax] Bram Zed" && Frames(GameBands.Nest(others.Rows)) == "Bram party[Tor Vex]" && GameBands.Nest(Arrange(null).Rows).Length == 0,
            "your frames lead and say so; others' parties are framed too");
        // The character's gameGroup as room snapshots and realtime news carry it.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Check(JsonSerializer.Deserialize<Character>("""{"id":"c","user":"u","characterName":"A","server":"S","region":"GLOBAL_EU","value":1,"confirmed":true,"valid":true,"lastReadingAt":"","gameGroup":{"party":"ab","at":"2026-10-28 12:00:00.000Z"}}""", json)!.GameGroup == new GameGroup("ab", null, "2026-10-28 12:00:00.000Z")
            && JsonSerializer.Deserialize<Character>("""{"id":"c","user":"u","characterName":"A","server":"S","region":"GLOBAL_EU","value":1,"confirmed":true,"valid":true,"lastReadingAt":"","gameGroup":null}""", json)!.GameGroup is null, "a character's gameGroup is read");
        // Compact mode across your rooms: only live characters count, your own from any of your characters.
        var moment = DateTimeOffset.Parse("2026-10-28T12:00:00Z"); string Ago(int seconds) => moment.AddSeconds(-seconds).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Character Seen(string id, string user, int value, int seconds, GameGroup? group = null) => new(id, user, id, "Lumiel", "GLOBAL_EU", value, true, true, Ago(600), Ago(seconds), "packet", GameGroup: group);
        RoomMember Member(string user, params Character[] characters) => new(user + "-m", user, "reporter", user, characters);
        RoomSnapshot Room(string id, params RoomMember[] members) => new(new Room(id, id, 40, "reporter", true), members, Ago(0));
        var rooms = new[] { Room("r1", Member("u", Seen("Hero", "u", 300, 5, new("p1", "f1")) with { Shared = false }), Member("l", Seen("Lumina", "l", 180, 5, new("p1", "f1"))), Member("z", Seen("Zed", "z", 900, 5))),
            Room("r2", Member("n", Seen("Nyx", "n", 90, 10, new("p2", "f1"))), Member("k", Seen("Kael", "k", 500, 60, new("p1", "f1")))) };
        var (mates, _, own) = CompactStrip.Mates(rooms, "u", moment, 40);
        Check(mates.Select(m => (m.Name, m.Band.Party, m.Band.Force)).SequenceEqual([("Lumina", "p1", "f1"), ("Nyx", "", "f1"), ("Zed", "", ""), ("Kael", "", "")]) && own == new Band("p1", "f1", true, true),
            "compact mode puts your party and force first across your rooms; a stale character's group says nothing");
        Check(CompactStrip.Mates(rooms, "u", moment.AddHours(3), 40) is { Own.Any: false } && GameBands.Own(rooms, "u", moment.AddHours(3)) is null, "without a live character you are in no band");
    }
    static void Frames()
    {
        var decoder = new ExperimentalOdyleDecoder(); var seen = new List<GameGroupEvent>(); var order = new List<string>();
        decoder.GroupObserved += e => { seen.Add(e); order.Add("group"); }; decoder.IdentityObserved += _ => order.Add("identity");
        // A roster before the own identity waits for it, and comes before the name.
        decoder.Feed(Frame(PartyRoster(0, (Mate, MateCharacter, "Mate"), (Own, OwnCharacter, "Hero"))));
        Check(seen.Count == 0, "a roster before the own identity waits");
        decoder.Feed(Frame(Identity(Own, "Hero")));
        Check(seen.Single() == new GameGroupEvent(GroupChange.Party, Party, OwnCharacter, Own) && order.SequenceEqual(["group", "identity"]), "the held roster is read once the own id is known, before the name");
        // A roster of the updates' layout (flag 08 and a block before the members), and one inside a bundle.
        decoder.Feed(Frame(PartyRoster(8, (Own, OwnCharacter, "Hero"))));
        Check(seen.Last() == new GameGroupEvent(GroupChange.Party, Party, OwnCharacter, Own), "an updated roster is read");
        decoder.Feed(Frame(Bundle(Frame(ForceRoster((Mate, MateCharacter, "Mate"), (Own, OwnCharacter, "Hero"))))));
        Check(seen.Last() == new GameGroupEvent(GroupChange.Force, Force, OwnCharacter, Own), "a force roster inside a bundle is read");
        decoder.Feed(Frame([0x22, 0x96, .. U64(MateCharacter), .. PartyKey, 4]));
        Check(seen.Last() == new GameGroupEvent(GroupChange.ForceLeft, Party, MateCharacter), "a player leaving the force");
        decoder.Feed(Frame([0x2e, 0x96, .. ForceKey, 3]));
        Check(seen.Last() == new GameGroupEvent(GroupChange.ForceEnded, Force), "the force ended");
        decoder.Feed(Frame([0x1f, 0x92, 3]));
        Check(seen.Last() == new GameGroupEvent(GroupChange.PartyEnded), "the party ended");
        // Rosters without the own entity, or without its entry's layout, and odd lengths are ignored.
        var count = seen.Count;
        foreach (var odd in new byte[][]
        {
            PartyRoster(0, (Mate, MateCharacter, "Mate")), ForceRoster((Mate, MateCharacter, "Mate")),
            [.. PartyRoster(0, (Own, OwnCharacter, "Hero"))[..3], .. new byte[8], .. PartyRoster(0, (Own, OwnCharacter, "Hero"))[11..]],
            PartyRoster(0, (Own, OwnCharacter, "Hero"), guid: "not-a-guid-at-all-but-thirty-six-lon"),
            PartyRoster(0, (Own, OwnCharacter, "Hero"))[..60],
            [0x1f, 0x92], [0x1f, 0x92, 3, 0], [0x22, 0x96, .. U64(OwnCharacter), .. PartyKey], [0x2e, 0x96, .. ForceKey], [0x2e, 0x96, .. ForceKey, 3, 0],
            [0x07, 0x92, 0x34, 0x19, 0x00, 0x09, 0x04, .. "Mate"u8], [0x1c, 0x92, .. U64(MateCharacter), .. new byte[40]],
        })
        {
            decoder.Feed(Frame(odd));
        }
        Check(seen.Count == count, "rosters without the own entity, other party messages and odd lengths are ignored");
        // Random bodies behind these opcodes never throw.
        var random = new Random(11);
        for (var i = 0; i < 3000; i++)
        {
            var body = new byte[random.Next(0, 200)]; random.NextBytes(body);
            byte[] opcode = (i % 5) switch { 0 => [0x00, 0x92], 1 => [0x02, 0x96], 2 => [0x1f, 0x92], 3 => [0x22, 0x96], _ => [0x2e, 0x96] };
            GameGroups.Read([.. opcode, .. body], Own);
        }
        // Another connection starts unknown: its first roster waits for its own identity again.
        var next = new ExperimentalOdyleDecoder(); var later = new List<GameGroupEvent>(); next.GroupObserved += later.Add;
        next.Feed(Frame(PartyRoster(0, (Own, OwnCharacter, "Hero")))); next.Feed(Frame(Identity(Mate, "Mate")));
        Check(later.Count == 0, "a held roster without the entity the game then names is dropped");
    }
    static void Tracking()
    {
        var tracker = new GroupTracker();
        Check(tracker.Observe(new(GroupChange.Party, Party, OwnCharacter, Own)) && tracker is { Party: Party, Force: "", Owner: Own }, "a party roster sets the party");
        Check(!tracker.Observe(new(GroupChange.Party, Party, OwnCharacter, Own)), "the same party again is no change");
        Check(tracker.Observe(new(GroupChange.Force, Force, OwnCharacter, Own)) && tracker is { Party: Party, Force: Force }, "a force roster sets the force");
        Check(!tracker.Observe(new(GroupChange.ForceLeft, Party, MateCharacter)) && tracker.Force == Force, "another player leaving changes nothing");
        Check(!tracker.Observe(new(GroupChange.ForceEnded, "0000000000000001")) && tracker.Force == Force, "another force ending changes nothing");
        Check(tracker.Observe(new(GroupChange.ForceLeft, Party, OwnCharacter)) && tracker is { Party: Party, Force: "" }, "leaving the force clears it");
        tracker.Observe(new(GroupChange.Force, Force, OwnCharacter, Own));
        Check(tracker.Observe(new(GroupChange.ForceEnded, Force)) && tracker is { Party: Party, Force: "" }, "the force ending clears it");
        tracker.Observe(new(GroupChange.Force, Force, OwnCharacter, Own));
        Check(tracker.Observe(new(GroupChange.PartyEnded)) && tracker is { Party: "", Force: "" }, "leaving the party leaves the force too");
        Check(!tracker.Observe(new(GroupChange.PartyEnded)), "ending no party is no change");
        // The rosters belong to the entity they listed.
        tracker.Observe(new(GroupChange.Party, Party, OwnCharacter, Own));
        tracker.Named(Own); Check(tracker.Party == Party, "naming the same entity keeps its party");
        tracker.Named(Mate); Check(tracker is { Party: "", Force: "", Owner: 0 }, "naming another entity forgets the party");
        tracker.Observe(new(GroupChange.Party, Party, OwnCharacter, Own)); tracker.Observe(new(GroupChange.Force, Force, OwnCharacter, Own));
        Check(tracker.Observe(new(GroupChange.Party, "1122334455667788", MateCharacter, Mate)) && tracker is { Party: "1122334455667788", Force: "", Owner: Mate }, "another entity's roster starts over");
        tracker.Reset(); Check(tracker is { Party: "", Force: "", Owner: 0 }, "a reset forgets everything");
    }
    static async Task Wire()
    {
        var requests = new List<(string Route, string Body)>(); var status = HttpStatusCode.OK; var message = "";
        using var api = new PartyApi("https://party.example", "token", null, new Stub(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync()));
            var body = status == HttpStatusCode.OK ? """{"serverNow":"x"}""" : JsonSerializer.Serialize(new { message });
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        await api.GameGroupAsync("s", "c", Party, Force);
        using (var sent = JsonDocument.Parse(requests.Single().Body))
        {
            var root = sent.RootElement;
            Check(requests.Single().Route == "/api/odyle/game-group" && root.GetProperty("sessionId").GetString() == "s" && root.GetProperty("characterId").GetString() == "c"
                && root.GetProperty("adapter").GetString() == "global-group-v1" && root.GetProperty("party").GetString() == Party && root.GetProperty("force").GetString() == Force, "the party and force wire contract");
        }
        await api.GameGroupAsync("s", "c", "", "");
        using (var sent = JsonDocument.Parse(requests.Last().Body)) Check(sent.RootElement.GetProperty("party").ValueKind == JsonValueKind.Null && sent.RootElement.GetProperty("force").ValueKind == JsonValueKind.Null, "outside a party and force both are null");
        status = HttpStatusCode.BadRequest; message = PartyApi.GroupAdapterRefused;
        try { await api.GameGroupAsync("s", "c", Party, ""); throw new Exception("Group check failed: a refused adapter was accepted."); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.GroupAdapterRefused) { }
    }
    sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => answer(request);
    }
    // 00 92: u8 flags, the party's id, the leader's character id, u32 size, 4 bytes, with flag 08 a
    // 121-byte block, u8 count, the members, then a tail.
    static byte[] PartyRoster(byte flag, (ulong Entity, ulong Character, string Name) first, (ulong Entity, ulong Character, string Name)? second = null, string? guid = null)
    {
        (ulong Entity, ulong Character, string Name)[] members = second is { } other ? [first, other] : [first];
        byte[] block = (flag & 8) != 0 ? [5, .. new byte[120]] : [];
        return [0x00, 0x92, flag, .. PartyKey, .. U64(members[0].Character), 5, 0, 0, 0, 2, 1, 0, 2, .. block, (byte)members.Length,
            .. members.SelectMany((m, i) => Member((byte)(i + 1), m.Entity, m.Character, m.Name, guid)), 1, 4];
    }
    // 02 96: u8 flags, the force's id, the leader's character id, a byte, u32 size, 4 bytes, the
    // party's id, its number, u32 size, a byte, u8 count, the members.
    static byte[] ForceRoster(params (ulong Entity, ulong Character, string Name)[] members) =>
        [0x02, 0x96, 0, .. ForceKey, .. U64(members[0].Character), 0, 20, 0, 0, 0, 2, 1, 0, 1, .. PartyKey, 1, 5, 0, 0, 0, 3, (byte)members.Length,
            .. members.SelectMany((m, i) => Member((byte)(i + 1), m.Entity, m.Character, m.Name, null))];
    // u8 mask, u8 slot, u32 entity, 4 bytes, the account GUID (u8 36 + 36 characters), u64 character id, the name, then the rest.
    static byte[] Member(byte slot, ulong entity, ulong character, string name, string? guid)
    {
        var id = Encoding.ASCII.GetBytes(guid ?? $"{entity:X8}-0000-4000-8000-{character:X12}"[..36]); var text = Encoding.UTF8.GetBytes(name);
        var entityBytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(entityBytes, (uint)entity);
        return [3, slot, .. entityBytes, 0, 9, 0, 9, 36, .. id, .. U64(character), (byte)text.Length, .. text, 24, 0, 0, 0, .. new byte[24], 7, 6, .. "Legion"u8, 2, 0, 0, 0];
    }
    static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
    static byte[] Identity(ulong id, string name)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(tail, 2304); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), 25);
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
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Group check failed: " + message); }
}
