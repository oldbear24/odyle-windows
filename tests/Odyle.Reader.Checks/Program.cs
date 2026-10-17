using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Odyle.Core;
using Odyle.Reader;

static class ReaderChecks
{
    [STAThread] static void Main(string[] args)
    {
        // Window preferences go to a temporary file so the checks never touch the user's.
        PreferencesStore.FilePath = Path.Combine(Path.GetTempPath(), "odyle-check-preferences-" + Guid.NewGuid().ToString("N") + ".json");
        // The checks read English texts whatever the language of the Windows running them.
        Localizer.Use("en");
        // The pairing, the hand-over and the log go to temporary files too.
        var scratch = Directory.CreateTempSubdirectory("odyle-check-").FullName;
        SessionStore.FilePath = Path.Combine(scratch, "session.bin"); HandoverStore.FilePath = Path.Combine(scratch, "handover.json"); Log.UseFolder(Path.Combine(scratch, "logs"));
        // Start with Windows writes to a Run key kept in memory, never to the real one.
        var runKey = new MemoryRunKey(); Autostart.Key = runKey; Autostart.Executable = @"C:\Odyle\Odyle.exe";
        App.Headless = true; var app = new App(); app.InitializeComponent();
        var window = new MainWindow(false);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (args.Length == 2 && args[0] == "--screenshots") { Screenshots(window, args[1]); return; }
                if (args is ["--update-probe"]) { await UpdateProbe(); return; }
                if (args is ["--compact-probe"]) { CompactProbe(window); return; }
                var settings = Settings(window);
                Check(window.FindName("CharactersInput") is null && window.FindName("CharacterInput") is null, "the main window offers no way to pick or type a character");
                Check(settings.FindName("CodeInput") is PasswordBox && window.FindName("CodeInput") is null, "pairing lives in the settings window");
                Check(settings.FindName("SharingList") is ItemsControl && settings.FindName("InviteInput") is null && settings.FindName("RoomsList") is null, "rooms and sharing are managed on the dashboard; settings only summarizes them");
                Check(Control<Button>(window, "ReportButton").Content as string == "Connect", "an unpaired reader points to settings");
                Check(Control<TextBlock>(window, "ValueLabel").Text == "—", "no value is shown before a reading");
                // Screen readers (W#16): every icon button (a glyph, no words) is named from its tooltip, and the
                // reader's state line and the status line are polite live regions.
                var icons = Tree(window).Concat(Tree(settings)).OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => b.Content is string c && Translation.IsGlyph(c)).ToArray();
                Check(icons.Length >= 5 && icons.All(b => System.Windows.Automation.AutomationProperties.GetName(b).Length > 0), "every icon button has a name: " + string.Join(", ", icons.Where(b => System.Windows.Automation.AutomationProperties.GetName(b).Length == 0).Select(b => b.Name)));
                Check(System.Windows.Automation.AutomationProperties.GetName(Control<Button>(window, "PinButton")) == "Stay on top of other windows" && Translation.NameOf("Settings · account, rooms and reader setup") == "Settings" && !Translation.IsGlyph("OK") && Translation.IsGlyph("✕"), "the name is the tooltip's first part");
                Check(new[] { "PacketStateLabel", "StatusLabel" }.All(n => System.Windows.Automation.AutomationProperties.GetLiveSetting(Control<TextBlock>(window, n)) == System.Windows.Automation.AutomationLiveSetting.Polite), "status lines are live regions");
                var root = (FrameworkElement)settings.Content;
                root.Width = 300; root.Height = 480; root.Measure(new Size(300, 480)); root.Arrange(new Rect(0, 0, 300, 480)); root.UpdateLayout();
                var scroll = (ScrollViewer)settings.FindName("Scroller");
                Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "narrow settings window has no horizontal overflow");
                // Updates: nothing is offered before a verified package exists, and an unstamped build never replaces itself.
                Check(!Updater.IsReleaseBuild && ((TextBlock)settings.FindName("UpdateLabel")).Text.Contains("development build") && Control<Button>(window, "UpdateButton").Visibility == Visibility.Collapsed && ((Button)settings.FindName("InstallUpdateButton")).Visibility == Visibility.Collapsed, "an unstamped build shows no update and never replaces itself");
                UpdateChecks();
                Check(System.Text.Json.JsonSerializer.Deserialize<SavedSession>("""{"Endpoint":"https://party.example","Token":"t"}""") is { Token: "t", DeviceKey: null }, "a session saved before device keys still loads and is upgraded on connect");
                SessionChecks();
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
                Check(Field("pendingIdentity").GetValue(window) is not null && (string)Field("readingOwnerKey").GetValue(window)! == "OfflineHero|2304", "the named character is followed and kept for the server");
                Check(Control<TextBlock>(window, "HeroTitle").Text == "OfflineHero · offline" && Control<TextBlock>(window, "ValueLabel").Text == "420" && Control<TextBlock>(window, "ReadingLabel").Text == "Offline · 420 Odyle read here, sent when the server is back.", "the card shows the local reading while offline");
                // A switch while offline drops the reading of the character left, as online.
                await (Task)Call(window, "CharacterChangedAsync", offlineCapture, 99UL)!;
                Check(Field("latest").GetValue(window) is null && (string)Field("readingOwnerKey").GetValue(window)! == "", "a switch offline never moves the reading to the next character");
                Field("capture").SetValue(window, null); Field("pendingIdentity").SetValue(window, null); Call(window, "ResetAttribution");
                await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
                Check(!((DispatcherTimer)Field("restoreTimer").GetValue(window)!).IsEnabled && Field("retryAt").GetValue(window) is null && !File.Exists(SessionStore.FilePath), "disconnecting stops the retries and forgets the pairing");
                Check(File.ReadAllText(Log.FilePath).Contains("next in 15 s") && File.ReadAllText(Log.FilePath).Contains("Status: ") && !File.ReadAllText(Log.FilePath).Contains("device-key"), "the log records retries and status lines but never the device key");
                // A decoded reading is displayed immediately, before its owner is known or saved.
                var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 0, false, false, "") }); Field("currentCharacterId").SetValue(window, "c1");
                Call(window, "OnCandidate", inert, new PacketCandidate(390, ExperimentalOdyleDecoder.Version) { Base = 0, Bonus = 390 });
                Check(Control<TextBlock>(window, "ValueLabel").Text == "390", "decoded reading is shown in the value card");
                Check(Control<TextBlock>(window, "CubeLabel").Text.StartsWith("9 cubes"), "cube count derives from the reading");
                Check(Control<TextBlock>(window, "StateText").Text == "Sending", "unsent reading is marked as sending");
                Check(Control<TextBlock>(window, "HeroTitle").Text == "Detecting character…", "an unattributed reading is never shown under a character's name");
                Check(Control<TextBlock>(window, "ReadingLabel").Text == "Waiting for the game to name the character.", "a snapshot waits for the name the game sends after the loading screen");
                // A snapshot the game leaves unnamed for 30 seconds came without a world load: it goes to the
                // named character only if that character's Odyle was already read this run.
                string Owner() => (string)Field("readingOwnerKey").GetValue(window)!;
                Field("gameCharacterKey").SetValue(window, "TestHero|2304"); Field("namedHasReading").SetValue(window, true);
                Call(window, "AttributePending");
                Check(Owner() == "", "during the loading screen a snapshot waits for the name even when the named character had a reading");
                Field("pendingSince").SetValue(window, DateTimeOffset.UtcNow.AddSeconds(-31)); Field("namedHasReading").SetValue(window, false);
                Call(window, "AttributePending");
                Check(Owner() == "", "an unnamed snapshot never goes to a named character that had no Odyle reading");
                Field("namedHasReading").SetValue(window, true); Call(window, "AttributePending");
                Check(Owner() == "TestHero|2304", "after 30 seconds an unnamed snapshot goes to the named character whose Odyle was read");
                // A resync may have lost a switch (the new character's snapshot and identity): the next reading,
                // even a live update, waits for the game's name and never falls back to the character named before.
                Call(window, "OnResync");
                Call(window, "OnCandidate", inert, new PacketCandidate(200, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 200 });
                Field("pendingSince").SetValue(window, DateTimeOffset.UtcNow.AddSeconds(-31)); Call(window, "AttributePending");
                Check(Owner() == "" && !(bool)Field("namedHasReading").GetValue(window)! && Control<TextBlock>(window, "HeroTitle").Text == "Detecting character…", "after a resync the next reading waits for the game's name instead of going to the character named before");
                Call(window, "OnCandidate", inert, new PacketCandidate(205, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { Base = 5, Bonus = 200 });
                Call(window, "AttributePending"); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
                Check(Owner() == "", "and so do the updates after it");
                // Two game clients on this PC would mix their characters' messages: nothing is read or named until one closes.
                Field("readingOwnerKey").SetValue(window, "TestHero|2304"); Field("gameCharacterKey").SetValue(window, "TestHero|2304");
                Call(window, "OnTwoClients");
                Call(window, "OnCandidate", inert, new PacketCandidate(999, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 999 });
                Call(window, "OnSnapshotWithoutOdyle", inert);
                Check(Field("latest").GetValue(window) is null && Owner() == "" && (string)Field("gameCharacterKey").GetValue(window)! == "" && !(bool)Field("odyleMissingDue").GetValue(window)!, "with two game clients nothing is read, attributed or named");
                Check((string)Call(window, "ReaderStatus")! == "two-clients", "the heartbeat reports two game clients");
                Field("twoClients").SetValue(window, false);
                // The heartbeat's reader state: listening until this connection gave a reading, then reading.
                Field("connectionRead").SetValue(window, false); Call(window, "SetReaderState", Enum.Parse(Field("readerState").FieldType, "Listening"), "Listening to AION2 · waiting for the Odyle snapshot.");
                var listening = (string)Call(window, "ReaderStatus")!;
                Call(window, "OnCandidate", inert, new PacketCandidate(390, ExperimentalOdyleDecoder.Version) { Base = 0, Bonus = 390 }); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
                Check(listening == "listening" && (string)Call(window, "ReaderStatus")! == "reading", "the heartbeat reports listening until Odyle is read on this connection, then reading");
                // A reader started after login (no snapshot this run) shows the next live update and says the game names the character soon.
                Call(window, "ResetAttribution");
                Call(window, "OnCandidate", inert, new PacketCandidate(380, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 380 });
                Check(Control<TextBlock>(window, "ValueLabel").Text == "380" && Control<TextBlock>(window, "ReadingLabel").Text == "The game names the character within a few minutes, or at once after a relog or channel change.", "a live update read without a snapshot is shown and waits for the game's name");
                ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
                // A snapshot without Odyle waits for the game's name like any snapshot; the named character then
                // says it has no Odyle yet instead of waiting for a snapshot forever. A reading still waiting for a
                // name belonged to the character the game left.
                var server = new FakeServer(); Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090", "token", null, server)); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, "");
                Call(window, "OnSnapshotWithoutOdyle", inert);
                Check(Field("latest").GetValue(window) is null, "an unnamed reading is dropped by a snapshot without Odyle");
                await (Task)Call(window, "SaveDetectedCharacterAsync", inert, new GameIdentity(50, "Newcomer", 2304, "Lumiel", "GLOBAL_EU"))!;
                Check(Control<TextBlock>(window, "HeroTitle").Text == "Newcomer · Lumiel" && Control<TextBlock>(window, "ReadingLabel").Text == "No Odyle on Newcomer yet · it may unlock at level 22." && Control<TextBlock>(window, "PacketStateLabel").Text == "Listening to AION2 · Newcomer has no Odyle yet.", "a named character without Odyle says so");
                // Its first Odyle replaces the note. A server that refuses this build's adapter makes the card ask for an
                // update instead of repeating the server's message, and the update check runs at once.
                Call(window, "OnCandidate", inert, new PacketCandidate(15, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { Base = 15, Bonus = 0 });
                Call(window, "AttributePending"); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
                Check((string)Field("odyleMissingKey").GetValue(window)! == "", "a reading clears the note");
                await (Task)Call(window, "PublishLatestAsync")!;
                Check(server.Reports == 1 && (bool)Field("adapterOutdated").GetValue(window)! && Control<TextBlock>(window, "StateText").Text == "Not saved" && Control<TextBlock>(window, "ReadingLabel").Text == "Odyle needs an update to keep reading · it looks for one every hour." && Control<TextBlock>(window, "PacketStateLabel").Text == "The server no longer accepts readings from this Odyle version.", "a refused adapter asks for an update");
                Check((DateTimeOffset)Field("publishBlockedUntil").GetValue(window)! > DateTimeOffset.UtcNow.AddMinutes(9) && DateTimeOffset.UtcNow - (DateTimeOffset)Field("outdatedCheckAt").GetValue(window)! < TimeSpan.FromMinutes(1), "the reading is tried again in ten minutes and the update check ran at once");
                Field("staged").SetValue(window, new StagedUpdate(new Version(9, 9, 9, 9), scratch, "https://github.com/oldbear24/odyle-windows/releases")); Call(window, "RenderHero");
                Check(Control<TextBlock>(window, "ReadingLabel").Text == "Odyle needs an update to keep reading · Restart to update installs it.", "a downloaded update is offered");
                Field("staged").SetValue(window, null);
                server.Refusal = null; Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue); await (Task)Call(window, "PublishLatestAsync")!;
                Check(server.Reports == 2 && !(bool)Field("adapterOutdated").GetValue(window)! && Control<TextBlock>(window, "StateText").Text == "Live" && Control<TextBlock>(window, "ReadingLabel").Visibility == Visibility.Collapsed, "once the server accepts the adapter again the card is live");
                // The first live reading offers to start with Windows (answered below, with Start with Windows).
                Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Visible, "the first live reading offers to start with Windows");
                // The reading carries the game's two numbers and what changed since the one saved before it.
                Check(server.LastReport.Contains("\"base\":15,\"bonus\":0,\"kind\":\"update\",\"delta\":null"), "the first reading of a live streak carries base and bonus and is an update");
                async Task Change(int total, int natural, int items)
                {
                    Call(window, "OnCandidate", inert, new PacketCandidate(total, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { Base = natural, Bonus = items });
                    ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop(); await (Task)Call(window, "PublishLatestAsync")!;
                }
                await Change(0, 0, 0);
                Check(server.LastReport.Contains("\"base\":0,\"bonus\":0,\"kind\":\"spend\",\"delta\":-15") && Control<TextBlock>(window, "SplitLabel").Text.StartsWith("natural 0 · items 0 · −15 at "), "a spend is sent as one, and the card shows the split and the change");
                await Change(15, 15, 0);
                Check(server.LastReport.Contains("\"kind\":\"regen\",\"delta\":15") && Control<TextBlock>(window, "SplitLabel").Text.StartsWith("natural 15 · items 0 · +15 at "), "a rise of the natural pool alone is a regeneration tick");
                // The Odyle clock under the split: the next tick and when each cap the character may have is reached; the operators can hide it.
                var odyleClock = Control<TextBlock>(window, "OdyleClockLabel");
                Check(odyleClock.Visibility == Visibility.Visible && System.Text.RegularExpressions.Regex.IsMatch(odyleClock.Text, @"^\+15 in .+ · full ≈ .+ at 560 / .+ at 840$"), "the card shows the Odyle clock: " + odyleClock.Text);
                Field("clockRules").SetValue(window, ClockRules.Default with { Paused = true }); Call(window, "RenderOdyleClock");
                Check(odyleClock.Visibility == Visibility.Collapsed, "the operators can hide the Odyle clock");
                Field("clockRules").SetValue(window, null);
                // The server's operators pause packet readers: a reading answered 503 stops the capture, the card
                // says so with the operators' announcement, and nothing starts until the heartbeat says it is over.
                server.Paused = true; Field("publishedTotal").SetValue(window, -1); Field("readerWanted").SetValue(window, true);
                await (Task)Call(window, "PublishLatestAsync")!; await Task.Delay(100);
                Check((bool)Field("readerPaused").GetValue(window)! && Field("capture").GetValue(window) is null && Control<TextBlock>(window, "PacketStateLabel").Text == "Paused by the server's operators · the capture is off until they resume it." && Control<TextBlock>(window, "StateText").Text == "Paused" && !Control<Button>(window, "ReportButton").IsEnabled, "a reading refused for the pause stops the capture");
                Check(Control<TextBlock>(window, "ReadingLabel").Text == "Paused by the server · reading resumes by itself when the operators allow it again.", "the card says the server paused reading");
                Call(window, "ShowAnnouncement", new Announcement("Readers are off while we check the new game patch.", "warning")); Call(window, "RenderHero");
                Check(Control<TextBlock>(window, "ReadingLabel").Text == "Paused by the server · Readers are off while we check the new game patch." && Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Collapsed, "the operators' announcement moves into the card");
                await (Task)Call(window, "StartReaderAsync")!;
                Check(Field("capture").GetValue(window) is null && Control<TextBlock>(window, "StateText").Text == "Paused", "nothing starts while readers are paused");
                // The heartbeat says the pause is over: the reader starts again by itself (here it finds no Npcap and says so).
                server.Paused = false; Npcap.Probe = () => false; Field("sessionId").SetValue(window, "s"); Field("needsReconnect").SetValue(window, false);
                Field("tokenRenewedAt").SetValue(window, DateTimeOffset.UtcNow); Field("lastTick").SetValue(window, DateTimeOffset.UtcNow);
                await (Task)Call(window, "PulseAsync")!; await Task.Delay(100);
                var npcapTimer = (DispatcherTimer)Field("npcapTimer").GetValue(window)!;
                Check(!(bool)Field("readerPaused").GetValue(window)! && Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap is not installed · Odyle needs it to read the game." && Control<Button>(window, "NpcapLink").Visibility == Visibility.Visible && npcapTimer.IsEnabled, "once the pause is over the reader starts again by itself");
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "StateText").Text != "Paused" && Control<Button>(window, "ReportButton").IsEnabled, "the announcement returns above the card");
                npcapTimer.Stop(); Npcap.Probe = Npcap.Installed; Call(window, "ShowAnnouncement", new object[] { null! }); Field("readerWanted").SetValue(window, false);
                Call(window, "ResetAttribution"); Field("latest").SetValue(window, null); Field("activeCharacterId").SetValue(window, ""); Field("publishedTotal").SetValue(window, -1);
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                // The party list shows other members' live and stale characters, never your own or offline ones.
                Field("accountId").SetValue(window, "u"); Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", true) });
                Field("snapshots").SetValue(window, new[] { PartySnapshot() }); Call(window, "RenderParty");
                var party = (PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource;
                Check(Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Visible && party.Length == 1 && party[0].Name == "Evening", "a connected account sees the party of its rooms");
                Check(party[0].Mates.Select(m => m.Name).SequenceEqual(new[] { "Lumina", "KaelenAlt" }), "only others' online characters are listed, live first");
                Check(party[0].Mates[0].Value == "180" && party[0].Mates[0].Cubes == "4 cubes" && party[0].Mates[1].Cubes == "5 cubes" && System.Text.RegularExpressions.Regex.IsMatch(party[0].Mates[1].Detail, @"^stale · seen \d+s ago · Kaelen · Lumiel$"), "each mate shows Odyle and cubes, and a stale one says when it was last seen: " + party[0].Mates[1].Detail);
                Check(party[0].Mates[0] is { Detail: "Chanter · Lv 45 · Lumiel · EU", Hint: "Chanter · Lv 45 · Lumina · live from the game" } , "a mate PLAYNC listed leads with its class and level: " + party[0].Mates[0].Detail);
                Field("capture").SetValue(window, null); Field("api").SetValue(window, null); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("latest").SetValue(window, null);
                Field("accountId").SetValue(window, ""); Field("rooms").SetValue(window, Array.Empty<Room>()); Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>()); Call(window, "RenderParty");
                Check(Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Collapsed, "no party list without a connection");
                // The server's announcement sits above the reading card until it is hidden; a new text shows again.
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Collapsed, "no announcement is shown before the server sends one");
                Call(window, "ShowAnnouncement", new Announcement("Maintenance at 10:00 UTC", "warning"));
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "AnnouncementText").Text == "Maintenance at 10:00 UTC", "the server's announcement is shown on the card");
                Call(window, "HideAnnouncement_Click", window, new RoutedEventArgs());
                Call(window, "ShowAnnouncement", new Announcement("Maintenance at 10:00 UTC", "warning"));
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Collapsed, "a hidden announcement stays hidden while its text is the same");
                Call(window, "ShowAnnouncement", new Announcement("Please update to the newest Odyle today."));
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "AnnouncementText").Text == "Please update to the newest Odyle today.", "a new announcement shows again");
                Call(window, "ShowAnnouncement", new object[] { null! });
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Collapsed, "a removed announcement disappears");
                Check(Field("hiddenAnnouncement").GetValue(window) is "Maintenance at 10:00 UTC", "hiding is remembered while the app runs");
                Field("hiddenAnnouncement").SetValue(window, "");
                // News on the account's topic: a changed announcement shows at once, a new warning also as a
                // notification; a release no newer than this build looks for nothing.
                Task News(string json) => (Task)Call(window, "OnNewsAsync", RealtimeTopics.Own + "u", RealtimeNews.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement.Clone()))!;
                typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
                await News("""{"announcement":{"text":"Servers restart at 08:00 UTC","level":"warning"}}""");
                var notice = ((string Title, string Text)?)typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "AnnouncementText").Text == "Servers restart at 08:00 UTC" && notice?.Text == "Servers restart at 08:00 UTC", "an announcement the server pushes shows at once, a warning also as a notification");
                await News("""{"announcement":null}""");
                await News("""{"release":{"version":"0.0.0.1"}}""");
                Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Collapsed && !(bool)Field("updating").GetValue(window)!, "a cleared announcement disappears at once; an older release starts no update check");
                // An update restart hands the live character to the next start, which takes it over without a relog.
                Field("capture").SetValue(window, new PacketCapture()); Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                Field("accountId").SetValue(window, "u"); Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "") }); Field("currentCharacterId").SetValue(window, "c1");
                foreach (var key in new[] { "gameCharacterKey", "detectedCharacterKey", "readingOwnerKey" }) Field(key).SetValue(window, "TestHero|2304");
                Field("gameCharacterName").SetValue(window, "TestHero"); Field("latest").SetValue(window, new PacketCandidate(390, ExperimentalOdyleDecoder.Version));
                Check(Call(window, "LiveHandover") is null, "a reading not sent yet is not handed over");
                Field("activeCharacterId").SetValue(window, "c1");
                var handover = (Handover?)Call(window, "LiveHandover");
                Check(handover is { Endpoint: "http://127.0.0.1:8090", AccountId: "u", CharacterId: "c1", CharacterKey: "TestHero|2304", Total: 390, ParserVersion: ExperimentalOdyleDecoder.Version }, "a live reading is handed to the restart");
                HandoverStore.Save(handover!); await (Task)Call(window, "StopCaptureAsync", false)!;
                // The restarted build: connected again, the reader running, nothing named by the game yet.
                Field("capture").SetValue(window, new PacketCapture()); Field("currentCharacterId").SetValue(window, "");
                await (Task)Call(window, "TakeOverAsync")!;
                Check(!File.Exists(HandoverStore.FilePath) && (string)Field("readingOwnerKey").GetValue(window)! == "TestHero|2304" && (string)Field("currentCharacterId").GetValue(window)! == "c1" && Field("latest").GetValue(window) is PacketCandidate { Total: 390 }, "the next start takes the character over, once");
                Check(Control<TextBlock>(window, "HeroTitle").Text == "TestHero · Lumiel" && Control<TextBlock>(window, "ValueLabel").Text == "390", "the taken-over character is shown with its value without a relog");
                HandoverStore.Save(handover! with { SavedAt = DateTimeOffset.UtcNow - HandoverStore.Lifetime - TimeSpan.FromSeconds(1) });
                Check(HandoverStore.Take(DateTimeOffset.UtcNow) is null && !File.Exists(HandoverStore.FilePath), "an old hand-over is dropped");
                HandoverStore.Save(handover! with { AccountId = "someone-else" }); await (Task)Call(window, "StopCaptureAsync", false)!;
                Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
                Check(!File.Exists(HandoverStore.FilePath) && (string)Field("readingOwnerKey").GetValue(window)! == "" && Field("latest").GetValue(window) is null, "another account's hand-over is ignored");
                // A hand-over from the build before v2 (with its stat owner fields) is taken over and its reading sent
                // again under the adapter that read it. A server that no longer takes that adapter drops the reading
                // without asking for an update, and this build reads the next Odyle change.
                await (Task)Call(window, "StopCaptureAsync", false)!;
                File.WriteAllText(HandoverStore.FilePath, $$"""{"Endpoint":"http://127.0.0.1:8090","AccountId":"u","CharacterId":"c1","CharacterKey":"TestHero|2304","CharacterName":"TestHero","Total":390,"EntityId":51591,"OwnerConfirmed":true,"ParserVersion":"global-reference-v1","Source":0,"SavedAt":"{{DateTimeOffset.UtcNow:O}}"}""");
                var refusing = new FakeServer(); Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090", "token", null, refusing)); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
                Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
                Check(!File.Exists(HandoverStore.FilePath) && refusing.Reports == 1 && (string)Field("readingOwnerKey").GetValue(window)! == "TestHero|2304" && (string)Field("currentCharacterId").GetValue(window)! == "c1" && Field("latest").GetValue(window) is null && !(bool)Field("adapterOutdated").GetValue(window)!, "a refused reading of the previous adapter is dropped without asking for an update");
                Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Updated · the value is read again at the next Odyle change.", "the reader line says the value is read again");
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                await (Task)Call(window, "StopCaptureAsync", false)!;
                Field("api").SetValue(window, null); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue); Field("activeCharacterId").SetValue(window, "");
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("accountId").SetValue(window, ""); Call(window, "RenderHero");
                // Window preferences: pinned above other windows and the checklist beside the card, remembered on this PC.
                Check(!window.Topmost && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && PreferencesStore.Load() is { StayOnTop: false, ShowTasks: false }, "the window starts unpinned and compact");
                // What's new: a first start begins at this version and says nothing; after an update the card says so once.
                var current = $"{Updater.Current.Major}.{Updater.Current.Minor}.{Updater.Current.Build}";
                Check(PreferencesStore.Load().SeenVersion == current && Control<Border>(window, "UpdatedBar").Visibility == Visibility.Collapsed && settings.FindName("WhatsNewButton") is Button, "a first start says nothing of an update; Settings offers What's new");
                Field("preferences").SetValue(window, PreferencesStore.Load() with { SeenVersion = "0.0.1" }); Call(window, "RenderUpdated");
                Check(Control<Border>(window, "UpdatedBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "UpdatedText").Text == $"Updated to {current}.", "after an update the card names the version");
                Call(window, "OpenWhatsNew");
                var news = (Window)Field("whatsNew").GetValue(window)!; var listed = ((StackPanel)news.FindName("Entries")).Children;
                Check(PreferencesStore.Load().SeenVersion == current && Control<Border>(window, "UpdatedBar").Visibility == Visibility.Collapsed, "opening What's new is remembered and the line goes");
                Check(listed.Count > 3 && listed[0] is DockPanel head && string.Concat(head.Children.OfType<TextBlock>().Last().Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) == current + "  this version", "What's new lists the changelog built into the app, this version first and marked");
                news.Close();

                Call(window, "SetStayOnTop", true, true);
                Check(window.Topmost && ((CheckBox)settings.FindName("StayOnTopToggle")).IsChecked == true && PreferencesStore.Load().StayOnTop, "pinning stays on top and is remembered");
                Call(window, "SetTasksShown", true, true);
                Check(Control<Border>(window, "TasksPanel").Visibility == Visibility.Visible && ((CheckBox)settings.FindName("TasksToggle")).IsChecked == true && PreferencesStore.Load() is { StayOnTop: true, ShowTasks: true }, "the checklist opens beside the card and is remembered");
                Check(((TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource).Length == 0 && Control<TextBlock>(window, "TasksHint").Text.StartsWith("Pair"), "without an account the panel asks for pairing");
                // Done follows the game's resets: a tick from before the current period no longer counts.
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090"));
                string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
                Field("tasks").SetValue(window, new[] { new UserTask("t1", "u", "Daily dungeon", "daily", Stamp(DateTimeOffset.UtcNow)), new UserTask("t2", "u", "Daily bounty", "daily"), new UserTask("t3", "u", "Weekly raid", "weekly", Stamp(Checklist.PeriodStart("weekly", DateTimeOffset.UtcNow).AddMinutes(-1))), new UserTask("t4", "u", "Weekly boss", "weekly", Stamp(DateTimeOffset.UtcNow)) });
                Field("tasksLoaded").SetValue(window, true); Call(window, "RenderTasks");
                var groups = (TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource;
                Check(groups.Length == 2 && groups[0].Header == "DAILY" && groups[0].Tasks.Select(t => t.Done).SequenceEqual(new[] { true, false }) && groups[0].Progress.Trim() == "1 / 2", "daily tasks show done and open items with their count");
                Check(groups[1].Header == "WEEKLY" && groups[1].Tasks.Select(t => t.Done).SequenceEqual(new[] { false, true }) && groups[1].Percent == 50, "a weekly tick from before the current period no longer counts");
                Check(groups.All(g => g.Reset.StartsWith("resets in ")) && groups[0].Tasks[0].Hint.StartsWith("Done") && groups[0].Tasks[1].Hint == "Still to do today", "each list counts down to its reset");
                // A list per character (#38): the card's character's tasks join the account's, the others' stay on the dashboard.
                var accountTasks = (UserTask[])Field("tasks").GetValue(window)!;
                Field("tasks").SetValue(window, accountTasks.Concat(new[] { new UserTask("t4", "u", "Nightmare", "weekly", Character: "alt", CharacterName: "Altie"), new UserTask("t5", "u", "Main boss", "weekly", Character: "main", CharacterName: "Mainy") }).ToArray());
                Field("characters").SetValue(window, new[] { new Character("alt", "u", "Altie", "Lumiel", "GLOBAL_EU", 0, false, false, "") }); Field("currentCharacterId").SetValue(window, "alt");
                Call(window, "RenderHero");
                var scopedGroups = (TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource;
                Check(scopedGroups[1].Tasks.Length == 3 && scopedGroups[1].Tasks[2] is { Task.Title: "Nightmare", Hint: "Altie · Still to do this week" } && !scopedGroups[1].Tasks.Any(t => t.Task.Title == "Main boss"), "the card's character brings its own list, named, and other characters' stay out: " + string.Join(",", scopedGroups[1].Tasks.Select(t => t.Task.Title)));
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("tasks").SetValue(window, accountTasks); Call(window, "RenderHero");
                Check(((TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource)[1].Tasks.Length == 2, "without a character only the account's list shows");
                var shell = (FrameworkElement)window.Content; shell.Width = 520; shell.Measure(new Size(520, double.PositiveInfinity)); shell.Arrange(new Rect(0, 0, 520, shell.DesiredSize.Height)); shell.UpdateLayout();
                Check(Control<Grid>(window, "Card").ActualWidth == 280 && Control<Border>(window, "TasksPanel").ActualWidth == 240, "the card keeps its width and the checklist takes its own column");
                shell.Width = double.NaN;
                Call(window, "SetTasksShown", false, true); Call(window, "SetStayOnTop", false, true);
                Check(!window.Topmost && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && PreferencesStore.Load() is { StayOnTop: false, ShowTasks: false }, "unpinning and hiding the checklist are remembered too");
                // Compact mode cuts the window down to your Odyle and your party's, in the same window and place, and is remembered.
                Check(!PreferencesStore.Load().CompactMode && Control<Border>(window, "CompactPanel").Visibility == Visibility.Collapsed && (string)Control<Button>(window, "CompactButton").ToolTip == "Compact mode · your Odyle and your party's, nothing else (Ctrl+Alt+O)", "the window starts with the full card, and the compact button names its shortcut");
                var mateSeen = Stamp(DateTimeOffset.UtcNow);
                Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 130, true, true, Stamp(DateTimeOffset.UtcNow.AddMinutes(-5))) }); Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u");
                Field("snapshots").SetValue(window, new[] { new RoomSnapshot(new Room("r1", "Evening", 40, "reporter", true), [new RoomMember("m1", "l", "reporter", "Lumina", [new Character("l1", "l", "Lumina", "Lumiel", "GLOBAL_EU", 180, true, true, mateSeen, mateSeen, "packet")])], mateSeen) });
                window.Left = 300; window.Top = 200; Call(window, "SetTasksShown", true, false);
                Call(window, "SetCompact", true, true); Call(window, "RenderHero");
                Check(PreferencesStore.Load().CompactMode && Control<Border>(window, "CompactPanel").Visibility == Visibility.Visible && Control<Grid>(window, "Card").Visibility == Visibility.Collapsed && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && window.Left == 300 && window.Top == 200,
                    "compact mode replaces the card and the checklist in the same window and place, and is remembered");
                Check(Control<TextBlock>(window, "CompactName").Text == "TestHero" && Control<TextBlock>(window, "CompactValue").Text == "130" && Control<TextBlock>(window, "CompactCubes").Text == "3 cubes", "your row shows your character, its Odyle and cubes");
                Check(Control<ItemsControl>(window, "CompactMates").ItemsSource is CompactMate[] and [{ Name: "Lumina", Value: "180", Cubes: "4 cubes", Hint: "Lumina · live from the game" }] && Control<TextBlock>(window, "CompactNote").Visibility == Visibility.Collapsed, "your party's rows follow, with their Odyle and cubes");
                var compactShell = (FrameworkElement)window.Content; compactShell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); compactShell.Arrange(new Rect(compactShell.DesiredSize)); compactShell.UpdateLayout();
                Check(Math.Abs(compactShell.ActualWidth - CompactStrip.Width) < 1 && compactShell.ActualHeight < 90, "compact mode is 220 px wide and only as tall as its rows");
                Field("readerPaused").SetValue(window, true); Call(window, "RenderHero");
                Check(Control<TextBlock>(window, "CompactNote").Text == "Paused by the server", "a server pause shows under the rows");
                Field("readerPaused").SetValue(window, false);
                var hotkey = (ComboBox)settings.FindName("CompactHotkeyInput");
                Check((string)hotkey.SelectedValue == "Ctrl+Alt+O" && hotkey.Items.Count == CompactStrip.Hotkeys.Length, "Settings offer the compact mode shortcuts, Ctrl+Alt+O first");
                hotkey.SelectedValue = "";
                Check(PreferencesStore.Load().CompactHotkey == "" && (string)Control<Button>(window, "CompactButton").ToolTip == "Compact mode · your Odyle and your party's, nothing else", "the shortcut can be turned off, and that is remembered");
                hotkey.SelectedValue = CompactStrip.DefaultHotkey; Call(window, "SetCompact", false, true);
                Check(!PreferencesStore.Load().CompactMode && Control<Grid>(window, "Card").Visibility == Visibility.Visible && Control<Border>(window, "TasksPanel").Visibility == Visibility.Visible && window.Left == 300 && window.Top == 200 && PreferencesStore.Load().CompactHotkey == "Ctrl+Alt+O",
                    "the full card and the checklist come back in the same place, and that is remembered");
                compactShell.Width = double.NaN; Call(window, "SetTasksShown", false, false);
                // A membership answer the readings contradict is pointed out under the clock; readings that fit it are not.
                Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 700, true, true, Stamp(DateTimeOffset.UtcNow.AddMinutes(-5)), NaturalMax: 700, AnsweredCap: 560) }); Call(window, "RenderHero");
                Check(Control<TextBlock>(window, "CapMismatchLabel") is { Visibility: Visibility.Visible, Text: "Natural pool held 700, more than 560: has the membership started? Answer on the dashboard." }, "the card points out readings above the answered cap");
                Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 700, true, true, Stamp(DateTimeOffset.UtcNow.AddMinutes(-5)), NaturalMax: 700, AnsweredCap: 840) }); Call(window, "RenderHero");
                Check(Control<TextBlock>(window, "CapMismatchLabel").Visibility == Visibility.Collapsed, "readings that fit the answer need no hint");
                Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>()); Field("accountId").SetValue(window, "");
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Call(window, "RenderHero");
                // Language: automatic by default; a choice is remembered and applies after a restart.
                var language = (ComboBox)settings.FindName("LanguageInput");
                Check((string)language.SelectedValue == Localizer.Automatic && language.Items.Count == 3, "the language follows Windows until one is chosen");
                language.SelectedValue = "cs";
                Check(PreferencesStore.Load().Language == "cs" && ((FrameworkElement)settings.FindName("LanguageRestartPanel")).Visibility == Visibility.Visible && Localizer.Language == "en", "choosing Czech is remembered and offers a restart");
                language.SelectedValue = Localizer.Automatic; Check(PreferencesStore.Load().Language == Localizer.Automatic, "automatic can be chosen again");
                // The offer after the first live reading waits for one answer; declining is remembered on this PC.
                Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Visible && !PreferencesStore.Load().AutostartOffered, "the offer waits for an answer");
                Call(window, "DeclineAutostart_Click", window, new RoutedEventArgs());
                Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Collapsed && PreferencesStore.Load().AutostartOffered && runKey.Values.Count == 0, "declining the offer is remembered and writes nothing");
                Call(window, "OfferAutostart");
                Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Collapsed, "an answered offer does not come back");
                // Start with Windows: "<Odyle.exe>" --tray in the Run key, removed again when switched off.
                Call(window, "SetAutostart", true);
                Check(runKey.Values.Single() is { Key: "Odyle", Value: "\"C:\\Odyle\\Odyle.exe\" --tray" } && ((CheckBox)settings.FindName("AutostartToggle")).IsChecked == true && Autostart.Enabled, "Start with Windows writes the Run entry for a start in the tray");
                Call(window, "SetAutostart", false);
                Check(runKey.Values.Count == 0 && ((CheckBox)settings.FindName("AutostartToggle")).IsChecked == false, "switching it off removes the entry");
                runKey.Values["Odyle"] = "\"C:\\Moved away\\Odyle.exe\" --tray"; Autostart.Repair();
                Check(runKey.Values["Odyle"] == "\"C:\\Odyle\\Odyle.exe\" --tray", "an entry for an executable that is gone starts this one");
                runKey.Values["Odyle"] = StartupCommand.For(Environment.ProcessPath!); Autostart.Repair();
                Check(runKey.Values["Odyle"] == StartupCommand.For(Environment.ProcessPath!), "an entry for another existing Odyle is left alone"); runKey.Values.Clear();
                Field("preferences").SetValue(window, (Preferences)Field("preferences").GetValue(window)! with { AutostartOffered = false }); Call(window, "OfferAutostart");
                Call(window, "AcceptAutostart_Click", window, new RoutedEventArgs());
                Check(runKey.Values.Single() is { Key: "Odyle", Value: "\"C:\\Odyle\\Odyle.exe\" --tray" } && PreferencesStore.Load().AutostartOffered && Control<Border>(window, "AutostartOffer").Visibility == Visibility.Collapsed, "accepting the offer starts Odyle with Windows");
                runKey.Values.Clear();
                // Npcap is checked whenever settings open.
                Npcap.Probe = () => false; Call(window, "RenderNpcap");
                Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap not found · install it to read the game" && ((Button)settings.FindName("NpcapButton")).Visibility == Visibility.Visible, "a missing Npcap is pointed out with its download");
                Npcap.Probe = () => true; Call(window, "RenderNpcap"); Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap installed", "an installed Npcap is confirmed");
                // Npcap restricted to administrators is explained in settings and on the reader line.
                Npcap.Restricted = () => true; Call(window, "RenderNpcap");
                Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap installed · only administrators may capture", "settings say Npcap only lets administrators capture");
                Call(window, "ShowNpcapProblem", new InvalidOperationException("No Npcap capture interfaces available."));
                Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap lets only administrators capture · reinstall it without “Restrict Npcap driver's access to Administrators only”." && Control<Button>(window, "NpcapLink") is { Visibility: Visibility.Visible, Content: "Npcap setup ↗" }, "a reader that finds no interface explains the administrators-only option");
                Npcap.Restricted = () => false;
                // Without Npcap the reader line links to its download and Npcap is looked for every 10 seconds;
                // once it is there an unpaired app only clears the line (a paired one starts the reader).
                var paired = Field("api").GetValue(window); Field("api").SetValue(window, null);
                Npcap.Probe = () => false; Call(window, "ShowNpcapProblem", new object[] { null! });
                Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap is not installed · Odyle needs it to read the game." && Control<Button>(window, "NpcapLink") is { Visibility: Visibility.Visible, Content: "Install Npcap ↗" } && npcapTimer.IsEnabled, "a missing Npcap shows in the reader row with its download");
                await (Task)Call(window, "NpcapTickAsync")!;
                Check(npcapTimer.IsEnabled && Control<Button>(window, "NpcapLink").Visibility == Visibility.Visible, "a missing Npcap is looked for again");
                Npcap.Probe = () => true; await (Task)Call(window, "NpcapTickAsync")!;
                Check(!npcapTimer.IsEnabled && Control<Button>(window, "NpcapLink").Visibility == Visibility.Collapsed && Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap is installed · pair Odyle in settings to read the game.", "an installed Npcap clears the line");
                // A saved pairing that is still being retried does not ask to pair again.
                Field("retryAt").SetValue(window, DateTimeOffset.UtcNow.AddSeconds(15)); Npcap.Probe = () => false; Call(window, "ShowNpcapProblem", new object[] { null! });
                Npcap.Probe = () => true; await (Task)Call(window, "NpcapTickAsync")!;
                Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Stopped · start the reader before logging in." && (string)Control<Button>(window, "ReportButton").Content == "Start", "a paired app still retrying the server can start its reader: it reads locally until the server answers");
                Field("retryAt").SetValue(window, null);
                // Copy diagnostics: version, Npcap, interfaces, the reader's last states and the end of the log,
                // with the account and this PC's name masked (built here without touching the clipboard).
                Log.Info("Connected to http://127.0.0.1:8090 as account abcdefghij12345 on device \"DESKTOP-CHECK\".");
                var diagnostics = (string)Call(window, "DiagnosticsReport")!;
                Check(diagnostics.StartsWith("Odyle diagnostics · ") && diagnostics.Contains("Npcap: installed") && diagnostics.Contains("Capture interfaces: ") && diagnostics.Contains("Account: not paired") && diagnostics.Contains("Problem · Npcap is not installed"), "diagnostics list the version, Npcap and the reader's last states");
                Check(diagnostics.Contains("account [account] on device \"[this PC]\"") && !diagnostics.Contains("abcdefghij12345") && !diagnostics.Contains("DESKTOP-CHECK") && !diagnostics.Contains("device-key"), "diagnostics mask the account and this PC's name");
                Field("api").SetValue(window, paired);
                // --unpair (the uninstaller): a running Odyle is asked to quit and waited for, then the pairing is
                // forgotten on this PC also when the server cannot be reached. A made-up user keeps the real Odyle out of it.
                var someone = "odyle-check-" + Guid.NewGuid().ToString("N");
                using (var asked = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Quit-" + someone))
                using (var ready = new ManualResetEventSlim())
                {
                    var running = new Thread(() => { using var owned = new Mutex(true, "Local\\Odyle-" + someone); ready.Set(); asked.WaitOne(TimeSpan.FromSeconds(30)); owned.ReleaseMutex(); });
                    running.Start(); ready.Wait();
                    SessionStore.Save(new SavedSession("http://127.0.0.1:1", "token", "device-key"));
                    typeof(App).GetMethod("Unpair", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [someone]);
                    Check(running.Join(TimeSpan.FromSeconds(5)) && !File.Exists(SessionStore.FilePath) && File.ReadAllText(Log.FilePath).Contains("The server could not be told to forget this PC"), "--unpair asks a running Odyle to quit, then forgets the pairing on this PC");
                }
                // The window reopens where it was, while that is on a connected screen.
                var kept = Field("preferences").GetValue(window)!;
                var area = System.Windows.SystemParameters.WorkArea;
                Field("preferences").SetValue(window, new Preferences(Left: area.Left + 40, Top: area.Top + 30)); Call(window, "RestorePosition");
                Check(window.WindowStartupLocation == WindowStartupLocation.Manual && window.Left == area.Left + 40 && window.Top == area.Top + 30, "a remembered position on a connected screen is restored");
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Field("preferences").SetValue(window, new Preferences(Left: -40000, Top: -40000)); Call(window, "RestorePosition");
                Check(window.WindowStartupLocation == WindowStartupLocation.CenterScreen && window.Left == area.Left + 40, "a position on a screen that is gone opens centred instead");
                Field("preferences").SetValue(window, kept);
                // The XAML is written in English and translated once per window.
                Localizer.Use("cs");
                try
                {
                    var czech = new SettingsWindow();
                    Check(czech.Title == "Nastavení Odyle" && ((Button)czech.FindName("PasteButton")).Content as string == "Vložit odkaz a připojit" && ((Expander)czech.FindName("ServerOptions")).Header as string == "Adresa serveru"
                        && ((Button)czech.FindName("NpcapButton")).ToolTip as string == "Pro čtení paketů musí být nainstalovaný Npcap", "settings speak Czech when Czech is chosen");
                    Check(Localizer.Tn(3, "{0} cube", "{0} cubes") == "3 kostky" && Localizer.T("Publisher session expired. Reconnect.") == "Relace čtečky vypršela. Připoj se znovu.", "Czech plurals and server messages");
                }
                finally { Localizer.Use("en"); }
                // Planned runs: one reminder 15 minutes before a run the account joined, once per start time.
                string RunAt(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
                var soon = DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(-20);
                RoomSnapshot Planned(string startsAt) => new(new Room("r1", "Evening", 40, "owner", true), [], DateTime.UtcNow.ToString("O"), [new PlannedRun("run1", "Fire Temple", startsAt, "Bring potions", "u", ["u", "l"]), new PlannedRun("run2", "Ice Cave", RunAt(soon), "", "l", ["l"])]);
                (string Title, string Text)? Notice() => ((string Title, string Text)?)typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                void ClearNotice() => typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
                Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon)) }); Call(window, "CheckReminders");
                Check(Notice() is { Title: "Fire Temple starts in 10 minutes", Text: var text } && text.StartsWith("Evening · starts at ") && text.EndsWith("\nBring potions"), "a joined run is announced with its dungeon, room, local time and note");
                ClearNotice(); Call(window, "CheckReminders");
                Check(Notice() is null && PreferencesStore.Load().RemindedRuns is [var reminded] && reminded.StartsWith("run1|"), "a run is announced once, also across restarts, and runs others joined are not");
                Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(3))) }); Call(window, "CheckReminders");
                Check(Notice() is { Title: "Fire Temple starts in 13 minutes" }, "a run moved to another time is announced again");
                // The account's notification settings (the dashboard's Account & data): no tray reminders, quiet hours.
                ClearNotice(); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(1))) });
                Field("notifications").SetValue(window, NotificationSettings.Default with { Channels = new(Tray: false) }); Call(window, "CheckReminders");
                Check(Notice() is null, "reminders can be switched off for the tray on the dashboard");
                string Hhmm(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm");
                Field("notifications").SetValue(window, NotificationSettings.Default with { Quiet = new(Hhmm(DateTimeOffset.UtcNow.AddMinutes(-30)), Hhmm(DateTimeOffset.UtcNow.AddMinutes(30)), "UTC") }); Call(window, "CheckReminders");
                Check(Notice() is null && PreferencesStore.Load().RemindedRuns!.Contains(Reminders.Key(new PlannedRun("run1", "Fire Temple", RunAt(soon.AddMinutes(1)), "", "u", []), 15)), "quiet hours show no reminder, and it does not come later");
                ClearNotice(); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(2))) }); Field("notifications").SetValue(window, NotificationSettings.Default with { LeadMinutes = [5] }); Call(window, "CheckReminders");
                Check(Notice() is null, "a run beyond the account's reminder times waits");
                // The settings move to the dashboard: Settings links there, and an old local "off" moves to the account once.
                Check(settings.FindName("RemindersToggle") is null && settings.FindName("NotificationsButton") is Button, "settings link to the dashboard's notification settings");
                var moving = new FakeServer(); Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090", "token", null, moving));
                Field("preferences").SetValue(window, PreferencesStore.Load() with { RemindRuns = false, RemindRunsMoved = false });
                await (Task)Call(window, "FollowNotificationsAsync", NotificationSettings.Default)!;
                Check(moving.LastNotifications.Contains("\"tray\":false") && Field("notifications").GetValue(window) is NotificationSettings { RunsInTray: false, Saved: true } && PreferencesStore.Load().RemindRunsMoved, "an old local switch-off moves to the account");
                moving.LastNotifications = ""; await (Task)Call(window, "FollowNotificationsAsync", NotificationSettings.Default)!;
                Check(moving.LastNotifications == "" && Field("notifications").GetValue(window) is NotificationSettings { RunsInTray: true }, "once: from then on the account's settings count");
                await (Task)Call(window, "FollowNotificationsAsync", new object[] { null! })!;
                Check(Field("notifications").GetValue(window) is NotificationSettings { RunsInTray: false }, "a backend without them keeps the local choice");
                // Cap alerts from the server show in the tray when the account wants them there.
                var capNews = RealtimeNews.Parse(System.Text.Json.JsonDocument.Parse("""{"capAlerts":[{"characterId":"c","characterName":"Lumina","state":"full","base":840,"cap":840,"settled":true,"tick":15}]}""").RootElement.Clone());
                ClearNotice(); Field("notifications").SetValue(window, NotificationSettings.Default); await (Task)Call(window, "OnNewsAsync", "odyle.user.u", capNews)!;
                Check(Notice() is null, "cap alerts stay off until the account turns them on");
                Field("notifications").SetValue(window, NotificationSettings.Default with { Kinds = new(Cap: true) }); await (Task)Call(window, "OnNewsAsync", "odyle.user.u", capNews)!;
                Check(Notice() is { Title: "Lumina is losing Odyle", Text: "Its natural pool is full: each tick of 15 is lost until you spend some." }, "a cap alert shows in the tray");
                // The party line of each room, the next run with Going, and a run of yours that was cancelled.
                var seenNow = Stamp(DateTimeOffset.UtcNow);
                Character Member(string id, string user, string name, int value) => new(id, user, name, "Lumiel", "GLOBAL_EU", value, true, true, Stamp(DateTimeOffset.UtcNow.AddHours(-1)), seenNow, "packet", Shared: true);
                RoomSnapshot Room(params PlannedRun[] runs) => new(new Room("r1", "Evening", 40, "owner", true), [new RoomMember("m1", "u", "owner", "You", [Member("c1", "u", "Ayla", 130)], true), new RoomMember("m2", "k", "reporter", "Kaelen", [Member("c2", "k", "Kael", 85)], true), new RoomMember("m3", "n", "reporter", "Nyx", [], false)], DateTime.UtcNow.ToString("O"), runs);
                var planned = new PlannedRun("run9", "Fire Temple", RunAt(DateTimeOffset.UtcNow.AddMinutes(50)), "", "u", ["u", "k"]);
                var planServer = new FakeServer(); Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090", "token", null, planServer)); Field("accountId").SetValue(window, "u");
                Field("snapshots").SetValue(window, new[] { Room(planned) }); Call(window, "RenderParty");
                var partyRooms = (PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource;
                Check(partyRooms is [{ Summary: "Party · 2 cubes · lowest Kaelen · 1 not live", Mates.Length: 1 }], "a room's party line counts your character with your mates': " + partyRooms.FirstOrDefault()?.Summary);
                Check(Control<Border>(window, "RunChipBox").Visibility == Visibility.Visible && System.Text.RegularExpressions.Regex.IsMatch(Control<TextBlock>(window, "RunChipLabel").Text, @"^Fire Temple · in (49|50)m · 2 going · Evening$") && (string)Control<Button>(window, "RunGoingButton").Content == "Going ✓", "the next run you go to, with its countdown: " + Control<TextBlock>(window, "RunChipLabel").Text);
                Call(window, "RunGoing_Click", Control<Button>(window, "RunGoingButton"), new RoutedEventArgs()); await Task.Delay(300);
                Check(planServer.LastPlan.Contains("\"roomId\":\"r1\"") && planServer.LastPlan.Contains("\"id\":\"run9\"") && planServer.LastPlan.Contains("\"going\":false"), "Going ✓ leaves the run: " + planServer.LastPlan);
                Field("snapshots").SetValue(window, new[] { Room() }); Call(window, "RenderRunChip");
                Check(Control<TextBlock>(window, "RunChipNotice") is { Visibility: Visibility.Visible, Text: var cancelled } && cancelled.StartsWith("Fire Temple at ") && cancelled.EndsWith(" was cancelled") && Control<TextBlock>(window, "RunChipLabel").Visibility == Visibility.Collapsed, "a cancelled run of yours says so");
                Field("chipNoticeUntil").SetValue(window, DateTimeOffset.MinValue); Call(window, "RenderRunChip");
                Check(Control<Border>(window, "RunChipBox").Visibility == Visibility.Collapsed, "no run, no chip");
                // A character none of your rooms can see says so on the card, with a link to the dashboard.
                Field("characters").SetValue(window, new[] { Member("c1", "u", "Ayla", 130) }); Field("currentCharacterId").SetValue(window, "c1");
                Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", false, []) }); Call(window, "RenderHero");
                Check(Control<Button>(window, "ShareHintButton").Visibility == Visibility.Visible, "a character no room sees says so");
                Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", true, ["c1"]) }); Call(window, "RenderHero");
                Check(Control<Button>(window, "ShareHintButton").Visibility == Visibility.Collapsed, "a shared character does not");
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("rooms").SetValue(window, Array.Empty<Room>());
                Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>()); Field("api").SetValue(window, null);
                Field("notifications").SetValue(window, NotificationSettings.Default);
                Field("preferences").SetValue(window, PreferencesStore.Load()); Field("accountId").SetValue(window, ""); Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>());
                Field("api").SetValue(window, null); Field("tasks").SetValue(window, Array.Empty<UserTask>()); Field("tasksLoaded").SetValue(window, false); Call(window, "RenderTasks");
                if (args.Length == 2)
                {
                    var api = new PartyApi(args[0]); var auth = await api.PairAsync(args[1]);
                    Field("api").SetValue(window, api);
                    await (Task)Call(window, "ConnectAsync", auth)!;
                    Check((await api.GetCharactersAsync()).Length == 0, "test account starts empty");
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
                    Call(window, "AttributePending"); await (Task)Call(window, "PublishLatestAsync")!;
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
                    using (var http = new HttpClient { BaseAddress = new Uri(args[0].TrimEnd('/') + "/") })
                    {
                        http.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);
                        var created = await http.PostAsJsonAsync("api/odyle/create", new { name = "UI room" }); created.EnsureSuccessStatusCode();
                        var roomId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("room").GetProperty("id").GetString();
                        (await http.PostAsJsonAsync("api/odyle/sharing", new { roomId, characterId = switched.Id, enabled = true })).EnsureSuccessStatusCode();
                    }
                    await (Task)Call(window, "RefreshPartyAsync")!;
                    var summary = (CharacterSharing[])((ItemsControl)settings.FindName("SharingList")).ItemsSource;
                    Check(summary.Single(g => g.Title.StartsWith("SwitchedHero")).Rooms == "Shared in UI room" && summary.Single(g => g.Title.StartsWith("DetectedHero")).Rooms == "Private in every room", "settings reflect sharing chosen on the dashboard");
                    Check(((RoomSnapshot[])Field("snapshots").GetValue(window)!).Single().Members.Single().Characters.Length == 3, "the room snapshot is read from the server");
                    Check(((PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource) is [{ Mates.Length: 0, Summary: var line }] && line.StartsWith("Party · ") && Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Visible, "your own characters count for the room's party but are not listed as party members");
                    // The checklist kept on the dashboard is shown beside the card and ticked off from here.
                    using (var http = new HttpClient { BaseAddress = new Uri(args[0].TrimEnd('/') + "/") })
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
                    Field("capture").SetValue(window, offlineRun); Call(window, "ResetAttribution");
                    Call(window, "OnCandidate", offlineRun, new PacketCandidate(512, "test-fixture-v1") { Base = 12, Bonus = 500 });
                    await (Task)Call(window, "SaveDetectedCharacterAsync", offlineRun, new GameIdentity(46, "ReturnHero", 2304, "Lumiel", "GLOBAL_EU"))!;
                    Check(!(await api.GetCharactersAsync()).Any(c => c.CharacterName == "ReturnHero"), "offline nothing reaches the server");
                    Field("api").SetValue(window, api); Field("retryAt").SetValue(window, null); await (Task)Call(window, "ConnectAsync", auth)!;
                    await (Task)Call(window, "PulseAsync")!;
                    for (var i = 0; i < 50 && !(await api.GetCharactersAsync()).Any(c => c.CharacterName == "ReturnHero" && c.Confirmed && c.Value == 512); i++) await Task.Delay(100);
                    Check((await api.GetCharactersAsync()).Single(c => c.CharacterName == "ReturnHero") is { Confirmed: true, Value: 512 }, "the character named offline is saved and its reading sent once the server answers");
                    await (Task)Call(window, "StopReaderAsync")!;
                    Console.WriteLine("Windows UI game-driven characters, change-only sending, in-game switch attribution, dashboard-managed room sharing, party list and persistence passed.");
                }
                // Open in Odyle: a second start hands its link to the running Odyle through the pipe; nobody listening is a plain failure.
                var pipeUser = "check-" + Guid.NewGuid().ToString("N"); var pipeLink = new TaskCompletionSource<string>(); var pipe = new PairPipe(pipeUser, link => pipeLink.TrySetResult(link));
                var pipeSent = false; for (var i = 0; i < 20 && !(pipeSent = PairPipe.Send(pipeUser, "odyle://pair?link=x")); i++) await Task.Delay(100);
                Check(pipeSent && await Task.WhenAny(pipeLink.Task, Task.Delay(3000)) == pipeLink.Task && pipeLink.Task.Result == "odyle://pair?link=x", "a second start hands its link to the running Odyle");
                pipe.Stop(); Check(!PairPipe.Send("check-nobody-" + Guid.NewGuid().ToString("N"), "odyle://pair?link=x"), "with no Odyle running the link is not taken");
                Check(PairProtocol.CommandFor(@"C:\Apps\Odyle\Odyle.exe") == "\"C:\\Apps\\Odyle\\Odyle.exe\" --pair \"%1\"", "odyle:// links start Odyle with --pair");
                Console.WriteLine("Windows UI compact window, compact mode, What's new, separate settings, reading card, party list, narrow layout, reader pause, Npcap setup, Start with Windows offer, diagnostics and --unpair passed.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { try { await (Task)Call(window, "QuitAsync")!; } finally { try { File.Delete(PreferencesStore.FilePath); } catch (IOException) { } try { Directory.Delete(scratch, true); } catch (IOException) { } Dispatcher.CurrentDispatcher.InvokeShutdown(); } }
        });
        Dispatcher.Run();
    }
    // The pairing is DPAPI-protected and written whole; a file this account cannot read
    // counts as not paired, says so and is removed instead of failing the start.
    static void SessionChecks()
    {
        SessionStore.Save(new SavedSession("https://party.example", "token", "key"));
        Check(SessionStore.Load(out var unreadable) is { Endpoint: "https://party.example", Token: "token", DeviceKey: "key" } && !unreadable && !File.ReadAllText(SessionStore.FilePath).Contains("token"), "the pairing is saved encrypted and read back");
        Check(Directory.GetFiles(Path.GetDirectoryName(SessionStore.FilePath)!, "*.tmp").Length == 0, "saving leaves no temporary file");
        File.WriteAllBytes(SessionStore.FilePath, [1, 2, 3, 4, 5]);
        Check(SessionStore.Load(out unreadable) is null && unreadable && !File.Exists(SessionStore.FilePath), "a damaged or foreign session.bin counts as not paired and is removed");
        Check(SessionStore.Load(out unreadable) is null && !unreadable, "without a saved pairing nothing is unreadable");
    }
    // Installing copies the staged files the way Windows allows while the app runs: the
    // executable moves aside to .old and the rest is overwritten. A staged folder counts
    // only when its Odyle.exe is stamped with the folder's version and that is newer.
    static void UpdateChecks()
    {
        var root = Directory.CreateTempSubdirectory("odyle-update-check-").FullName;
        var staging = Updater.Staging;
        try
        {
            var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "Odyle.exe"), "running"); File.WriteAllText(Path.Combine(target, "Odyle.exe.old"), "older"); File.WriteAllText(Path.Combine(target, "THIRD-PARTY.md"), "old notices");
            File.WriteAllText(Path.Combine(source, "Odyle.exe"), "new"); File.WriteAllText(Path.Combine(source, "THIRD-PARTY.md"), "new notices");
            Updater.Apply(source, target);
            Check(File.ReadAllText(Path.Combine(target, "Odyle.exe")) == "new" && File.ReadAllText(Path.Combine(target, "Odyle.exe.old")) == "running" && File.ReadAllText(Path.Combine(target, "THIRD-PARTY.md")) == "new notices", "installing moves the executable aside and copies the new files in");
            // This checks executable is stamped 1.0.0.0, newer than the unstamped app build.
            Updater.Staging = Path.Combine(root, "updates");
            foreach (var (folder, stamped) in new[] { ("1.0.0.0", true), ("9.9.9.9", true), ("9.9.9.8", false), ("0.0.0.1", true), ("9.9.9.7.partial", true) })
            {
                var dir = Path.Combine(Updater.Staging, folder); Directory.CreateDirectory(dir);
                if (stamped) File.Copy(Environment.ProcessPath!, Path.Combine(dir, "Odyle.exe")); else File.WriteAllText(Path.Combine(dir, "Odyle.exe"), "not an executable");
            }
            var staged = Updater.Staged();
            Check(staged is { Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 } } && staged.Directory == Path.Combine(Updater.Staging, "1.0.0.0"), "a staged folder counts only with an executable stamped with its newer version");
            Updater.CleanUp();
            Check(Directory.GetDirectories(Updater.Staging).Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "1.0.0.0", "9.9.9.8", "9.9.9.9" }), "cleanup removes partial downloads and versions that are not newer");
        }
        finally { Updater.Staging = staging; Directory.Delete(root, true); }
    }
    // Opt-in and online: fetches the newest GitHub release and stages this machine's package
    // in a temporary folder, exercising download, checksum verification and extraction.
    // On a desktop (not in CI): the real window switched to compact mode and back stays where it
    // is and only changes size, and a shortcut is registered and released.
    static void CompactProbe(MainWindow window)
    {
        App.Headless = false;
        Call(window, "UseHotkey", "Ctrl+Alt+F12");
        var taken = (string)Field("hotkeyProblem").GetValue(window)!;
        static void Settle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.Show(); window.Left = 300; window.Top = 200; Settle();
        var width = window.ActualWidth; var height = window.ActualHeight;
        Call(window, "SetCompact", true, false); Settle();
        Check(window.IsVisible && window.Left == 300 && window.Top == 200 && window.ActualWidth < width && window.ActualHeight < height, "compact mode keeps the window where it is and only makes it smaller");
        Call(window, "SetCompact", false, false); Settle();
        Check(window.Left == 300 && window.Top == 200 && Math.Abs(window.ActualWidth - width) < 1, "the full card comes back in the same place");
        window.Hide(); Call(window, "UseHotkey", "");
        Console.WriteLine("Compact probe passed; the shortcut Ctrl+Alt+F12 " + (taken.Length == 0 ? "was registered and released." : "is taken here: " + taken));
    }
    static async Task UpdateProbe()
    {
        var root = Directory.CreateTempSubdirectory("odyle-update-probe-").FullName; Updater.Staging = root;
        try
        {
            var release = await Updater.FetchLatestAsync(CancellationToken.None) ?? throw new Exception("No release is published.");
            Console.WriteLine($"Latest release {release.Version} ({release.Url}); this build is {Updater.Current}, {Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")}.");
            var staged = await Updater.DownloadAsync(release, new Progress<double>(part => Console.Write($"\r{part:P0}")), CancellationToken.None);
            Console.WriteLine($"\nStaged {staged.Version} in {staged.Directory}: {string.Join(", ", Directory.GetFiles(staged.Directory).Select(Path.GetFileName))}");
        }
        finally { Directory.Delete(root, true); }
    }
    static void Screenshots(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = Settings(window);
        Control<TextBlock>(window, "StatusLabel").Text = "Pair the app in settings to start.";
        // Previews show a released build, which keeps itself current.
        ((TextBlock)settings.FindName("UpdateLabel")).Text = "Odyle 0.5.0.2 · installs new releases at every start and looks for them once a day.";
        ((Button)settings.FindName("CheckUpdateButton")).Visibility = Visibility.Visible;
        Snapshot(window, Path.Combine(directory, "connect-preview.png"));
        Snapshot(settings, Path.Combine(directory, "settings-connect-preview.png"), 320);
        Field("accountName").SetValue(window, "Sample account");
        Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090"));
        var stamp = DateTime.UtcNow.AddSeconds(-40).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, stamp), new Character("c2", "u", "TestAlt", "Lumiel", "GLOBAL_EU", 120, true, false, stamp) });
        Field("rooms").SetValue(window, new[] { new Room("r1", "The evening expedition", 40, "owner", true, ["c1"]), new Room("r2", "Weekend raid", 30, "reporter", true, ["c1", "c2"]) });
        Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { PartySnapshot() });
        ((TextBlock)settings.FindName("AccountDetail")).Text = "Signed in as Sample account via Discord · odyle.example";
        ((TextBlock)settings.FindName("UpdateLabel")).Text = "Odyle 0.5.0.2 · up to date · checked 20:14";
        Call(window, "ShowConnected", true); Call(window, "RenderRooms");
        Snapshot(settings, Path.Combine(directory, "settings-preview.png"), 320);
        var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
        Call(window, "RenderReaderButton");
        Field("detectedCharacterKey").SetValue(window, "TestHero|2304"); Field("gameCharacterKey").SetValue(window, "TestHero|2304");
        Call(window, "OnCandidate", inert, new PacketCandidate(390, ExperimentalOdyleDecoder.Version) { Base = 0, Bonus = 390 });
        Field("readingOwnerKey").SetValue(window, "TestHero|2304"); Field("activeCharacterId").SetValue(window, "c1");
        var state = typeof(MainWindow).GetNestedType("ReaderState", BindingFlags.NonPublic)!;
        Call(window, "SetReaderState", Enum.Parse(state, "Live"), "Live · 390 Odyle saved to your account.");
        Call(window, "RenderHero"); Call(window, "RenderParty");
        Control<TextBlock>(window, "CaptureStatsLabel").Text = "1,284 packets · 96 KB from the game";
        Control<TextBlock>(window, "StatusLabel").Text = "TestHero · Lumiel saved to your account.";
        Snapshot(window, Path.Combine(directory, "compact-preview.png"));
        // The same moment in compact mode.
        Call(window, "SetCompact", true, false);
        Snapshot(window, Path.Combine(directory, "compact-mode-preview.png"), (int)CompactStrip.Width);
        Call(window, "SetCompact", false, false);
        // What's new, from the changelog built into the app.
        var news = new WhatsNewWindow(Changelog.Parse(WhatsNewWindow.Embedded()), $"{Updater.Current.Major}.{Updater.Current.Minor}.{Updater.Current.Build}");
        Snapshot(news, Path.Combine(directory, "whats-new-preview.png"), 380); news.Close();
        // Pinned, with the checklist beside the card; ticks are dated within the current periods.
        Call(window, "SetStayOnTop", true, false); Call(window, "SetTasksShown", true, false);
        var now = DateTimeOffset.UtcNow; var dayStart = Checklist.PeriodStart("daily", now); var weekStart = Checklist.PeriodStart("weekly", now);
        string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
        Field("tasks").SetValue(window, new[] { new UserTask("t1", "u", "Daily dungeon run", "daily", Stamp(Later(dayStart, now.AddHours(-2)))), new UserTask("t2", "u", "Odyle bounty", "daily", Stamp(Later(dayStart, now.AddMinutes(-40)))), new UserTask("t3", "u", "Transformation contract", "daily"),
            new UserTask("t4", "u", "Weekly raid", "weekly", Stamp(Later(weekStart, now.AddDays(-1)))), new UserTask("t5", "u", "Guild boss", "weekly"), new UserTask("t6", "u", "Legion quests", "weekly") });
        Field("tasksLoaded").SetValue(window, true); Call(window, "RenderTasks");
        Snapshot(window, Path.Combine(directory, "tasks-preview.png"), 520);
        Call(window, "SetTasksShown", false, false); Call(window, "SetStayOnTop", false, false);
        Field("latest").SetValue(window, null); Call(window, "RenderHero");
        Call(window, "SetReaderState", Enum.Parse(state, "Midstream"), "Started after login · the value appears at the next Odyle change; the game names your character within a few minutes, or at once after a relog or channel change.");
        Snapshot(window, Path.Combine(directory, "midstream-preview.png"));
        Call(window, "ShowAnnouncement", new Announcement("Server maintenance on Thursday 10:00–11:00 UTC. Readings pause meanwhile.", "warning"));
        Snapshot(window, Path.Combine(directory, "announcement-preview.png"));
        // The operators pause packet readers: the announcement moves into the card.
        Field("capture").SetValue(window, null); Field("readerPaused").SetValue(window, true);
        Call(window, "ShowAnnouncement", new Announcement("Readers are off while we check today's game patch.", "warning")); Call(window, "ShowPaused");
        Snapshot(window, Path.Combine(directory, "paused-preview.png"));
        Field("readerPaused").SetValue(window, false); Call(window, "ShowAnnouncement", new object[] { null! });
        // Npcap missing: the reader line links to its download; after the first live reading, the Start with Windows offer.
        var probe = Npcap.Probe; Npcap.Probe = () => false; Call(window, "ShowNpcapProblem", new object[] { null! }); Npcap.Probe = probe; Call(window, "RenderReaderButton");
        ((System.Windows.Threading.DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); Control<Border>(window, "AutostartOffer").Visibility = Visibility.Visible; Call(window, "RenderHero");
        Snapshot(window, Path.Combine(directory, "setup-preview.png"));
        Control<Border>(window, "AutostartOffer").Visibility = Visibility.Collapsed; Call(window, "RenderReaderButton");
        Console.WriteLine("Rendered previews to " + directory);
    }
    static void Snapshot(Window window, string path, int width = 280)
    {
        var root = (FrameworkElement)window.Content;
        // The window sizes its height to the content; measure it the same way.
        root.Width = width; root.Height = double.NaN;
        // Templates and expanders settle over a few passes; measure until the height is stable.
        for (double last = -1; Math.Abs(root.DesiredSize.Height - last) > 0.5; ) { last = root.DesiredSize.Height; root.Measure(new Size(width, double.PositiveInfinity)); root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height)); root.UpdateLayout(); }
        var height = (int)Math.Ceiling(Math.Min(root.DesiredSize.Height, window.MaxHeight));
        root.Height = height; root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    // Synthetic room: your own live character, two live/stale mates and one offline mate.
    static RoomSnapshot PartySnapshot()
    {
        string Ago(int seconds) => DateTime.UtcNow.AddSeconds(-seconds).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Character Hero(string id, string user, string name, int value, bool valid, int seen) => new(id, user, name, "Lumiel", "GLOBAL_EU", value, true, valid, Ago(600), Ago(seen), "packet");
        return new RoomSnapshot(new Room("r1", "Evening", 40, "", false), [
            new RoomMember("m1", "u", "owner", "Sample account", [Hero("c1", "u", "TestHero", 390, true, 2)]),
            new RoomMember("m2", "l", "reporter", "Lumina", [Hero("c3", "l", "Lumina", 180, true, 5) with { Level = 45, ClassName = "Chanter" }]),
            new RoomMember("m3", "k", "reporter", "Kaelen", [Hero("c4", "k", "KaelenAlt", 235, false, 10)]),
            new RoomMember("m4", "n", "reporter", "Nyx", [Hero("c5", "n", "Nyx", 400, true, 300)])], DateTime.UtcNow.ToString("O"));
    }
    // Answers like the backend for the offline checks: the character the game names, the account's
    // characters and rooms, and readings, refused for their adapter while Refusal is set. Paused is
    // the operators' reader pause: the rooms and heartbeat answers carry it, and readings answer 503.
    sealed class FakeServer : HttpMessageHandler
    {
        public int Reports;
        public string LastReport = "";
        public string LastNotifications = "";
        public string LastPlan = "";
        public string? Refusal = PartyApi.AdapterRefused;
        public bool? Paused;
        const string Saved = """{"id":"c9","user":"u","characterName":"Newcomer","server":"Lumiel","region":"GLOBAL_EU","value":0,"confirmed":false,"valid":false,"lastReadingAt":""}""";
        const string Read = """{"id":"c9","user":"u","characterName":"Newcomer","server":"Lumiel","region":"GLOBAL_EU","value":15,"confirmed":true,"valid":true,"lastReadingAt":"2026-10-15 12:00:00.000Z"}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/api/odyle/report") { Reports++; LastReport = request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult(); }
            if (route == "/api/odyle/notifications") LastNotifications = request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult();
            if (route == "/api/odyle/plan") LastPlan = request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult();
            var pause = Paused is { } paused ? $$""","readerPaused":{{(paused ? "true" : "false")}}""" : "";
            var (status, body) = route switch
            {
                "/api/odyle/character" => (HttpStatusCode.OK, $$"""{"character":{{Saved}}}"""),
                "/api/collections/characters/records" => (HttpStatusCode.OK, $$"""{"items":[{{Saved}}]}"""),
                "/api/odyle/rooms" => (HttpStatusCode.OK, $$"""{"rooms":[],"odylePerCube":40,"announcement":null{{pause}}}"""),
                "/api/odyle/heartbeat" => (HttpStatusCode.OK, $$"""{"serverNow":"2026-10-15 20:00:00.000Z"{{pause}}}"""),
                "/api/odyle/report" when Paused == true => (HttpStatusCode.ServiceUnavailable, System.Text.Json.JsonSerializer.Serialize(new { message = PartyApi.ReadersPaused, data = new { } })),
                "/api/odyle/report" when Refusal is { } message => (HttpStatusCode.BadRequest, System.Text.Json.JsonSerializer.Serialize(new { message, data = new { } })),
                "/api/odyle/report" => (HttpStatusCode.OK, $$"""{"character":{{Read}}}"""),
                "/api/odyle/notifications" => (HttpStatusCode.OK, "{\"notifications\":" + LastNotifications[..LastNotifications.LastIndexOf('}')] + ",\"saved\":true}}"),
                _ => (HttpStatusCode.OK, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    sealed class MemoryRunKey : IRunKey
    {
        public readonly Dictionary<string, string> Values = new();
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }
    static Window Settings(MainWindow window) => (Window)Field("settings").GetValue(window)!;
    static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static IEnumerable<DependencyObject> Tree(DependencyObject at) { yield return at; foreach (var child in LogicalTreeHelper.GetChildren(at)) if (child is DependencyObject next) foreach (var below in Tree(next)) yield return below; }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("UI check failed: " + message); }
    static async Task Until(Func<bool> condition, int attempts)
    {
        for (var i = 0; i < attempts && !condition(); i++) await Task.Delay(25);
        Check(condition(), "waited condition holds");
    }
}
