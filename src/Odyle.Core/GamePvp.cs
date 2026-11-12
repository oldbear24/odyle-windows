using System.Buffers.Binary;

namespace Odyle.Core;
// PvP stats (opt-in): the own character's kills, assists and deaths against other players, for an
// account that turned on PvP stats on the dashboard. Nothing here is read without that opt-in
// (ExperimentalOdyleDecoder.ReadPvp), except for the patch-day trace, which counts and keeps nothing.
//
// Four server-to-client frames are read, by the layouts public decoders document for Global
// (docs/packet-reader.md, PvP stats). They are not yet confirmed with a capture of the owner's client:
// the adapter has its own name, which Server controls can switch off, and the trace counts what it reads.
//   45 36  another player's record: its entity id and name, for naming who was killed
//   04 8D  a kill credit: who died and the player who made the kill
//   03 38  a hit landed: who hit whom; only the own character's hits, for assists
//   04 38  a damage record: who was hit by whom; only its head, and only the own character's
// Never read: damage amounts, HP, skills, positions of others, or anyone's gear.
public enum PvpKind { Kill, Assist, Death }
// What the decoder made of a PvP frame, for the trace: a player's record, an own hit, a kill credit
// that is not the own character's business (Credit), and the three events; Unread is a frame of
// these opcodes without the documented layout.
public enum PvpRead { Player, PlayerUnread, OwnHit, Credit, CreditUnread, Kill, Assist, Death }
/// <summary>One PvP event of the own character.</summary>
/// <param name="Name">The other player: who was killed (Kill, Assist) or who made the kill (Death).</param>
/// <param name="Server">That player's server id, 0 when the game did not say.</param>
/// <param name="By">Assist only: who made the kill.</param>
/// <param name="Owner">The own character's entity id when it happened.</param>
public sealed record PvpEvent(PvpKind Kind, string Name, ushort Server, string By, ushort ByServer, ulong Owner, DateTimeOffset At);

public static class GamePvp
{
    public static bool IsOpcode(byte first, byte second) => (first, second) is (0x45, 0x36) or (0x04, 0x8d) or (0x03, 0x38) or (0x04, 0x38);
    private static bool Entity(ulong id) => id is > 0 and <= uint.MaxValue;
    // 45 36, another player's record: varint entity, u32 mask, u8 mask (bit 0: a name follows), u8
    // UTF-8 byte count and the name; then, on some records, u16 server and u32 class. Server is 0
    // when the record does not carry one.
    public static bool Player(ReadOnlySpan<byte> payload, out ulong entity, out string name, out ushort server)
    {
        name = ""; server = 0; var o = 0;
        if (!ExperimentalOdyleDecoder.TryVarint(payload, ref o, out entity) || !Entity(entity) || payload.Length - o < 7) return false;
        o += 4;
        if ((payload[o++] & 1) == 0) return false;
        int count = payload[o++];
        if (count is < 1 or > 36 || payload.Length - o < count || !GameIdentity.TryName(payload.Slice(o, count), out name)) return false;
        o += count;
        if (payload.Length - o >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(payload[o..]) is >= 1000 and < 3000 and var id && BinaryPrimitives.ReadUInt32LittleEndian(payload[(o + 2)..]) is >= 5 and <= 48) server = id;
        return true;
    }
    // 04 8D, a kill credit: varint victim, u32, varint killer, then for a player's kill u16 server,
    // u8 UTF-8 byte count and the killer's name. All zero after the victim is a summon despawning.
    // killerName is "" when the killer is no player (a monster, a fall).
    public static bool Kill(ReadOnlySpan<byte> payload, out ulong victim, out ulong killer, out string killerName, out ushort killerServer)
    {
        killer = 0; killerName = ""; killerServer = 0; var o = 0;
        if (!ExperimentalOdyleDecoder.TryVarint(payload, ref o, out victim) || !Entity(victim) || payload.Length - o < 5) return false;
        var kind = BinaryPrimitives.ReadUInt32LittleEndian(payload[o..]); o += 4;
        if (!ExperimentalOdyleDecoder.TryVarint(payload, ref o, out killer) || kind == 0 && killer == 0) return false;
        if (!Entity(killer) || payload.Length - o < 4) return true;
        var server = BinaryPrimitives.ReadUInt16LittleEndian(payload[o..]); int count = payload[o + 2];
        if (server is < 1000 or >= 3000 || count is < 1 or > 36 || payload.Length - o - 3 < count || !GameIdentity.TryName(payload.Slice(o + 3, count), out var name)) return true;
        killerName = name; killerServer = server;
        return true;
    }
    // Who hit whom, from a whole frame. 03 38, a hit landed: varint actor, 00, varint target. 04 38, a
    // damage record (after an optional 01 00): varint target, varint switch (low four bits 4 to 7; 0
    // announces a cast), varint flag, varint actor. Only the first record of a frame, and nothing
    // after the two ids: no skill, no damage.
    public static bool Hit(ReadOnlySpan<byte> frame, out ulong actor, out ulong target)
    {
        actor = target = 0;
        if (frame.Length < 5) return false;
        var p = frame[2..]; var o = 0;
        if (frame[0] == 0x03 && frame[1] == 0x38)
            return ExperimentalOdyleDecoder.TryVarint(p, ref o, out actor) && Entity(actor) && o < p.Length && p[o++] == 0
                && ExperimentalOdyleDecoder.TryVarint(p, ref o, out target) && Entity(target) && target != actor;
        if (frame[0] != 0x04 || frame[1] != 0x38) return false;
        if (p.Length >= 2 && p[0] == 1 && p[1] == 0) o = 2;
        return ExperimentalOdyleDecoder.TryVarint(p, ref o, out target) && Entity(target)
            && ExperimentalOdyleDecoder.TryVarint(p, ref o, out var layout) && (layout & 0x0f) is >= 4 and <= 7
            && ExperimentalOdyleDecoder.TryVarint(p, ref o, out _)
            && ExperimentalOdyleDecoder.TryVarint(p, ref o, out actor) && Entity(actor) && actor != target;
    }
}

