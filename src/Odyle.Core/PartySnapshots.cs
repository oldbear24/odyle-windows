namespace Odyle.Core;
// One room's snapshot as a refresh fetched it: null when the fetch failed or took too long, with
// Gone set when the server answered 403 or 404 (the account no longer sees the room).
// ReceivedAt is when the answer arrived on this PC, for the server's clock (PartySnapshots.ClockOffset).
public sealed record FetchedSnapshot(string RoomId, RoomSnapshot? Snapshot, DateTimeOffset ReceivedAt, bool Gone = false);
// The party list's room snapshots after a refresh. Each of the account's rooms shows the snapshot
// just fetched or, when its fetch failed or timed out, the one it showed before, so one slow or
// failing room never hides the other rooms or itself. A room no longer listed, or gone on the
// server, is dropped. A refresh of single rooms (realtime news of one room) passes just theirs.
public static class PartySnapshots
{
    public static RoomSnapshot[] Merge(IEnumerable<Room> rooms, IEnumerable<RoomSnapshot> shown, IEnumerable<FetchedSnapshot> fetched)
    {
        var fresh = fetched.GroupBy(f => f.RoomId).ToDictionary(g => g.Key, g => g.Last());
        var before = shown.GroupBy(s => s.Room.Id).ToDictionary(g => g.Key, g => g.Last());
        return rooms.Select(r => fresh.TryGetValue(r.Id, out var f) ? f.Snapshot ?? (f.Gone ? null : before.GetValueOrDefault(r.Id)) : before.GetValueOrDefault(r.Id))
            .OfType<RoomSnapshot>().ToArray();
    }
    // The server's clock against this PC's, from a snapshot just fetched (never one kept from an
    // earlier refresh): its serverNow against the moment it arrived. Null when none was fetched.
    public static TimeSpan? ClockOffset(IEnumerable<FetchedSnapshot> fetched) =>
        fetched.Select(f => f.Snapshot is { } s && Readiness.Parse(s.ServerNow) is { } server ? server - f.ReceivedAt : (TimeSpan?)null).FirstOrDefault(offset => offset is not null);
}
