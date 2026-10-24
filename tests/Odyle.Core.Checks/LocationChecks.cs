using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4;
using Odyle.Core;

// Location sharing: the own character's map and position from world loads (21 36), arrivals (23 36)
// and its own effects (2A 38), attributed like readings to the character the game names, and the
// wire contract of POST /api/odyle/location. Synthetic frames in the layouts seen on Global, never captures.
static class LocationChecks
{
    public static async Task Run()
    {
        Frames();
        Reporting();
        await Wire();
        Console.WriteLine("Location frame, attribution, throttling and wire checks passed.");
    }
    static void Frames()
    {
        var decoder = new ExperimentalOdyleDecoder(); var seen = new List<GameLocation>(); decoder.LocationObserved += seen.Add;
        decoder.Feed(Frame(MapLoad(1110, -83475.7f, -33883.7f, 19698f, 113.4f)));
        Check(seen.Single() is { Map: 1110, Source: GameLocation.Load } l && Near(l.X, -83475.7f) && Near(l.Y, -33883.7f) && Near(l.Z, 19698f) && l.Heading is { } h && Near(h, 113.4f), "a world load names its map, position and heading");
        decoder.Feed(Frame([0xf3, .. MapLoad(600001, 100, 200, 300, -137.5f)]));
        Check(seen.Last() is { Map: 600001, Heading: -137.5f }, "a flagged world load is read");
        decoder.Feed(Frame(Bundle(Frame(Arrival(-83475.7f, -33883.7f, 19698f)))));
        Check(seen.Last() is { Map: null, Heading: null, Source: GameLocation.Arrival } a && Near(a.X, -83475.7f), "an arrival inside a bundle is read");
        // Effects carry a position only for the own entity, known from its identity message.
        decoder.Feed(Frame(Effect(42, 1000, 2000, 3000))); Check(seen.Count == 3, "an effect before the own identity is not read");
        decoder.Feed(Frame(Identity(42, "TestHero")));
        decoder.Feed(Frame(Effect(42, 1000, 2000, 3000)));
        Check(seen.Last() is { Source: GameLocation.Flight, Map: null } f && Near(f.X, 1000) && Near(f.Z, 3000), "an own effect gives the position");
        decoder.Feed(Frame([0xf1, .. Effect(42, 1500, 2000, 3000)])); Check(seen.Count == 5 && Near(seen.Last().X, 1500), "a flagged own effect is read");
        // Effect 231 is the tick while gliding or flying; any other effect on the character is an effect.
        decoder.Feed(Frame(Effect(42, 1600, 2000, 3000, effect: 200))); decoder.Feed(Frame(Effect(42, 1700, 2000, 3000, effect: 10002)));
        Check(seen.Count == 7 && seen[^2] is { Source: GameLocation.Effect, Map: null } && seen[^1].Source == GameLocation.Effect && Near(seen[^1].X, 1700), "other own effects are effects, not flights");
        decoder.Feed(Frame(Effect(43, 1000, 2000, 3000))); decoder.Feed(Frame(Effect(42, 1000, 2000, 3000, second: 43)));
        decoder.Feed(Frame(Effect(43, 1000, 2000, 3000, effect: 200)));
        Check(seen.Count == 7, "another entity's effect is never read");
        var count = seen.Count;
        foreach (var odd in new byte[][]
        {
            MapLoad(0, 1, 2, 3, 0), MapLoad(10_000_000, 1, 2, 3, 0), MapLoad(1110, float.NaN, 2, 3, 0), MapLoad(1110, 1, float.PositiveInfinity, 3, 0), MapLoad(1110, 1, 2, 2_000_000, 0), MapLoad(1110, 0, 0, 0, 0),
            MapLoad(1110, 1, 2, 3, 0)[..33], [.. MapLoad(1110, 1, 2, 3, 0), .. new byte[100]], [0x21, 0x36],
            [.. Arrival(1, 2, 3)[..2], 1, .. Arrival(1, 2, 3)[3..]], Arrival(1, 2, 3)[..19], [0x23, 0x36, 0, 0, 0],
            [.. Effect(42, 1, 2, 3), 0], Effect(42, 1, 2, 3)[..^1], [0x2a, 0x38, 42, 1, 0x12, .. Effect(42, 1, 2, 3)[5..]], [0x2a, 0x38],
        })
        {
            decoder.Feed(Frame(odd));
        }
        Check(seen.Count == count, "frames without the expected layout or with impossible values are ignored");
        // A heading out of range drops only the heading.
        decoder.Feed(Frame(MapLoad(1110, 1, 2, 3, float.NaN))); Check(seen.Last() is { Map: 1110, Heading: null }, "an unreadable heading is left out");
        // Random bodies behind these opcodes never throw.
        var random = new Random(7);
        for (var i = 0; i < 3000; i++)
        {
            var body = new byte[random.Next(0, 80)]; random.NextBytes(body);
            byte[] opcode = (i % 3) switch { 0 => [0x21, 0x36], 1 => [0x23, 0x36], _ => [0x2a, 0x38, 42] };
            decoder.Feed(Frame([.. opcode, .. body]));
        }
        var trace = new DecoderTrace(); trace.Location(seen[0]); trace.Location(seen[0]); trace.Location(seen.Last(l => l.Source == GameLocation.Flight)); trace.Location(seen.Last(l => l.Source == GameLocation.Effect));
        using var json = JsonDocument.Parse(trace.ToJson("0.28.0", ExperimentalOdyleDecoder.Version));
        var counts = json.RootElement.GetProperty("locations");
        Check(counts.GetProperty("load").GetInt64() == 2 && counts.GetProperty("flight").GetInt64() == 1 && counts.GetProperty("effect").GetInt64() == 1 && !trace.ToJson("0.28.0", "x").Contains("83475"), "the trace counts positions without keeping them");
    }
    static void Reporting()
    {
        var at = new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);
        GameLocation Load(int map, float x, float heading = 10) => new(map, x, 0, 100, heading, GameLocation.Load);
        GameLocation Arrive(float x) => new(null, x, 0, 100, null, GameLocation.Arrival);
        GameLocation Fly(float x) => new(null, x, 0, 100, null, GameLocation.Flight);
        GameLocation Affect(float x) => new(null, x, 0, 100, null, GameLocation.Effect);
        var r = new LocationReporter();
        // Nothing is known before the game names a character.
        Check(r.Observe(Fly(10), at) is null && r.Named("a", at) is null && r.Observe(Fly(10), at) is null, "no map without a world load");
        // A load waits for the name; the arrival before it refines it.
        Check(r.Observe(Load(1110, 1000), at) is null && r.Observe(Arrive(1020), at.AddSeconds(6)) is null, "a load waits for the name");
        Check(r.Named("a", at.AddSeconds(7)) is { CharacterId: "a", Map: 1110, X: 1020, Heading: 10, Source: GameLocation.Arrival }, "the named character gets the load, refined by its arrival");
        Check(r.Named("a", at.AddSeconds(60)) is null, "a repeated name sends nothing more");
        // Own effects follow the named character on its map, at most every five seconds and after 500 units.
        Check(r.Observe(Fly(1400), at.AddSeconds(20)) is null && r.Current is { X: 1400 }, "less than 500 units is not sent, but is the current position");
        Check(r.Observe(Fly(2000), at.AddSeconds(21)) is { Source: GameLocation.Flight, Map: 1110, X: 2000 }, "a flight after 500 units is sent");
        Check(r.Observe(Fly(3000), at.AddSeconds(23)) is null && r.Observe(Fly(3100), at.AddSeconds(26.5)) is { X: 3100 }, "flights at most every five seconds");
        // Other own effects follow the same five seconds and 500 units, together with flights.
        Check(r.Observe(Affect(4000), at.AddSeconds(28)) is null && r.Current is { X: 4000, Source: GameLocation.Effect }, "an effect within five seconds of a flight waits");
        Check(r.Observe(Affect(3300), at.AddSeconds(32)) is null, "an effect that moved less than 500 units waits");
        Check(r.Observe(Affect(3700), at.AddSeconds(33)) is { Source: GameLocation.Effect, Map: 1110, X: 3700 } && r.Observe(Fly(4300), at.AddSeconds(35)) is null, "effects and flights share the throttle");
        // An arrival after the name is sent when it moved the character.
        Check(r.Observe(Arrive(4300), at.AddSeconds(36)) is null && r.Observe(Arrive(4400), at.AddSeconds(37)) is { Source: GameLocation.Arrival, X: 4400 }, "an arrival after the name only when it moved");
        // A switch: the load before the new character's name goes to the new character, never the previous one.
        r.Observe(Load(1220, 50), at.AddSeconds(40)); r.Switched();
        Check(r.Observe(Fly(5000), at.AddSeconds(41)) is null, "nothing goes out between a switch and the new name");
        Check(r.Named("b", at.AddSeconds(46)) is { CharacterId: "b", Map: 1220, X: 50 }, "the switch's load belongs to the newly named character");
        // A load waiting for a name also stops flights of the character named before it.
        r.Observe(Load(1330, 70), at.AddSeconds(100));
        Check(r.Observe(Fly(9000), at.AddSeconds(105)) is null, "no flight while a load waits for its name");
        // A load never named within 30 seconds is dropped, and so is the map.
        Check(r.Named("b", at.AddSeconds(131)) is null && r.Observe(Fly(20000), at.AddSeconds(140)) is null, "a load unnamed for 30 seconds is dropped");
        // A name for another character without a load: its map is unknown until its next load.
        r.Observe(Load(1110, 0), at.AddSeconds(200)); r.Named("b", at.AddSeconds(205));
        Check(r.Named("c", at.AddSeconds(300)) is null && r.Current is null && r.Observe(Fly(9000), at.AddSeconds(310)) is null, "another named character starts without a map");
        r.Observe(Load(1110, 0), at.AddSeconds(400)); r.Reset();
        Check(r.Named("c", at.AddSeconds(405)) is null, "a reset forgets the waiting load");
    }
    static async Task Wire()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Check(JsonSerializer.Deserialize<HeartbeatResponse>("""{"serverNow":"x","readerPaused":false,"shareLocation":true}""", options)!.ShareLocation == true
            && JsonSerializer.Deserialize<HeartbeatResponse>("""{"serverNow":"x","readerPaused":false}""", options)!.ShareLocation is null
            && JsonSerializer.Deserialize<PublisherSession>("""{"sessionId":"s","sequence":0,"readerPaused":false,"shareLocation":false}""", options)!.ShareLocation == false, "heartbeat and session answers carry shareLocation, absent on older backends");
        var requests = new List<(string Route, string Body)>(); var shared = true; var status = HttpStatusCode.OK; var message = "";
        using var api = new PartyApi("https://party.example", "token", null, new Stub(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync()));
            var body = status == HttpStatusCode.OK ? JsonSerializer.Serialize(new { shared }) : JsonSerializer.Serialize(new { message });
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        Check(await api.LocationAsync("s", new LocationReport("c", 1110, -83475.7f, -33883.7f, 19698f, 113.4f, GameLocation.Load)), "a stored position answers shared");
        using (var sent = JsonDocument.Parse(requests.Single().Body))
        {
            var root = sent.RootElement;
            Check(requests.Single().Route == "/api/odyle/location" && root.GetProperty("sessionId").GetString() == "s" && root.GetProperty("characterId").GetString() == "c" && root.GetProperty("adapter").GetString() == "global-location-v1"
                && root.GetProperty("map").GetInt32() == 1110 && root.GetProperty("x").GetDouble() == -83475.7 && root.GetProperty("y").GetDouble() == -33883.7 && root.GetProperty("z").GetDouble() == 19698 && root.GetProperty("heading").GetDouble() == 113.4 && root.GetProperty("source").GetString() == "load", "the position's wire contract");
        }
        await api.LocationAsync("s", new LocationReport("c", 1110, 1, 2, 3, null, GameLocation.Flight));
        using (var sent = JsonDocument.Parse(requests.Last().Body)) Check(sent.RootElement.GetProperty("heading").ValueKind == JsonValueKind.Null && sent.RootElement.GetProperty("source").GetString() == "flight", "a position without heading sends null");
        await api.LocationAsync("s", new LocationReport("c", 1110, 1, 2, 3, null, GameLocation.Effect));
        using (var sent = JsonDocument.Parse(requests.Last().Body)) Check(sent.RootElement.GetProperty("source").GetString() == "effect", "an own effect is sent as effect");
        shared = false; Check(!await api.LocationAsync("s", new LocationReport("c", 1110, 1, 2, 3, null, GameLocation.Flight)), "an account that does not share answers shared false");
        status = HttpStatusCode.BadRequest; message = PartyApi.LocationAdapterRefused;
        try { await api.LocationAsync("s", new LocationReport("c", 1110, 1, 2, 3, null, GameLocation.Flight)); throw new Exception("Location check failed: a refused adapter was accepted."); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.LocationAdapterRefused) { }
    }
    sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => answer(request);
    }
    // 21 36: u32 load count, u32 map, u64 server ms, f32 x, y, z, heading, then 14 bytes.
    static byte[] MapLoad(uint map, float x, float y, float z, float heading)
    {
        var p = new byte[46]; BinaryPrimitives.WriteUInt32LittleEndian(p, 3); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), map); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8), 2_611_155);
        Floats(p.AsSpan(16), x, y, z, heading); p[32] = 2; return [0x21, 0x36, .. p];
    }
    // 23 36: six zero bytes, f32 x, y, z, u32, u16.
    static byte[] Arrival(float x, float y, float z) { var p = new byte[24]; Floats(p.AsSpan(6), x, y, z); p[18] = 3; return [0x23, 0x36, .. p]; }
    // 2A 38: varint entity, 01 11, counter, u32 effect, u32 duration, u32, u64 time, varint entity, 01 00, f32 x, y, z.
    static byte[] Effect(ulong entity, float x, float y, float z, ulong? second = null, uint effect = 231)
    {
        var middle = new byte[21]; middle[0] = 0x36; BinaryPrimitives.WriteUInt32LittleEndian(middle.AsSpan(1), effect); BinaryPrimitives.WriteUInt32LittleEndian(middle.AsSpan(5), 200);
        BinaryPrimitives.WriteUInt64LittleEndian(middle.AsSpan(13), 1_792_859_851_517);
        var position = new byte[12]; Floats(position, x, y, z);
        return [0x2a, 0x38, .. Varint(entity), 1, 0x11, .. middle, .. Varint(second ?? entity), 1, 0, .. position];
    }
    static void Floats(Span<byte> target, params float[] values) { for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], values[i]); }
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
    static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Location check failed: " + message); }
}