// Turns what the frames say into the own character's kills, assists and deaths. It remembers, in
// memory only and only while PvP stats are on, the names of the players the game showed this PC
// (to name who was killed) and whom the own character hit in the last seconds (for assists).
//   Death:  a kill credit for the own character whose killer is a player.
//   Kill:   a kill credit naming the own character as the killer of a player this PC saw named.
//   Assist: another player of the own side killed a player the own character hit at most 15
//           seconds before.
// A victim this PC never saw named is not counted: it cannot be told from a monster.
public sealed class PvpTracker
{
    // The backend's PvP adapter registry lists this name (backend/odyle/protocols.go).
    public const string Adapter = "global-pvp-v1";
    public static readonly TimeSpan AssistWindow = TimeSpan.FromSeconds(15), RepeatWindow = TimeSpan.FromSeconds(5);
    public const int PlayerLimit = 4096, HitLimit = 256;
    private readonly Dictionary<ulong, (string Name, ushort Server)> players = [];
    private readonly Queue<ulong> seen = new();
    private readonly Dictionary<ulong, DateTimeOffset> hits = [], counted = [];
    private ulong own; private string ownName = ""; private ushort ownServer;
    public int Players => players.Count;
    // The game named the own character (33 36). Another entity is another character: what the
    // previous one saw and hit is forgotten.
    public void Own(ulong entity, string name, ushort server)
    {
        if (entity != own) Forget();
        own = entity;
        if (name.Length > 0) { ownName = name; ownServer = server; }
    }
    // A world load: the game hands out entity ids anew and names the players in view again.
    public void Loaded() { players.Clear(); seen.Clear(); hits.Clear(); counted.Clear(); }
    // PvP stats were turned off: no other player's name stays in memory. The own character stays known.
    public void Forget() => Loaded();
    public void Reset() { Loaded(); own = 0; ownName = ""; ownServer = 0; }
    public void Player(ulong entity, string name, ushort server)
    {
        if (entity == own || name.Length == 0) return;
        if (players.TryGetValue(entity, out var known))
        {
            // A record without a server keeps the server an earlier one of the same player gave.
            players[entity] = (name, server != 0 || known.Name != name ? server : known.Server);
            return;
        }
        while (players.Count >= PlayerLimit && seen.TryDequeue(out var oldest)) players.Remove(oldest);
        players[entity] = (name, server); seen.Enqueue(entity);
    }
    // True for a hit of the own character on someone else, which is remembered for an assist.
    public bool Hit(ulong actor, ulong target, DateTimeOffset now)
    {
        if (own == 0 || actor != own || target == own) return false;
        if (hits.Count >= HitLimit && !hits.ContainsKey(target)) Prune(hits, now, AssistWindow);
        if (hits.Count >= HitLimit && !hits.ContainsKey(target)) return true;
        hits[target] = now;
        return true;
    }
    public PvpEvent? Kill(ulong victim, ulong killer, string killerName, ushort killerServer, DateTimeOffset now)
    {
        if (own == 0 || victim == 0) return null;
        var byPlayer = killerName.Length > 0;
        var mine = killer == own || byPlayer && ownName.Length > 0 && killerName == ownName && killerServer == ownServer;
        // A kill credit names its killer: later that player may be the one killed.
        if (byPlayer && !mine) Player(killer, killerName, killerServer);
        // The game may say one death twice (the reference decoders count both frames as one).
        if (counted.TryGetValue(victim, out var last) && now - last < RepeatWindow) return null;
        if (counted.Count >= HitLimit) Prune(counted, now, RepeatWindow);
        if (victim == own)
        {
            // A monster, a fall or the character itself is no PvP death.
            if (!byPlayer || mine) return null;
            counted[victim] = now;
            return new(PvpKind.Death, killerName, killerServer, "", 0, own, now);
        }
        var hit = hits.Remove(victim, out var at) && now - at <= AssistWindow;
        if (!players.TryGetValue(victim, out var player)) return null;
        counted[victim] = now;
        if (mine) return new(PvpKind.Kill, player.Name, player.Server, "", 0, own, now);
        // Someone of the other side killing a player the own character touched is an ally dying, not an assist.
        if (!byPlayer || !hit || ownServer != 0 && killerServer / 1000 != ownServer / 1000) return null;
        return new(PvpKind.Assist, player.Name, player.Server, killerName, killerServer, own, now);
    }
    private static void Prune(Dictionary<ulong, DateTimeOffset> times, DateTimeOffset now, TimeSpan keep)
    {
        foreach (var key in times.Where(t => now - t.Value > keep).Select(t => t.Key).ToArray()) times.Remove(key);
    }
}

