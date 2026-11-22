using System.IO;
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
        Check(Field("api").GetValue(window) is null && File.Exists(SessionStore.FilePath) && ((Button)settings.FindName("RetryButton")).Visibility == Visibility.Visible, "an unreachable server keeps the pairing and offers Reconnect saved account");
        Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 15 s") && ((DispatcherTimer)Field("restoreTimer").GetValue(window)!).IsEnabled, "the first automatic retry comes after 15 seconds and is shown");
        await (Task)Call(window, "RestoreAsync")!;
        Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 30 s"), "the next retry waits longer");
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
        Check(!((DispatcherTimer)Field("restoreTimer").GetValue(window)!).IsEnabled && Field("retryAt").GetValue(window) is null && !File.Exists(SessionStore.FilePath), "disconnecting stops the retries and forgets the pairing");
        Check(File.ReadAllText(Log.FilePath).Contains("next in 15 s") && File.ReadAllText(Log.FilePath).Contains("Status: ") && !File.ReadAllText(Log.FilePath).Contains("device-key"), "the log records retries and status lines but never the device key");
    }
    // Online (args: <url> <pair code>): the dashboard's integration suite asserts the sentence this prints.
    static async Task OnlineScenario(MainWindow window, string url, string code)
    {
        var settings = Settings(window); var failed = failures.Count;
        var api = new PartyApi(url); var auth = await api.PairAsync(code);
        Field("api").SetValue(window, api);
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
        var sent = (long)Field("sequence").GetValue(window)!;
        await (Task)Call(window, "PublishLatestAsync")!;
        Call(window, "OnCandidate", capture, new PacketCandidate(390, "test-fixture-v1", CandidateSource.Update) { Base = 0, Bonus = 390 });
        await Task.Delay(300);
        Check((long)Field("sequence").GetValue(window)! == sent, "an unchanged value is not sent again");
        Call(window, "OnCandidate", capture, new PacketCandidate(395, "test-fixture-v1", CandidateSource.Update) { Base = 5, Bonus = 390 });
        await Until(() => (int)Field("publishedTotal").GetValue(window)! == 395, 200);
        Check((long)Field("sequence").GetValue(window)! == sent + 1, "a changed value is sent once");
        // In-game switches: a character without Odyle is named (its snapshot has no Odyle field), then
        // one with Odyle. The game sends the new snapshot first and names the character only after the
        // loading screen, 6 to 10 seconds later, when the publish delay has long run out.
        await (Task)Call(window, "SaveDetectedCharacterAsync", capture, new GameIdentity(45, "NoOdyleHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        Call(window, "OnCandidate", capture, new PacketCandidate(120, "test-fixture-v1") { Base = 0, Bonus = 120 });
        window.Attribution.AttributePending(DateTimeOffset.UtcNow); await (Task)Call(window, "PublishLatestAsync")!;
        Check((long)Field("sequence").GetValue(window)! == sent + 1 && Control<TextBlock>(window, "ReadingLabel").Text == "Waiting for the game to name the character.", "a snapshot is not sent before the game names its character, however long the loading screen takes");
        // Two capture interfaces see the same connection, and each reports the switch and the identity.
        for (var copy = 0; copy < 2; copy++)
        {
            await (Task)Call(window, "CharacterChangedAsync", capture, 44UL)!;
            await (Task)Call(window, "SaveDetectedCharacterAsync", capture, new GameIdentity(44, "SwitchedHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        }
        var afterSwitch = await api.GetCharactersAsync();
        Check(afterSwitch.Single(c => c.Id == automatic.Id) is { Value: 395, Valid: false } && afterSwitch.Single(c => c.CharacterName == "NoOdyleHero") is { Confirmed: false }, "a switch never moves the new character's Odyle onto the previous one");
        var switched = afterSwitch.Single(c => c.CharacterName == "SwitchedHero");
        Check(switched is { Value: 120, Valid: true, Confirmed: true } && (long)Field("sequence").GetValue(window)! == sent + 2, "the new character receives its own snapshot, and the second interface's report keeps it live");
        Check((string)Field("currentCharacterId").GetValue(window)! == switched.Id, "the card switches with the game");
        // An update restart: the live character is handed over instead of marked stale, and the
        // next start takes it over under its own session, whose heartbeat keeps it live.
        var handed = (Handover?)Call(window, "LiveHandover");
        Check(handed is { CharacterName: "SwitchedHero", Total: 120 }, "the live character is handed to the restart");
        HandoverStore.Save(handed!); await (Task)Call(window, "StopCaptureAsync", false)!;
        Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Valid: true, Value: 120 }, "a restart does not mark the handed-over character stale");
        var before = (string)Field("sessionId").GetValue(window)!;
        await (Task)Call(window, "ConnectAsync", auth)!;
        capture = new PacketCapture(); Field("capture").SetValue(window, capture);
        await (Task)Call(window, "TakeOverAsync")!;
        var resumed = (string)Field("sessionId").GetValue(window)!;
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
        var offlineRun = new PacketCapture(); Field("api").SetValue(window, null); Field("retryAt").SetValue(window, DateTimeOffset.UtcNow.AddSeconds(15));
        Field("capture").SetValue(window, offlineRun); window.Attribution.Reset();
        Call(window, "OnCandidate", offlineRun, new PacketCandidate(512, "test-fixture-v1") { Base = 12, Bonus = 500 });
        await (Task)Call(window, "SaveDetectedCharacterAsync", offlineRun, new GameIdentity(46, "ReturnHero", 2304, "Lumiel", "GLOBAL_EU"))!;
        Check(!(await api.GetCharactersAsync()).Any(c => c.CharacterName == "ReturnHero"), "offline nothing reaches the server");
        Field("api").SetValue(window, api); Field("retryAt").SetValue(window, null); await (Task)Call(window, "ConnectAsync", auth)!;
        await (Task)Call(window, "PulseAsync")!;
        for (var i = 0; i < 50 && !(await api.GetCharactersAsync()).Any(c => c.CharacterName == "ReturnHero" && c.Confirmed && c.Value == 512); i++) await Task.Delay(100);
        Check((await api.GetCharactersAsync()).Single(c => c.CharacterName == "ReturnHero") is { Confirmed: true, Value: 512 }, "the character named offline is saved and its reading sent once the server answers");
        await (Task)Call(window, "StopReaderAsync")!;
        if (failures.Count == failed) Console.WriteLine("Windows UI game-driven characters, change-only sending, in-game switch attribution, dashboard-managed room sharing, party list and persistence passed.");
    }
}
