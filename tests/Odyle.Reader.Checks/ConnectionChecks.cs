using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Odyle.Core;
using Odyle.Reader;

// The connection to the server: a saved pairing the server cannot confirm at start, and, online, the
// whole flow against a live backend (the dashboard's integration suite runs it).
static partial class ReaderChecks
{
    // The connection's life on its own (ConnectionLifecycle), without a window: the retries of a saved pairing, what
    // starts a new session, and a server that asks the app to wait.
    static async Task ConnectionLifecycleScenario()
    {
        var link = new ConnectionLifecycle(); var now = DateTimeOffset.UtcNow; var offline = new HttpRequestException("No such host is known.");
        var waits = Enumerable.Range(0, 6).Select(_ => link.ScheduleRetry(offline, now).TotalSeconds).ToArray();
        Check(waits.SequenceEqual([15, 30, 60, 120, 300, 300]) && link.Attempt == 6 && link.RetryTimer.IsEnabled && link.RetryAt == now + TimeSpan.FromMinutes(5) && link.RetryProblem == "" && link.Offline, "a saved pairing is tried again after 15 s, 30 s, 1 min, 2 min, then every 5 minutes: " + string.Join(", ", waits));
        Check(link.ScheduleRetry(new ServerBusyException("Too Many Requests.", now.AddMinutes(20)), now) == TimeSpan.FromMinutes(20) && link.RetryProblem == "busy", "a 429 that asks for longer waits as long as it asks");
        link.Connected(new PublisherSession("s9", 4));
        Check(link.SessionId == "s9" && link.Sequence == 4 && link.Attempt == 0 && !link.RetryTimer.IsEnabled && link.RetryAt is null && !link.Offline, "a connected session ends the retries");
        HttpRequestException Status(HttpStatusCode? code) => new("x", null, code);
        Check(ConnectionLifecycle.SessionGone(Status(HttpStatusCode.NotFound)) && ConnectionLifecycle.SessionGone(Status(HttpStatusCode.BadRequest)) && !ConnectionLifecycle.SessionGone(Status(HttpStatusCode.Unauthorized))
            && !ConnectionLifecycle.SessionGone(Status(HttpStatusCode.TooManyRequests)) && !ConnectionLifecycle.SessionGone(Status(HttpStatusCode.ServiceUnavailable)) && !ConnectionLifecycle.SessionGone(Status(null)), "only a server that answered below 500, and not 401 or 429, is asked for a fresh session");
        link.NoteFailure(new TaskCanceledException());
        Check(!link.NeedsReconnect, "without a connection a failure starts no new session");
        var busyAnswers = 0;
        var api = new PartyApi("https://party.example", "token", null, new Answering(_ => { busyAnswers++; var answer = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"message":"Too Many Requests."}""") }; answer.Headers.Add("Retry-After", "30"); return answer; }));
        link.Use(api);
        Check(link.Endpoint == "https://party.example" && api.AppVersion == Updater.Current.ToString(), "the connection names its server and this build");
        foreach (var (error, renews) in new (Exception, bool)[] { (new ServerBusyException("x", now), false), (Status(HttpStatusCode.BadRequest), false), (Status(HttpStatusCode.Forbidden), false), (new TaskCanceledException(), true), (Status(null), true), (Status(HttpStatusCode.BadGateway), true), (Status(HttpStatusCode.Unauthorized), true) })
        {
            link.NeedsReconnect = false; link.NoteFailure(error);
            Check(link.NeedsReconnect == renews, $"{(error is HttpRequestException { StatusCode: { } code } ? (int)code : error.GetType().Name)} {(renews ? "starts" : "does not start")} a new session");
        }
        link.NeedsReconnect = false; link.LastTick = link.TokenRenewedAt = now;
        Check(!link.SessionDue(now.AddSeconds(44)) && link.SessionDue(now.AddSeconds(46)) && !link.TokenDue(now.AddHours(23)) && link.TokenDue(now.AddHours(25)), "a session silent for 45 seconds is started again and the token is renewed daily");
        try { await api.HeartbeatAsync("s9", null); } catch (ServerBusyException) { }
        Check(busyAnswers == 1 && link.Waiting(api, DateTimeOffset.UtcNow) && link.LastTick == api.BusyUntil && api.BusyUntil > DateTimeOffset.UtcNow.AddSeconds(25), "while the server asks to wait the heartbeat sends nothing, and the quiet time is no lost connection");
        link.Drop();
        Check(link.Api is null && !link.Heartbeat.IsEnabled && link.Endpoint == "https://party.example" && !link.Offline, "dropping the connection keeps the pairing's server");
        link.Forget();
        Check(link.Endpoint == "" && link.RetryAt is null, "forgetting the pairing forgets its server");
    }
    sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(answer(request));
    }
    static async Task OfflineRestoreScenario(MainWindow window)
    {
        var settings = Settings(window);
        // A saved pairing the server cannot confirm at start is kept and tried again by itself, later each time.
        SessionStore.Save(new SavedSession("http://127.0.0.1:1", "token", "device-key"));
        // Npcap is stubbed out: the reader is tried at once but no real capture starts in the checks.
        var probeNpcap = Npcap.Probe; Npcap.Probe = () => false;
        await (Task)Call(window, "RestoreAsync")!;
        Check((bool)Field("readerWanted").GetValue(window)! && ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).IsEnabled, "while the server cannot confirm the pairing the reader starts anyway (here it waits for Npcap)");
        Check(Control<TextBlock>(window, "AccountLabel").Text == "OFFLINE" && Control<TextBlock>(window, "ReadingLabel").Text == "Offline · the reader reads locally and sends what it reads when the server is back.", "the card says it is offline, not that the PC is unpaired");
        ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); Npcap.Probe = probeNpcap;
        Check(window.Link.Api is null && File.Exists(SessionStore.FilePath) && ((Button)settings.FindName("RetryButton")).Visibility == Visibility.Visible, "an unreachable server keeps the pairing and offers Reconnect saved account");
        Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 15 s") && window.Link.RetryTimer.IsEnabled, "the first automatic retry comes after 15 seconds and is shown");
        await (Task)Call(window, "RestoreAsync")!;
        Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 30 s"), "the next retry waits longer");
        // The address box is editable while the saved pairing is retried: the dashboard links and diagnostics keep the pairing's server.
        ((TextBox)settings.FindName("EndpointInput")).Text = "https://typed.example";
        Check(((string)Call(window, "DiagnosticsReport")!).Contains("Account: paired · 127.0.0.1 · not reached yet") && window.Link.Endpoint == "http://127.0.0.1:1", "what is typed in the address box meanwhile is not the pairing's server");
        // Offline the character the game names is followed as online and its reading shown; both wait for the server.
        var offlineCapture = new PacketCapture(); Field("capture").SetValue(window, offlineCapture);
        Call(window, "OnCandidate", offlineCapture, new PacketCandidate(420, ExperimentalOdyleDecoder.Version, CandidateSource.Snapshot) { Base = 120, Bonus = 300 });
        await (Task)Call(window, "SaveDetectedCharacterAsync", offlineCapture, new GameIdentity(7, "OfflineHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        Check(Field("pendingIdentity").GetValue(window) is not null && window.Attribution.OwnerKey == "OfflineHero|2304", "the named character is followed and kept for the server");
        Check(Control<TextBlock>(window, "HeroTitle").Text == "OfflineHero · offline" && Control<TextBlock>(window, "ValueLabel").Text == "420" && Control<TextBlock>(window, "ReadingLabel").Text == "Offline · 420 Odyle read here, sent when the server is back.", "the card shows the local reading while offline");
        // A switch while offline drops the reading of the character left, as online.
        await (Task)Call(window, "CharacterChangedAsync", offlineCapture, 99UL)!;
        Check(window.Attribution.Latest is null && window.Attribution.OwnerKey == "", "a switch offline never moves the reading to the next character");
        Field("capture").SetValue(window, null); Field("pendingIdentity").SetValue(window, null); window.Attribution.Reset();
        await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
        Check(!window.Link.RetryTimer.IsEnabled && window.Link.RetryAt is null && !File.Exists(SessionStore.FilePath), "disconnecting stops the retries and forgets the pairing");
        Check(File.ReadAllText(Log.FilePath).Contains("next in 15 s") && File.ReadAllText(Log.FilePath).Contains("Status: ") && !File.ReadAllText(Log.FilePath).Contains("device-key"), "the log records retries and status lines but never the device key");
    }
    // Online (args: <url> <pair code>): the dashboard's integration suite asserts the sentence this prints.
    static async Task OnlineScenario(MainWindow window, string url, string code)
    {
        var settings = Settings(window); var failed = failures.Count;
        var api = new PartyApi(url); var auth = await api.PairAsync(code);
        window.Link.Api = api;
        await (Task)Call(window, "ConnectAsync", auth)!;
        Check((await api.GetCharactersAsync()).Length == 0, "test account starts empty");
        Check(Control<TextBlock>(window, "StatusLabel").Text == "", "once connected the footer says nothing: " + Control<TextBlock>(window, "StatusLabel").Text);
        Check(Control<Button>(window, "ReportButton").Content as string == "Start", "a paired account can start the reader");
        var capture = new PacketCapture(); Field("capture").SetValue(window, capture);
        var detected = new GameIdentity(42, "DetectedHero", 2304, "Lumiel", "GLOBAL_EU");
        await (Task)Call(window, "SaveDetectedCharacterAsync", capture, detected)!;
        var automatic = (await api.GetCharactersAsync()).Single();
        Check(automatic.CharacterName == "DetectedHero" && automatic.Server == "Lumiel" && automatic.Region == "GLOBAL_EU" && !automatic.Confirmed, "the game's character is saved with unknown energy");
        Check((string)Field("currentCharacterId").GetValue(window)! == automatic.Id, "the card follows the character the game reports");
        // A snapshot is held until the game names its owner after the loading screen, then saved to that character.
        Call(window, "OnCandidate", capture, new PacketCandidate(390, "test-fixture-v1") { Base = 0, Bonus = 390 });
        await (Task)Call(window, "SaveDetectedCharacterAsync", capture, detected)!;
        Check((await api.GetCharactersAsync()).Length == 1, "repeated identity is deduplicated");
        await Until(() => (int)Field("publishedTotal").GetValue(window)! == 390, 400);
        var published = (await api.GetCharactersAsync()).Single(c => c.Id == automatic.Id);
        Check(published.Confirmed && published.Valid && published.Value == 390, "decoded reading is saved to the detected character");
        // Odyle is sent only on first read and on change; the heartbeat keeps it live.
        var sent = window.Link.Sequence;
        await (Task)Call(window, "PublishLatestAsync")!;
        Call(window, "OnCandidate", capture, new PacketCandidate(390, "test-fixture-v1", CandidateSource.Update) { Base = 0, Bonus = 390 });
        await Task.Delay(300);
        Check(window.Link.Sequence == sent, "an unchanged value is not sent again");
        Call(window, "OnCandidate", capture, new PacketCandidate(395, "test-fixture-v1", CandidateSource.Update) { Base = 5, Bonus = 390 });
        await Until(() => (int)Field("publishedTotal").GetValue(window)! == 395, 200);
        Check(window.Link.Sequence == sent + 1, "a changed value is sent once");
        // In-game switches: a character without Odyle is named (its snapshot has no Odyle field), then
        // one with Odyle. The game sends the new snapshot first and names the character only after the
        // loading screen, 6 to 10 seconds later, when the publish delay has long run out.
        await (Task)Call(window, "SaveDetectedCharacterAsync", capture, new GameIdentity(45, "NoOdyleHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        Call(window, "OnCandidate", capture, new PacketCandidate(120, "test-fixture-v1") { Base = 0, Bonus = 120 });
        window.Attribution.AttributePending(DateTimeOffset.UtcNow); await (Task)Call(window, "PublishLatestAsync")!;
        Check(window.Link.Sequence == sent + 1 && Control<TextBlock>(window, "ReadingLabel").Text == "Waiting for the game to name the character.", "a snapshot is not sent before the game names its character, however long the loading screen takes");
        // Two capture interfaces see the same connection, and each reports the switch and the identity.
        for (var copy = 0; copy < 2; copy++)
        {
            await (Task)Call(window, "CharacterChangedAsync", capture, 44UL)!;
            await (Task)Call(window, "SaveDetectedCharacterAsync", capture, new GameIdentity(44, "SwitchedHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        }
        var afterSwitch = await api.GetCharactersAsync();
        Check(afterSwitch.Single(c => c.Id == automatic.Id) is { Value: 395, Valid: false } && afterSwitch.Single(c => c.CharacterName == "NoOdyleHero") is { Confirmed: false }, "a switch never moves the new character's Odyle onto the previous one");
        var switched = afterSwitch.Single(c => c.CharacterName == "SwitchedHero");
        Check(switched is { Value: 120, Valid: true, Confirmed: true } && window.Link.Sequence == sent + 2, "the new character receives its own snapshot, and the second interface's report keeps it live");
        Check((string)Field("currentCharacterId").GetValue(window)! == switched.Id, "the card switches with the game");
        // An update restart: the live character is handed over instead of marked stale, and the
        // next start takes it over under its own session, whose heartbeat keeps it live.
        var handed = (Handover?)Call(window, "LiveHandover");
        Check(handed is { CharacterName: "SwitchedHero", Total: 120 }, "the live character is handed to the restart");
        HandoverStore.Save(handed!); await (Task)Call(window, "StopCaptureAsync", false)!;
        Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Valid: true, Value: 120 }, "a restart does not mark the handed-over character stale");
        var before = window.Link.SessionId;
        await (Task)Call(window, "ConnectAsync", auth)!;
        capture = new PacketCapture(); Field("capture").SetValue(window, capture);
        await (Task)Call(window, "TakeOverAsync")!;
        var resumed = window.Link.SessionId;
        Check(resumed != before && (string)Field("activeCharacterId").GetValue(window)! == switched.Id && Control<TextBlock>(window, "StateText").Text == "Live", "the next start takes the character over under its own session");
        var seen = (await api.GetCharactersAsync()).Single(c => c.Id == switched.Id).LastSeenAt;
        await Task.Delay(100); await api.HeartbeatAsync(resumed, switched.Id);
        Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Valid: true, Value: 120 } live && string.CompareOrdinal(live.LastSeenAt, seen) > 0, "the new session's heartbeat keeps the character live");
        // Sharing is chosen on the dashboard; the app picks it up and lists the room's party.
        using (var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);
            var created = await http.PostAsJsonAsync("api/odyle/create", new { name = "UI room" }); created.EnsureSuccessStatusCode();
            var roomId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("room").GetProperty("id").GetString();
            (await http.PostAsJsonAsync("api/odyle/sharing", new { roomId, characterId = switched.Id, enabled = true })).EnsureSuccessStatusCode();
        }
        await (Task)Call(window, "RefreshPartyAsync", new object[] { null! })!;
        var summary = (CharacterSharing[])((ItemsControl)settings.FindName("SharingList")).ItemsSource;
        Check(summary.Single(g => g.Title.StartsWith("SwitchedHero")).Rooms == "Shared in UI room" && summary.Single(g => g.Title.StartsWith("DetectedHero")).Rooms == "Private in every room", "settings reflect sharing chosen on the dashboard");
        Check(((RoomSnapshot[])Field("snapshots").GetValue(window)!).Single().Members.Single().Characters.Length == 3, "the room snapshot is read from the server");
        Check(((PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource) is [{ Mates.Length: 0, Summary: var line }] && line.StartsWith("Party · ") && Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Visible, "your own characters count for the room's party but are not listed as party members");
        // The checklist kept on the dashboard is shown beside the card and ticked off from here.
        using (var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);
            (await http.PostAsJsonAsync("api/odyle/task", new { title = "Daily dungeon", cadence = "daily" })).EnsureSuccessStatusCode();
        }
        // Opening the panel reads the list by itself.
        Call(window, "SetTasksShown", true, false); await Until(() => (bool)Field("tasksLoaded").GetValue(window)!, 200);
        TaskItem[] Daily() => ((TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource)[0].Tasks;
        Check(Daily().Single() is { Title: "Daily dungeon", Done: false }, "the account's tasks are read from the server");
        await (Task)Call(window, "ToggleTaskAsync", Daily()[0], true)!;
        Check(Daily().Single().Done && (await api.GetTasksAsync()).Tasks.Single().DoneAt.Length > 0, "ticking a task stores the time on the server");
        await (Task)Call(window, "ToggleTaskAsync", Daily()[0], false)!;
        Check(!Daily().Single().Done && (await api.GetTasksAsync()).Tasks.Single().DoneAt.Length == 0, "unticking clears it again");
        Call(window, "SetTasksShown", false, false);
        // A character on an unmapped server is never saved under an assumed identity.
        await (Task)Call(window, "SaveDetectedCharacterAsync", capture, new GameIdentity(43, "UnmappedHero", 2360, null, "GLOBAL_EU"))!;
        Check((await api.GetCharactersAsync()).Length == 3, "unmapped server creates no character");
        Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Value: 120, Valid: false }, "leaving for an unsupported character ends the previous reading's freshness");
        await (Task)Call(window, "StopReaderAsync")!;
        Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Confirmed: true, Value: 120 }, "stopping the reader keeps the value");
        // The server unreachable (as at a start during an outage): the character the game names and its
        // reading wait on this PC, and both reach the server once it answers again.
        var offlineRun = new PacketCapture(); window.Link.Api = null; window.Link.RetryAt = DateTimeOffset.UtcNow.AddSeconds(15);
        Field("capture").SetValue(window, offlineRun); window.Attribution.Reset();
        Call(window, "OnCandidate", offlineRun, new PacketCandidate(512, "test-fixture-v1") { Base = 12, Bonus = 500 });
        await (Task)Call(window, "SaveDetectedCharacterAsync", offlineRun, new GameIdentity(46, "ReturnHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        Check(!(await api.GetCharactersAsync()).Any(c => c.CharacterName == "ReturnHero"), "offline nothing reaches the server");
        window.Link.Api = api; window.Link.RetryAt = null; await (Task)Call(window, "ConnectAsync", auth)!;
        await (Task)Call(window, "PulseAsync")!;
        for (var i = 0; i < 50 && !(await api.GetCharactersAsync()).Any(c => c.CharacterName == "ReturnHero" && c.Confirmed && c.Value == 512); i++) await Task.Delay(100);
        Check((await api.GetCharactersAsync()).Single(c => c.CharacterName == "ReturnHero") is { Confirmed: true, Value: 512 }, "the character named offline is saved and its reading sent once the server answers");
        await (Task)Call(window, "StopReaderAsync")!;
        if (failures.Count == failed) Console.WriteLine("Windows UI game-driven characters, change-only sending, in-game switch attribution, dashboard-managed room sharing, party list and persistence passed.");
    }
}
