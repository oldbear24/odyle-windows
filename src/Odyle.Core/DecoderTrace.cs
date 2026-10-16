using System.Text.Json;

namespace Odyle.Core;
// One opcode in the patch-day trace: how many leaf frames carried it and their payload sizes.
public sealed record OpcodeCount(string Opcode, long Frames, int MinBytes, int MaxBytes, long Bytes);
// The patch-day trace (Odyle.exe --decoder-trace): plain numbers about what the decoder saw, for
// re-deriving it after a game patch. Opcodes with their frame counts and sizes, stat snapshots
// and stat updates with their record values, frame layouts, and identities by server and class
// only. Never payload bytes, character names or addresses; it stays on this PC.
public sealed class DecoderTrace
{
    // Each list keeps its first entries; the counts keep counting.
    public const int Kept = 500;
    private readonly object gate = new();
    private readonly Dictionary<ushort, OpcodeCount> opcodes = [];
    private readonly List<object> snapshots = [], updates = [], layouts = [], identities = [];
    private readonly HashSet<OdyleFrameLayout> layoutsSeen = [];
    private long snapshotCount, updateCount;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    // The game build from Steam's app manifest (never from the game's process or files).
    public string? GameVersion { get; set; }

    public void Frame(ushort opcode, int bytes)
    {
        lock (gate)
            opcodes[opcode] = opcodes.TryGetValue(opcode, out var c)
                ? c with { Frames = c.Frames + 1, MinBytes = Math.Min(c.MinBytes, bytes), MaxBytes = Math.Max(c.MaxBytes, bytes), Bytes = c.Bytes + bytes }
                : new($"{opcode:X4}", 1, bytes, bytes, bytes);
    }
    public void Snapshot(OdyleSnapshotObservation s)
    {
        lock (gate)
        {
            snapshotCount++;
            if (snapshots.Count < Kept) snapshots.Add(new { bytes = s.PayloadBytes, records = s.Records, read = s.Read, odyle = s.Fields.Select(f => new { tag = $"{f.Tag:X2}", f.Base, f.Bonus, f.Offset }) });
        }
    }
    public void Update(OdyleUpdateObservation u)
    {
        lock (gate)
        {
            updateCount++;
            if (updates.Count < Kept) updates.Add(new { resource = u.Resource, tag = $"{u.Tag:X2}", direction = u.Direction, @base = u.Base, bonus = u.Bonus, trailerBytes = u.TrailerBytes });
        }
    }
    public void Layout(OdyleFrameLayout l)
    {
        lock (gate) if (layouts.Count < Kept && layoutsSeen.Add(l)) layouts.Add(new { opcode = $"{l.Opcode:X4}", header = $"{l.Header0:X2} {l.Header1:X2} {l.Header2:X2}", bytes = l.PayloadBytes });
    }
    // The character's name is left out: server and class are enough to rebuild an identity fixture.
    public void Identity(GameIdentity identity)
    {
        lock (gate) if (identities.Count < Kept) identities.Add(new { server = identity.ServerId, region = identity.Region, @class = identity.Class });
    }
    public OpcodeCount[] Opcodes() { lock (gate) return [.. opcodes.Values.OrderByDescending(c => c.Frames).ThenBy(c => c.Opcode)]; }
    // One log line: the most frequent opcodes with their frame counts.
    public string Summary(int top = 12)
    {
        var list = Opcodes();
        return list.Length == 0 ? "no frames yet" : string.Join(", ", list.Take(top).Select(c => $"{c.Opcode}×{c.Frames} ({c.MinBytes}–{c.MaxBytes} B)")) + (list.Length > top ? $", {list.Length - top} more opcodes" : "");
    }
    public string ToJson(string appVersion, string adapter)
    {
        lock (gate)
            return JsonSerializer.Serialize(new
            {
                app = appVersion, adapter, game = GameVersion, startedAt = StartedAt, writtenAt = DateTimeOffset.UtcNow,
                opcodes = opcodes.Values.OrderByDescending(c => c.Frames).ThenBy(c => c.Opcode),
                snapshots = new { total = snapshotCount, kept = snapshots }, updates = new { total = updateCount, kept = updates }, layouts, identities,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}