// One event as the server takes it (POST /api/odyle/pvp): an id of the app's making, so an event
// sent twice is stored once; the other player with the server and region names characters are saved
// with ("" when this build does not know the server); Ago is how many seconds ago it happened.
public sealed record PvpReport(string Id, string Kind, string Name, string Server, string Region, string By, string ByServer, string ByRegion, int Ago);
// The events not yet stored by the server: at most 200, each for an hour, sent 20 at a time for the
// character the game named. Events of another character than the named one are dropped: they were
// read before a switch and cannot be saved under the new character.
public sealed class PvpOutbox
{
    public const int Limit = 200, Batch = 20;
    public static readonly TimeSpan Keep = TimeSpan.FromHours(1);
    private readonly List<(string Id, PvpEvent Event)> waiting = [];
    public int Count => waiting.Count;
    public void Add(PvpEvent e)
    {
        if (waiting.Count >= Limit) waiting.RemoveAt(0);
        waiting.Add((Guid.NewGuid().ToString("N"), e));
    }
    public PvpReport[] Next(ulong owner, DateTimeOffset now)
    {
        waiting.RemoveAll(w => now - w.Event.At > Keep || w.Event.Owner != owner);
        return [.. waiting.Take(Batch).Select(w => Report(w.Id, w.Event, now))];
    }
    public void Sent(IReadOnlyCollection<PvpReport> sent) => waiting.RemoveAll(w => sent.Any(s => s.Id == w.Id));
    public void Clear() => waiting.Clear();
    private static PvpReport Report(string id, PvpEvent e, DateTimeOffset now)
    {
        var (server, region) = GameIdentity.ServerOf(e.Server); var (byServer, byRegion) = GameIdentity.ServerOf(e.ByServer);
        // A server without a known region is left out whole: the name alone could be another region's.
        if (server is null || region is null) server = region = null;
        if (byServer is null || byRegion is null) byServer = byRegion = null;
        return new(id, e.Kind.ToString().ToLowerInvariant(), e.Name, server ?? "", region ?? "", e.By, byServer ?? "", byRegion ?? "", (int)Math.Clamp((now - e.At).TotalSeconds, 0, Keep.TotalSeconds));
    }
}
