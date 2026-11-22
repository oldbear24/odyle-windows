using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Odyle.Core;

namespace Odyle.Reader;
// Fetching the party (W#90: moved out of MainWindow, which shows it): the account's rooms and each room's snapshot.
// Read-only, so it runs beside reader operations without their lock; rooms are reloaded too, so sharing changed on
// the dashboard shows up. A refresh asked for while one runs makes it run once more; the caller waits for that, so
// it always sees the party as the server has it now. Realtime news of one room fetches only that room's snapshot, in
// the same queue: one fetch at a time, each asked for after the one before was applied, so an answer the server
// gave earlier can never replace one it gave later.
internal sealed class PartyRefreshQueue
{
    private Task? running;
    private bool wholeAgain;
    // Rooms whose realtime news asked for their own snapshot only.
    private readonly HashSet<string> roomsAgain = [];
    // The rooms answer a caller fetched a moment ago (ConnectAsync): the next whole refresh uses it instead of asking again.
    private RoomsResponse? known;
    private readonly Func<PartyApi?> api;
    private readonly Func<string, bool> listed;
    private readonly Func<RoomsResponse, FetchedSnapshot[], Task> wholeFetched;
    private readonly Action<FetchedSnapshot[]> roomsFetched;
    // api: the request client of the connection (a refresh for one that is gone meanwhile applies nothing). listed:
    // whether the party lists a room already. wholeFetched: the rooms and their snapshots, to apply; roomsFetched:
    // some rooms' snapshots.
    internal PartyRefreshQueue(Func<PartyApi?> api, Func<string, bool> listed, Func<RoomsResponse, FetchedSnapshot[], Task> wholeFetched, Action<FetchedSnapshot[]> roomsFetched)
    {
        this.api = api; this.listed = listed; this.wholeFetched = wholeFetched; this.roomsFetched = roomsFetched;
    }
    internal Task WholeAsync(RoomsResponse? known = null) { wholeAgain = true; if (known is not null) this.known = known; return RunAsync(); }
    internal Task RoomAsync(string roomId) { roomsAgain.Add(roomId); return RunAsync(); }
    // A new connection asks for its own rooms; a disconnected account wants no room's snapshot.
    internal void NewConnection() => known = null;
    internal void Clear() => roomsAgain.Clear();
    private async Task RunAsync()
    {
        // A refresh that ended without doing what was asked (the account changed meanwhile) leaves it for the next.
        while (running is { IsCompleted: false } before) await before;
        if (wholeAgain || roomsAgain.Count > 0) await (running = RefreshNowAsync());
    }
    private async Task RefreshNowAsync()
    {
        if (api() is not { } current) return;
        while (api() == current && (wholeAgain || roomsAgain.Count > 0))
        {
            try
            {
                if (wholeAgain) await WholeNowAsync(current);
                else await SomeNowAsync(current);
            }
            // Connection problems are reported by the heartbeat; keep the last list.
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { }
        }
    }
    private async Task WholeNowAsync(PartyApi current)
    {
        // This covers the rooms asked for alone too; news arriving meanwhile asks again.
        wholeAgain = false; roomsAgain.Clear();
        var rooms = known ?? await current.GetRoomsAsync(); known = null;
        var fetched = await Task.WhenAll(rooms.Rooms.Select(r => FetchSnapshotAsync(current, r.Id)));
        if (api() != current) return;
        await wholeFetched(rooms, fetched);
    }
    private async Task SomeNowAsync(PartyApi current)
    {
        var wanted = roomsAgain.ToArray(); roomsAgain.Clear();
        // News of a room the list does not have yet (just joined) fetches the whole party.
        if (!wanted.All(listed)) { wholeAgain = true; return; }
        var fetched = await Task.WhenAll(wanted.Select(id => FetchSnapshotAsync(current, id)));
        if (api() != current) return;
        // A room the server no longer shows the account changes its list of rooms.
        if (fetched.Any(f => f.Gone)) wholeAgain = true;
        roomsFetched(fetched);
    }
    // A room that fails or takes longer than the 12-second timeout keeps the snapshot it showed
    // (PartySnapshots.Merge) instead of failing the whole party's refresh.
    private static async Task<FetchedSnapshot> FetchSnapshotAsync(PartyApi current, string roomId)
    {
        try { var snapshot = await current.GetSnapshotAsync(roomId); return new(roomId, snapshot, DateTimeOffset.UtcNow); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            var gone = error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound };
            Log.Warn($"Room {roomId} could not be fetched{(gone ? "; the account no longer sees it" : "; it keeps what it showed")}: {error.Message}");
            return new(roomId, null, DateTimeOffset.UtcNow, gone);
        }
    }
}
