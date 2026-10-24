using System.Buffers.Binary;

namespace Odyle.Core;
// Where the own character is, for the rooms of an account that shares its location (an opt-in on
// the dashboard). Map is null for an arrival or an own effect, which carry no map; heading (degrees)
// comes with a world load only. Coordinates are the game's world units. An own effect is a flight
// (effect 231, the tick while gliding or flying) or any other effect on the character (effect).
public sealed record GameLocation(int? Map, float X, float Y, float Z, float? Heading, string Source)
{
    public const string Load = "load", Arrival = "arrival", Flight = "flight", Effect = "effect";
    private const uint FlightTick = 231;
    private const float Limit = 1_000_000f;
    // A whole frame, opcode first; null for anything without the layout seen on Global. Other
    // entities' effects (2A 38 naming another entity) are never read.
    public static GameLocation? Read(ReadOnlySpan<byte> frame, ulong? own)
    {
        if (frame.Length < 2) return null;
        var payload = frame[2..];
        return (frame[0], frame[1]) switch { (0x21, 0x36) => MapLoad(payload), (0x23, 0x36) => Arrived(payload), (0x2a, 0x38) when own is { } id => OwnEffect(payload, id), _ => null };
    }
    // 21 36, a world load (login, teleport, channel change): u32 load count, u32 map id, u64 server
    // time (ms), f32 x, y, z and heading, then more.
    private static GameLocation? MapLoad(ReadOnlySpan<byte> p)
    {
        if (p.Length is < 32 or > 128) return null;
        var map = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        if (map is < 1 or > 9_999_999 || !Position(p[16..], out var x, out var y, out var z)) return null;
        var heading = BinaryPrimitives.ReadSingleLittleEndian(p[28..]);
        return new((int)map, x, y, z, float.IsFinite(heading) && MathF.Abs(heading) <= 360 ? heading : null, Load);
    }
    // 23 36, the end of the loading screen: six zero bytes, f32 x, y, z, then six more bytes.
    private static GameLocation? Arrived(ReadOnlySpan<byte> p)
    {
        if (p.Length is < 18 or > 40 || p[..6].ContainsAnyExcept((byte)0) || !Position(p[6..], out var x, out var y, out var z)) return null;
        return new(null, x, y, z, null, Arrival);
    }
    // 2A 38, an effect on an entity: varint entity, 01 11, u8 counter, u32 effect, u32 duration (ms),
    // u32, u64 time (ms), the varint entity again, 01 00, f32 x, y, z, nothing after. Effect 231 ticks
    // about once a second while the character glides or flies.
    private static GameLocation? OwnEffect(ReadOnlySpan<byte> p, ulong own)
    {
        var o = 0;
        if (!ExperimentalOdyleDecoder.TryVarint(p, ref o, out var actor) || actor != own || o + 23 > p.Length || p[o] != 1 || p[o + 1] != 0x11) return null;
        var effect = BinaryPrimitives.ReadUInt32LittleEndian(p[(o + 3)..]); o += 23;
        if (!ExperimentalOdyleDecoder.TryVarint(p, ref o, out var again) || again != own || p.Length != o + 14 || p[o] != 1 || p[o + 1] != 0) return null;
        return Position(p[(o + 2)..], out var x, out var y, out var z) ? new(null, x, y, z, null, effect == FlightTick ? Flight : Effect) : null;
    }
    private static bool Position(ReadOnlySpan<byte> p, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (p.Length < 12) return false;
        x = BinaryPrimitives.ReadSingleLittleEndian(p); y = BinaryPrimitives.ReadSingleLittleEndian(p[4..]); z = BinaryPrimitives.ReadSingleLittleEndian(p[8..]);
        return Inside(x) && Inside(y) && Inside(z) && (x, y, z) != (0, 0, 0);
    }
    private static bool Inside(float v) => float.IsFinite(v) && MathF.Abs(v) <= Limit;
}
// A position to send (POST /api/odyle/location), for the saved character the game named.
public sealed record LocationReport(string CharacterId, int Map, float X, float Y, float Z, float? Heading, string Source);
// Like a reading, a position belongs to the character the game names. A world load waits for the
// name the game sends after the loading screen (6 to 10 seconds), so a switch never gives the next
// character's position to the one before; one still unnamed after 30 seconds is dropped. Between
// loads the named character's own effects (flights and other effects alike) follow it on its map, at
// most every five seconds and only after it moved 500 units. Nothing arrives while it walks: the map shows the last known position.
public sealed class LocationReporter
{
    // The backend's location adapter registry lists this name (backend/odyle/protocols.go).
    public const string Adapter = "global-location-v1";
    public static readonly TimeSpan NameWait = TimeSpan.FromSeconds(30), FlightEvery = TimeSpan.FromSeconds(5);
    public const float FlightDistance = 500, ArrivalDistance = 1;
    private GameLocation? pending; private DateTimeOffset pendingAt;
    private string? owner; private int? map;
    private LocationReport? last; private DateTimeOffset lastAt;
    // The named character's newest position, sent or not: what goes out when sharing is switched on.
    public LocationReport? Current { get; private set; }
    // A position the decoder read; the report to send now, if any.
    public LocationReport? Observe(GameLocation location, DateTimeOffset now)
    {
        if (location.Source == GameLocation.Load) { pending = location; pendingAt = now; return null; }
        var waiting = pending is not null && now - pendingAt <= NameWait;
        if (location.Source == GameLocation.Arrival)
        {
            // Usually just before the name: it refines the load still waiting for it.
            if (waiting) { if (Distance(pending!, location) > ArrivalDistance) pending = pending! with { X = location.X, Y = location.Y, Z = location.Z, Source = GameLocation.Arrival }; return null; }
            if (owner is null || map is null || Current is { } known && Distance(known, location) <= ArrivalDistance) return null;
            return Report(new(owner, map.Value, location.X, location.Y, location.Z, null, GameLocation.Arrival), now);
        }
        if (waiting || owner is null || map is null) return null;
        var moved = new LocationReport(owner, map.Value, location.X, location.Y, location.Z, null, location.Source);
        if (last is { } sent && (now - lastAt < FlightEvery || Distance(sent, moved) < FlightDistance)) { Current = moved; return null; }
        return Report(moved, now);
    }
    // The game named its character, saved as characterId; a world load waiting for that name is its position.
    public LocationReport? Named(string characterId, DateTimeOffset now)
    {
        if (owner != characterId) { owner = characterId; map = null; Current = last = null; }
        if (pending is not { } load) return null;
        pending = null;
        // A load that waited too long came without a name: where the character is now is unknown.
        if (now - pendingAt > NameWait) { map = null; return null; }
        map = load.Map;
        return Report(new(characterId, load.Map!.Value, load.X, load.Y, load.Z, load.Heading, load.Source), now);
    }
    // The game switched characters: nothing goes to either until the game names the new one. A load
    // already waiting belongs to the new character.
    public void Switched() { owner = null; map = null; Current = last = null; }
    public void Reset() { pending = null; Switched(); }
    private LocationReport Report(LocationReport report, DateTimeOffset now) { Current = last = report; lastAt = now; return report; }
    private static float Distance(GameLocation a, GameLocation b) => Distance(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Distance(LocationReport a, GameLocation b) => Distance(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Distance(LocationReport a, LocationReport b) => Distance(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Distance(float dx, float dy, float dz) => MathF.Sqrt(dx * dx + dy * dy + dz * dz);
}
