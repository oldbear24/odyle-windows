using System.Text.Json;
using Odyle.Core;

// Realtime news from the backend: the messages of its topics as the app reads them, applied
// to room snapshots and the account's characters without fetching again, the topics the
// app listens to and the reconnect schedule. RunLive checks a real connection to a backend.
static class RealtimeChecks
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    const string Lumina = """{"id":"c","user":"l","characterName":"Lumina","server":"Lumiel","region":"GLOBAL_EU","value":180,"confirmed":true,"valid":true,"source":"packet","lastReadingAt":"2026-10-12 11:00:00.000Z","lastSeenAt":"2026-10-12 11:59:50.000Z"}""";
    static RoomSnapshot Snapshot() => JsonSerializer.Deserialize<RoomSnapshot>("""{"room":{"id":"r","name":"Evening","cost":40,"role":"reporter","sharing":true},"serverNow":"2026-10-12T12:00:00.000Z","members":[{"id":"m","user":"l","role":"reporter","displayName":"Lumina","characters":[""" + Lumina + """]}]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    public static void Run()
    {
        var refresh = RealtimeNews.Parse(Json("""{"refresh":true}"""));
        // Wishlist invites (odyle-dashboard#45): who invites to which run, worded for the tray.
        var invites = RealtimeNews.Parse(Json("""{"invites":[{"runId":"r1","roomId":"room","room":"Raid night","dungeon":"Fire Temple","startsAt":"2026-10-17 20:00:00.000Z","by":"Lumina"},{"runId":"","dungeon":"x"}]}""")).Invites;
        Check(invites is [{ RunId: "r1", By: "Lumina", Now: false }] && invites[0].Notice().Title == "Lumina invites you to Fire Temple" && invites[0].Notice().Text.StartsWith("Raid night · ") && refresh.Invites.Length == 0, "invite news");
        // A call to a run now (odyle-dashboard 0.41.0) says so, with the start time.
        var call = RealtimeNews.Parse(Json("""{"invites":[{"runId":"r2","roomId":"room","room":"Raid night","dungeon":"Fire Temple","startsAt":"2026-10-17 20:00:00.000Z","by":"Lumina","now":true}]}""")).Invites;
        Check(call is [{ RunId: "r2", Now: true }] && call[0].Notice().Title == "Lumina calls the party to Fire Temple" && call[0].Notice().Text.StartsWith("Raid night · starts "), "call news");
        // Party ready (odyle-dashboard#48): the runs whose attendees can all claim a cube, worded for the tray.
        var party = RealtimeNews.Parse(Json("""{"partyReady":[{"runId":"r1","roomId":"room","room":"Raid night","dungeon":"Fire Temple","startsAt":"2026-10-17 20:00:00.000Z","of":5},{"runId":"","dungeon":"x","of":1}]}""")).PartyReady;
        Check(party is [{ RunId: "r1", Of: 5 }] && party[0].Notice() == ("Fire Temple: the party is ready", "All 5 going can claim a cube · Raid night") && refresh.PartyReady.Length == 0, "party-ready news");
        Check(NotificationSettings.Default.ReadyInTray && !new NotificationSettings(Kinds: new(Ready: false)).ReadyInTray && !new NotificationSettings(Channels: new(Tray: false)).ReadyInTray, "party ready shows in the tray until turned off");
        // Held-back Windows versions (#19) come as their own news; other news leaves them unknown.
        Check(RealtimeNews.Parse(Json("""{"windowsBlocked":["0.18.0","bad"]}""")).HeldBack is [{ Major: 0, Minor: 18, Build: 0 }] && RealtimeNews.Parse(Json("""{"windowsBlocked":[]}""")).HeldBack is [] && refresh.HeldBack is null, "held-back versions news");
        Check(refresh is { Refresh: true, Removed: false, Characters.Length: 0, Seen.Count: 0 }, "a room refresh");
        // Update all apps now (the dashboard's admin pages): the latest version the server knows, if any.
        var update = RealtimeNews.Parse(Json("""{"windowsUpdate":{"version":"0.30.0","at":"2026-10-28T18:00:00Z"}}""")).UpdateNow;
        Check(update is { Version: { Major: 0, Minor: 30, Build: 0 } } && update.Needed(new Version(0, 29, 0, 0)) && !update.Needed(new Version(0, 30, 0, 0)) && !update.Needed(new Version(0, 31, 0)), "an update request names the latest version; apps at it or newer stay");
        Check(RealtimeNews.Parse(Json("""{"windowsUpdate":{"at":"2026-10-28T18:00:00Z"}}""")).UpdateNow is { Version: null } unknown && unknown.Needed(new Version(0, 30, 0)) && RealtimeNews.Parse(Json("""{"windowsUpdate":{"version":"nightly"}}""")).UpdateNow is { Version: null }
            && refresh.UpdateNow is null && RealtimeNews.Parse(Json("""{"windowsUpdate":true}""")).UpdateNow is null, "without a known version every app asks GitHub; no request, no update");
        var changed = RealtimeNews.Parse(Json("""{"refresh":true,"characters":[""" + Lumina.Replace("\"value\":180", "\"value\":220").Replace("}", ",\"shared\":true}") + """]}"""));
        Check(changed.Characters.Single() is { Id: "c", Value: 220, Parts: null }, "changed characters with their snapshot fields");
        var split = RealtimeNews.Parse(Json("""{"characters":[""" + Lumina.Replace("}", ",\"parts\":{\"base\":90,\"bonus\":990}}") + """]}"""));
        Check(split.Characters.Single().Parts == new OdyleParts(90, 990), "a character carries the game's two numbers when its last reading sent them");
        var seen = RealtimeNews.Parse(Json("""{"seen":{"c":"2026-10-12T12:00:10.000Z","x":5}}"""));
        Check(seen is { Refresh: false } && seen.Seen.Count == 1 && seen.Seen["c"] == "2026-10-12T12:00:10.000Z", "heartbeats, ignoring what is not a time");
        Check(RealtimeNews.Parse(Json("[]")).Refresh, "an unreadable message fetches again");
        var saved = RealtimeNews.Parse(Json("""{"notifications":{"channels":{"tray":true,"browser":false,"push":false},"leadMinutes":[30],"kinds":{"runs":false,"cap":true},"capTicks":2,"quiet":{"from":"23:00","to":"06:00","timeZone":"Europe/Prague"},"saved":true}}"""));
        var capped = RealtimeNews.Parse(Json("""{"capAlerts":[{"characterId":"c","characterName":"Lumina","state":"soon","base":545,"cap":560,"settled":false,"tick":15,"at":"2026-10-15T19:00:00.000Z"},{"characterId":"x","characterName":"","state":"soon"},{"characterId":"y","characterName":"Odd","state":"later"}]}"""));
        Check(capped is { Refresh: false, CapAlerts: [{ CharacterName: "Lumina", Cap: 560, Settled: false }] } && capped.CapAlerts[0].Notice() is ("Lumina caps soon", var body) && body.StartsWith("Its natural pool reaches 560 ≈ ") && body.EndsWith(", full if that is its cap."), "cap alerts of the account's characters");
        Check(new CapAlert("c", "Lumina", "full", 840, 840, true, 15).Notice() == ("Lumina is losing Odyle", "Its natural pool is full: each tick of 15 is lost until you spend some."), "a full pool loses each tick");
        Check(saved is { Refresh: false, Notifications: { RunsInTray: false, Saved: true } } && saved.Notifications.Leads.SequenceEqual([30]) && saved.Notifications.Quiet!.From == "23:00" && saved.Notifications is { CapInTray: true, CapTicks: 2 }, "notification settings saved on the dashboard");
        // News for everyone on the account's topic: a new release and a changed announcement.
        var release = RealtimeNews.Parse(Json("""{"release":{"version":"0.6.0.16","publishedAt":"2026-10-16T15:19:32Z","url":"https://github.com/oldbear24/odyle-windows/releases/tag/v0.6.0.16"}}"""));
        Check(release is { Refresh: false, AnnouncementChanged: false } && release.Release == new Version(0, 6, 0, 16), "a new release names its version and fetches nothing");
        Check(RealtimeNews.Parse(Json("""{"release":{"version":"latest"}}""")).Release is null && RealtimeNews.Parse(Json("""{"refresh":true}""")).Release is null, "a release without a version is no release");
        var warning = RealtimeNews.Parse(Json("""{"announcement":{"text":"Maintenance at 08:00 UTC","level":"warning"}}"""));
        Check(warning is { AnnouncementChanged: true, Refresh: false, Announcement: { IsWarning: true, Text: "Maintenance at 08:00 UTC" } }, "a changed announcement with its level");
        Check(RealtimeNews.Parse(Json("""{"announcement":null}""")) is { AnnouncementChanged: true, Announcement: null } && RealtimeNews.Parse(Json("""{"seen":{}}""")) is { AnnouncementChanged: false }, "a cleared announcement, and news without one");

        var snapshots = new[] { Snapshot(), Snapshot() with { Room = Snapshot().Room with { Id = "other" } } };
        var applied = PartyNews.WithCharacters(snapshots, "r", changed.Characters, out var complete);
        Check(complete && applied[0].Members[0].Characters[0].Value == 220 && applied[1].Members[0].Characters[0].Value == 180, "a changed character replaces its card in its room only");
        PartyNews.WithCharacters(snapshots, "r", [changed.Characters[0] with { Id = "new" }], out complete);
        Check(!complete, "a character the room does not list yet asks for a fetch");
        var later = PartyNews.WithSeen(snapshots, "r", seen.Seen);
        var earlier = PartyNews.WithSeen(snapshots, "r", new Dictionary<string, string> { ["c"] = "2026-10-12T11:00:00.000Z" });
        Check(later[0].Members[0].Characters[0].LastSeenAt == "2026-10-12T12:00:10.000Z" && earlier[0].Members[0].Characters[0].LastSeenAt == "2026-10-12 11:59:50.000Z", "a heartbeat moves lastSeenAt forward, never back");
        var now = Readiness.Parse("2026-10-12T12:00:40.000Z")!.Value;
        Check(Readiness.Of(snapshots[0].Members[0].Characters[0], now) == Presence.Stale && Readiness.Of(later[0].Members[0].Characters[0], now) == Presence.Live, "a heartbeat keeps a mate live without fetching the room");
        var own = PartyNews.WithCharacters(snapshots[0].Members[0].Characters, changed.Characters, out complete);
        Check(complete && own.Single().Value == 220, "the account's own characters follow their readings");

        var rooms = new[] { snapshots[0].Room, snapshots[1].Room };
        Check(RealtimeTopics.For("u", rooms).SequenceEqual(new[] { "odyle.user.u", "odyle.room.r", "odyle.presence.r", "odyle.room.other", "odyle.presence.other" }), "the account's topic and each room's two");
        // After a refresh (PartySnapshots): a room whose fetch failed or timed out keeps what it showed,
        // a room no longer listed or gone on the server is dropped, and the rooms keep the list's order.
        var at = Readiness.Parse("2026-10-12T12:00:30.000Z")!.Value;
        RoomSnapshot In(string id, string serverNow = "2026-10-12T12:00:00.000Z") => Snapshot() with { Room = Snapshot().Room with { Id = id }, ServerNow = serverNow };
        Room Listed(string id) => Snapshot().Room with { Id = id };
        var shown = new[] { In("r"), In("other"), In("left") };
        var fresh = In("r", "2026-10-12T12:05:00.000Z") with { Members = [] };
        var merged = PartySnapshots.Merge([Listed("other"), Listed("r"), Listed("new")], shown, [new("r", fresh, at), new("other", null, at), new("new", null, at), new("left", In("left"), at)]);
        Check(merged.Length == 2 && merged[0] == shown[1] && merged[1] == fresh, "one failing room keeps its snapshot and the others take theirs; a room no longer listed is dropped");
        Check(PartySnapshots.Merge([Listed("r"), Listed("other")], shown, [new("other", null, at, Gone: true)]) is [var only] && only == shown[0], "a room the server no longer shows is dropped; rooms not fetched keep theirs");
        Check(PartySnapshots.Merge([Listed("r")], [], [new("r", null, at)]).Length == 0, "a room never fetched shows nothing until it is");
        // The server's clock comes from a snapshot just fetched, against the moment it arrived; never from one kept.
        Check(PartySnapshots.ClockOffset([new("other", null, at), new("r", fresh, at)]) == TimeSpan.FromSeconds(270) && PartySnapshots.ClockOffset([new("other", null, at)]) is null, "the server's clock from a fresh snapshot only");
        Check(new[] { 0, 1, 2, 3, 4, 9 }.Select(RealtimeClient.Backoff).SequenceEqual(new[] { 1, 2, 5, 10, 30, 30 }.Select(s => TimeSpan.FromSeconds(s))), "reconnects after 1, 2, 5, 10 s, then every 30 s");
        Console.WriteLine("Realtime news, applied snapshots, presence, topics and reconnect schedule passed.");
    }
    // Against a backend: the connection subscribes with the account's token and hears of a
    // reading and a heartbeat of the account's character on the account's topic. It adds no
    // character: the backend's integration tests count them.
    public static async Task RunLive(PartyApi api, string accountId, string sessionId, string characterId)
    {
        using var realtime = new RealtimeClient(api);
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var news = System.Threading.Channels.Channel.CreateUnbounded<(string Topic, RealtimeNews News)>();
        realtime.Connected += again => connected.TrySetResult(again);
        realtime.Message += (topic, data) => news.Writer.TryWrite((topic, RealtimeNews.Parse(data)));
        realtime.Start(RealtimeTopics.For(accountId, []));
        Check(await Task.WhenAny(connected.Task, Task.Delay(10000)) == connected.Task && !connected.Task.Result && realtime.IsConnected, "the realtime connection subscribes (" + realtime.LastProblem + ")");
        async Task<RealtimeNews> Next(Func<RealtimeNews, bool> wanted, string what)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { while (true) { var (topic, item) = await news.Reader.ReadAsync(timeout.Token); if (topic == RealtimeTopics.Own + accountId && wanted(item)) return item; } }
            catch (OperationCanceledException) { throw new Exception("Realtime check failed: no " + what + "."); }
        }
        // News of the pairing and session just made arrives a moment later; then it is quiet.
        await Task.Delay(500); while (news.Reader.TryRead(out _)) { }
        // The character was marked stale; a reading makes it valid again.
        await api.ReportAsync(new PacketReading(sessionId, 10, "Main", "EU", "GLOBAL_EU", 0, "test-fixture-v1", characterId));
        await Next(n => n.Characters.Any(c => c.Id == characterId && c.Valid), "news of the reading");
        await api.HeartbeatAsync(sessionId, characterId);
        await Next(n => n.Seen.ContainsKey(characterId), "heartbeat");
        Console.WriteLine("Windows realtime connection check passed.");
    }
    static void Check(bool condition, string what) { if (!condition) throw new Exception("Realtime check failed: " + what + "."); }
}
