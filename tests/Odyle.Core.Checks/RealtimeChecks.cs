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
        Check(refresh is { Refresh: true, Removed: false, Characters.Length: 0, Seen.Count: 0 }, "a room refresh");
        Check(RealtimeNews.Parse(Json("""{"refresh":true,"removed":true}""")) is { Refresh: true, Removed: true }, "a removed member");
        var changed = RealtimeNews.Parse(Json("""{"refresh":true,"characters":[""" + Lumina.Replace("\"value\":180", "\"value\":220").Replace("}", ",\"shared\":true}") + """]}"""));
        Check(changed.Characters.Single() is { Id: "c", Value: 220 }, "changed characters with their snapshot fields");
        var seen = RealtimeNews.Parse(Json("""{"seen":{"c":"2026-10-12T12:00:10.000Z","x":5}}"""));
        Check(seen is { Refresh: false } && seen.Seen.Count == 1 && seen.Seen["c"] == "2026-10-12T12:00:10.000Z", "heartbeats, ignoring what is not a time");
        Check(RealtimeNews.Parse(Json("[]")).Refresh, "an unreadable message fetches again");
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
