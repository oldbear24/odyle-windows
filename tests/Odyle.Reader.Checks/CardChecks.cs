using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;
using Odyle.Core;
using Odyle.Reader;

// The reading card: which character a reading belongs to, sending it, the operators' reader pause, the
// restart hand-over and what the tray and the card list of the account's characters.
static partial class ReaderChecks
{
    // The attribution state machine on its own (ReaderAttribution), without a window: a snapshot waits for the game's
    // name, a duplicate frame only moves the time on, a switch or a new name drops a reading the previous character
    // owns, and two game clients stop everything until one closes.
    static void AttributionStateScenario()
    {
        var delay = new DispatcherTimer { Interval = TimeSpan.FromHours(1) }; var resets = 0;
        var state = new ReaderAttribution(delay, () => resets++); var now = DateTimeOffset.UtcNow; var capture = new PacketCapture();
        var snapshot = new PacketCandidate(400, OdyleDecoder.Version, CandidateSource.Snapshot) { Base = 10, Bonus = 390 };
        Check(state.Candidate(snapshot, now) && state.Latest == snapshot && state.NameDue && state.OwnerKey == "" && delay.IsEnabled && state.PendingSince == now && state.ConnectionRead, "a snapshot is shown, waits for the game's name and starts the publish delay");
        state.GameKey = "Hero|2304"; state.NamedHasReading = true;
        state.AttributePending(now.AddSeconds(29));
        Check(state.OwnerKey == "", "within 30 seconds of a snapshot the reading waits for the name even when the named character had a reading");
        state.AttributePending(now.AddSeconds(31));
        Check(state.OwnerKey == "Hero|2304" && !state.NameDue, "after 30 seconds it goes to the named character whose Odyle was read");
        Check(!state.Candidate(new PacketCandidate(400, OdyleDecoder.Version, CandidateSource.Snapshot), now.AddSeconds(1)) && state.Latest == snapshot && state.LatestAt == now.AddSeconds(1), "the same frame from a second interface only moves the reading's time on");
        state.GameId = 7;
        Check(!state.Switched(7) && state.OwnerKey == "Hero|2304", "a second interface's report of the switch to the named character changes nothing");
        Check(state.Switched(8) && state.GameKey == ReaderAttribution.UnknownOwner && state.Latest is null && state.OwnerKey == "" && state.GameId == 0, "a switch drops the reading the previous character owned");
        var identity = new GameIdentity(8, "Alt", 2304, "Lumiel", "GLOBAL_EU"); var key = ReaderAttribution.KeyOf(identity);
        state.Candidate(new PacketCandidate(120, OdyleDecoder.Version, CandidateSource.Snapshot), now.AddSeconds(5));
        Check(key == "Alt|2304" && state.Name(key, "Alt") && !state.Name(key, "Alt") && state.GameName == "Alt" && state.Latest is { Total: 120 }, "naming another character keeps a reading nobody owns yet; naming it again changes nothing");
        Check(!state.NoteIdentity(capture, identity, key, now.AddSeconds(6)) && state.GameId == 8 && !state.NameDue && state.Named is { Identity.CharacterName: "Alt" }, "the identity is noted");
        state.Claim(key);
        Check(state.OwnerKey == key && !delay.IsEnabled && state.NamedHasReading, "the waiting reading belongs to the character just named");
        state.DetectedKey = key;
        Check(state.OwnerSaved && state.OwnerSettled && state.NamedSaved, "a named, saved owner may be shown, handed over and followed");
        // A snapshot without Odyle right after the name belongs to that character; later ones wait for the next name.
        state.Latest = null;
        Check(state.SnapshotWithoutOdyle(now.AddSeconds(10)) && state.OdyleMissingKey == key, "a snapshot without Odyle a moment after the name is that character's");
        state.OdyleMissingKey = "";
        Check(!state.SnapshotWithoutOdyle(now.AddSeconds(30)) && state.OdyleMissingDue, "a later one waits for the game to name its character");
        Check(state.NoteIdentity(capture, identity, key, now.AddSeconds(31)) && state.OdyleMissingKey == key && !state.OdyleMissingDue, "and the next name says that character has no Odyle yet");
        // A resync: the next reading, even an update, waits for the name.
        state.Resync(); delay.Stop();
        state.Candidate(new PacketCandidate(125, OdyleDecoder.Version, CandidateSource.Update), now.AddSeconds(40));
        Check(state.OwnerKey == "" && state.NameDue && !state.IdentityDue && !state.NamedHasReading && delay.IsEnabled, "after a resync the next update waits for the game's name");
        // A new game connection drops an update nobody owns; two game clients stop everything until one closes.
        Check(state.DropUnowned() && state.Latest is null && !delay.IsEnabled, "a new connection drops an update the game never attributed");
        var before = resets;
        Check(state.TwoClientsStarted() && !state.TwoClientsStarted() && state.GameKey == "" && resets == before + 1, "two game clients reset the attribution once");
        Check(state.OneClientLeft() && !state.OneClientLeft() && !state.TwoClients && resets == before + 2, "one closing starts over once");
        // A restart's hand-over is taken only while the game has named nothing since.
        var handed = new Handover("http://x", "u", "c1", "Hero|2304", "Hero", 390, OdyleDecoder.Version, CandidateSource.Update, now);
        state.NameDue = true;
        Check(!state.TakeOver(handed, now) && state.OwnerKey == "", "a hand-over is not taken after the game loaded a character");
        state.Reset();
        Check(state.TakeOver(handed, now) && state.OwnerKey == "Hero|2304" && state.DetectedKey == "Hero|2304" && state.Latest is { Total: 390 }, "otherwise the handed-over character and its value are taken over");
        delay.Stop();
    }
    static void AttributionScenario(MainWindow window)
    {
        // A decoded reading is displayed immediately, before its owner is known or saved.
        var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
        window.Link.Api = new PartyApi("http://127.0.0.1:8090"); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 0, false, false, "") }); Field("currentCharacterId").SetValue(window, "c1");
        Call(window, "OnCandidate", inert, new PacketCandidate(390, OdyleDecoder.Version) { Base = 0, Bonus = 390 });
        Check(Control<TextBlock>(window, "ValueLabel").Text == "390", "decoded reading is shown in the value card");
        Check(Control<TextBlock>(window, "CubeLabel").Text == "9" && Control<TextBlock>(window, "CubeWord").Text == "cubes" && Control<TextBlock>(window, "RefillCaption").Text == "30/40 to the next cube", "cube count derives from the reading");
        // The blue ring fills with what items added towards 2000; the inner one waits for the character (its cap) to be known.
        Check(Math.Abs(Control<Ring>(window, "BonusTrack").Value - 390 * 100.0 / 2000) < 0.001 && Control<Ring>(window, "EnergyTrack").Value == 0, "the blue ring shows what items added, the natural pool's waits for the character: " + Control<Ring>(window, "BonusTrack").Value + " / " + Control<Ring>(window, "EnergyTrack").Value);
        Check(Control<TextBlock>(window, "StateText").Text == "Sending", "unsent reading is marked as sending");
        Check(Shown(Control<TextBlock>(window, "HeroTitle")) == "Detecting character…", "an unattributed reading is never shown under a character's name");
        Check(Control<TextBlock>(window, "ReadingLabel").Text == "Waiting for the game to name the character.", "a snapshot waits for the name the game sends after the loading screen");
        // A snapshot the game leaves unnamed for 30 seconds came without a world load: it goes to the
        // named character only if that character's Odyle was already read this run.
        string Owner() => window.Attribution.OwnerKey;
        window.Attribution.GameKey = "TestHero|2304"; window.Attribution.NamedHasReading = true;
        window.Attribution.AttributePending(DateTimeOffset.UtcNow);
        Check(Owner() == "", "during the loading screen a snapshot waits for the name even when the named character had a reading");
        window.Attribution.PendingSince = DateTimeOffset.UtcNow.AddSeconds(-31); window.Attribution.NamedHasReading = false;
        window.Attribution.AttributePending(DateTimeOffset.UtcNow);
        Check(Owner() == "", "an unnamed snapshot never goes to a named character that had no Odyle reading");
        window.Attribution.NamedHasReading = true; window.Attribution.AttributePending(DateTimeOffset.UtcNow);
        Check(Owner() == "TestHero|2304", "after 30 seconds an unnamed snapshot goes to the named character whose Odyle was read");
        // A resync may have lost a switch (the new character's snapshot and identity): the next reading,
        // even a live update, waits for the game's name and never falls back to the character named before.
        window.Attribution.Resync();
        Call(window, "OnCandidate", inert, new PacketCandidate(200, OdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 200 });
        window.Attribution.PendingSince = DateTimeOffset.UtcNow.AddSeconds(-31); window.Attribution.AttributePending(DateTimeOffset.UtcNow);
        Check(Owner() == "" && !window.Attribution.NamedHasReading && Shown(Control<TextBlock>(window, "HeroTitle")) == "Detecting character…", "after a resync the next reading waits for the game's name instead of going to the character named before");
        Call(window, "OnCandidate", inert, new PacketCandidate(205, OdyleDecoder.Version, CandidateSource.Update) { Base = 5, Bonus = 200 });
        window.Attribution.AttributePending(DateTimeOffset.UtcNow); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
        Check(Owner() == "", "and so do the updates after it");
        // Two game clients on this PC would mix their characters' messages: nothing is read or named until one closes.
        window.Attribution.OwnerKey = "TestHero|2304"; window.Attribution.GameKey = "TestHero|2304";
        Call(window, "OnTwoClients");
        Call(window, "OnCandidate", inert, new PacketCandidate(999, OdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 999 });
        Call(window, "OnSnapshotWithoutOdyle", inert);
        Check(window.Attribution.Latest is null && Owner() == "" && window.Attribution.GameKey == "" && !window.Attribution.OdyleMissingDue, "with two game clients nothing is read, attributed or named");
        Check((string)Call(window, "ReaderStatus")! == "two-clients", "the heartbeat reports two game clients");
        window.Attribution.TwoClients = false;
        // The heartbeat's reader state: listening until this connection gave a reading, then reading.
        window.Attribution.ConnectionRead = false; Call(window, "SetReaderState", Enum.Parse(Field("readerState").FieldType, "Listening"), "Listening to AION2 · waiting for the Odyle snapshot.");
        var listening = (string)Call(window, "ReaderStatus")!;
        Call(window, "OnCandidate", inert, new PacketCandidate(390, OdyleDecoder.Version) { Base = 0, Bonus = 390 }); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
        Check(listening == "listening" && (string)Call(window, "ReaderStatus")! == "reading", "the heartbeat reports listening until Odyle is read on this connection, then reading");
        // A reader started after login (no snapshot this run) shows the next live update and says the game names the character soon.
        window.Attribution.Reset();
        Call(window, "OnCandidate", inert, new PacketCandidate(380, OdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 380 });
        Check(Control<TextBlock>(window, "ValueLabel").Text == "380" && Control<TextBlock>(window, "ReadingLabel").Text == "The game names the character within a few minutes, or at once after a relog or channel change.", "a live update read without a snapshot is shown and waits for the game's name");
        ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
    }
    static async Task PublishingScenario(MainWindow window)
    {
        // The state the card has after a reader started after login read a live update the game has not named yet.
        var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
        Call(window, "OnCandidate", inert, new PacketCandidate(380, OdyleDecoder.Version, CandidateSource.Update) { Base = 0, Bonus = 380 });
        ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
        // A snapshot without Odyle waits for the game's name like any snapshot; the named character then
        // says it has no Odyle yet instead of waiting for a snapshot forever. A reading still waiting for a
        // name belonged to the character the game left.
        var server = new FakeServer(); window.Link.Api = new PartyApi("http://127.0.0.1:8090", "token", null, server); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
        Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, "");
        Call(window, "OnSnapshotWithoutOdyle", inert);
        Check(window.Attribution.Latest is null, "an unnamed reading is dropped by a snapshot without Odyle");
        await (Task)Call(window, "SaveDetectedCharacterAsync", inert, new GameIdentity(50, "Newcomer", 2304, "Lumiel", "GLOBAL_EU"))!;
        Check(Shown(Control<TextBlock>(window, "HeroTitle")) == "Newcomer · Lumiel" && Control<TextBlock>(window, "ReadingLabel").Text == "No Odyle on Newcomer yet · it may unlock at level 22." && Control<TextBlock>(window, "PacketStateLabel").Text == "Listening to AION2", "a named character without Odyle says so, once");
        // Its first Odyle replaces the note. A server that refuses this build's adapter makes the card ask for an
        // update instead of repeating the server's message, and the update check runs at once.
        Call(window, "OnCandidate", inert, new PacketCandidate(15, OdyleDecoder.Version, CandidateSource.Update) { Base = 15, Bonus = 0 });
        window.Attribution.AttributePending(DateTimeOffset.UtcNow); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
        Check(window.Attribution.OdyleMissingKey == "", "a reading clears the note");
        await (Task)Call(window, "PublishLatestAsync")!;
        Check(server.Reports == 1 && Conn(window).AdapterOutdated && Control<TextBlock>(window, "StateText").Text == "Not saved" && Control<TextBlock>(window, "ReadingLabel").Text == "Odyle needs an update to keep reading · it looks for one every hour." && Control<TextBlock>(window, "PacketStateLabel").Text == "The server no longer accepts readings from this Odyle version.", "a refused adapter asks for an update");
        Check((DateTimeOffset)Field("publishBlockedUntil").GetValue(window)! > DateTimeOffset.UtcNow.AddMinutes(9) && DateTimeOffset.UtcNow - (DateTimeOffset)Field("outdatedCheckAt").GetValue(window)! < TimeSpan.FromMinutes(1), "the reading is tried again in ten minutes and the update check ran at once");
        window.Updates.Staged = new StagedUpdate(new Version(9, 9, 9, 9), Path.GetTempPath(), "https://github.com/oldbear24/odyle-windows/releases"); Call(window, "RenderHero");
        Check(Control<TextBlock>(window, "ReadingLabel").Text == "Odyle needs an update to keep reading · Restart to update installs it.", "a downloaded update is offered");
        window.Updates.Staged = null;
        server.Refusal = null; Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue); await (Task)Call(window, "PublishLatestAsync")!;
        Check(server.Reports == 2 && !Conn(window).AdapterOutdated && Control<TextBlock>(window, "StateText").Text == "Live" && Control<TextBlock>(window, "ReadingLabel").Visibility == Visibility.Collapsed, "once the server accepts the adapter again the card is live");
        // A live reading says no more than that: the value is on the card, so neither the reader line nor an age repeats it.
        Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Live" && Control<TextBlock>(window, "AgeLabel").Visibility == Visibility.Collapsed, "a live reading has a short reader line and no age: " + Control<TextBlock>(window, "PacketStateLabel").Text);
        // The first live reading offers to start with Windows (answered below, with Start with Windows).
        Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Visible, "the first live reading offers to start with Windows");
        // The reading carries the game's two numbers and what changed since the one saved before it.
        Check(server.LastReport.Contains("\"base\":15,\"bonus\":0,\"kind\":\"update\",\"delta\":null"), "the first reading of a live streak carries base and bonus and is an update");
        async Task Change(int total, int natural, int items)
        {
            Call(window, "OnCandidate", inert, new PacketCandidate(total, OdyleDecoder.Version, CandidateSource.Update) { Base = natural, Bonus = items });
            ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop(); await (Task)Call(window, "PublishLatestAsync")!;
        }
        await Change(0, 0, 0);
        Check(server.LastReport.Contains("\"base\":0,\"bonus\":0,\"kind\":\"spend\",\"delta\":-15") && Control<TextBlock>(window, "SplitLabel").Text.StartsWith("natural 0 · items 0 · −15 at ", StringComparison.Ordinal), "a spend is sent as one, and the card shows the split and the change");
        await Change(15, 15, 0);
        Check(server.LastReport.Contains("\"kind\":\"regen\",\"delta\":15") && Control<TextBlock>(window, "SplitLabel").Text.StartsWith("natural 15 · items 0 · +15 at ", StringComparison.Ordinal), "a rise of the natural pool alone is a regeneration tick");
        // The Odyle clock under the split: the next tick and when each cap the character may have is reached; the operators can hide it.
        var odyleClock = Control<TextBlock>(window, "OdyleClockLabel");
        Check(odyleClock.Visibility == Visibility.Collapsed && System.Text.RegularExpressions.Regex.IsMatch(odyleClock.Text, @"^full ≈ .+ at 560 / .+ at 840$") && Control<Border>(window, "NextTile").ToolTip is string clockTip && System.Text.RegularExpressions.Regex.IsMatch(clockTip, @"^\+15 in .+ · full ≈ .+ at 560 / .+ at 840$") && Control<TextBlock>(window, "NextLabel").Text == "NEXT +15" && System.Text.RegularExpressions.Regex.IsMatch(Control<TextBlock>(window, "RefillCountdown").Text, @"^\d+:\d\d(:\d\d)?$"), "the card shows the Odyle clock: " + Control<TextBlock>(window, "RefillCountdown").Text + " / " + odyleClock.Text);
        Field("clockRules").SetValue(window, ClockRules.Default with { Paused = true }); Call(window, "RenderOdyleClock");
        Check(odyleClock.Visibility == Visibility.Collapsed, "the operators can hide the Odyle clock");
        Field("clockRules").SetValue(window, null);
        // Near its cap the clock turns orange (11 ticks to 560: 30 to 33 hours), and red with the value once the pool is full.
        var shownReading = window.Attribution.Latest;
        window.Attribution.Latest = new PacketCandidate(400, OdyleDecoder.Version, CandidateSource.Update) { Base = 400, Bonus = 0 }; Call(window, "RenderOdyleClock");
        Check(odyleClock.Visibility == Visibility.Visible && odyleClock.Text.StartsWith("Nearly full · full ≈ ", StringComparison.Ordinal) && ReferenceEquals(Control<TextBlock>(window, "RefillCountdown").Foreground, window.FindResource("Orange")) && ReferenceEquals(odyleClock.Foreground, window.FindResource("Orange")), "two days before the cap the clock is orange: " + odyleClock.Text);
        // The taskbar button fills with the natural pool towards its cap, in the card's colours.
        Check(window.TaskbarItemInfo is { ProgressState: TaskbarItemProgressState.Paused } bar && Math.Abs(bar.ProgressValue - 400 / 560.0) < 0.001 && bar.Description.Contains(" · Nearly full · "), "two days before the cap the taskbar button is yellow: " + window.TaskbarItemInfo?.ProgressState + " " + window.TaskbarItemInfo?.ProgressValue + " " + window.TaskbarItemInfo?.Description);
        // So does the card's ring (400 is ten whole cubes: the ring is not empty for it), in orange.
        Check(Control<Ring>(window, "EnergyTrack") is { } nearRing && Math.Abs(nearRing.Value - 400 * 100.0 / 560) < 0.001 && ReferenceEquals(nearRing.Fill, window.FindResource("Orange")), "the ring fills with the natural pool towards its cap, in orange near it: " + Control<Ring>(window, "EnergyTrack").Value);
        window.Attribution.Latest = new PacketCandidate(560, OdyleDecoder.Version, CandidateSource.Update) { Base = 560, Bonus = 0 }; Call(window, "RenderOdyleClock");
        Check(odyleClock.Text.StartsWith("natural pool at 560: full if its cap is 560", StringComparison.Ordinal) && ReferenceEquals(odyleClock.Foreground, window.FindResource("Red")) && ReferenceEquals(Control<TextBlock>(window, "ValueLabel").Foreground, window.FindResource("Red")), "a full pool is red: " + odyleClock.Text);
        Check(window.TaskbarItemInfo is { ProgressState: TaskbarItemProgressState.Error, ProgressValue: 1 }, "a full pool fills the taskbar button in red");
        Check(Control<Ring>(window, "EnergyTrack") is { Value: 100 } fullRing && ReferenceEquals(fullRing.Fill, window.FindResource("Red")), "a full pool fills the ring in red: " + Control<Ring>(window, "EnergyTrack").Value);
        window.Attribution.Latest = shownReading; Call(window, "RenderOdyleClock");
        Check(ReferenceEquals(odyleClock.Foreground, window.FindResource("Muted")) && ReferenceEquals(Control<TextBlock>(window, "RefillCountdown").Foreground, window.FindResource("Mint")) && ReferenceEquals(Control<TextBlock>(window, "ValueLabel").Foreground, window.FindResource("Text")), "far from its cap the clock is mint again");
        Check(window.TaskbarItemInfo is { ProgressState: TaskbarItemProgressState.Normal }, "far from its cap the taskbar button is green again");
        // The Discord status (off by default): while the reader has the game, the card's character with its Odyle.
        Check(Settings(window).FindName("DiscordToggle") is CheckBox { IsChecked: false } && Call(window, "PresenceNow") is null, "the Discord status is off unless chosen");
        Call(window, "SetDiscordStatus", true, false); Field("gamePresent").SetValue(window, true);
        Check(Call(window, "PresenceNow") is DiscordActivity { Details: var details } && details.EndsWith(" · " + Control<TextBlock>(window, "ValueLabel").Text + " Odyle", StringComparison.Ordinal) && ((TextBlock)Settings(window).FindName("DiscordNoteLabel")).Visibility == Visibility.Visible, "chosen, Discord shows the character and its Odyle while the game is read");
        Field("gamePresent").SetValue(window, false);
        Check(Call(window, "PresenceNow") is null, "without the game Discord shows nothing");
        Call(window, "SetDiscordStatus", false, false);
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
        server.Paused = false; Npcap.Probe = () => false; window.Link.SessionId = "s"; window.Link.NeedsReconnect = false;
        window.Link.TokenRenewedAt = DateTimeOffset.UtcNow; window.Link.LastTick = DateTimeOffset.UtcNow;
        await (Task)Call(window, "PulseAsync")!; await Task.Delay(100);
        var npcapTimer = (DispatcherTimer)Field("npcapTimer").GetValue(window)!;
        Check(!(bool)Field("readerPaused").GetValue(window)! && Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap is not installed · Odyle needs it to read the game." && Control<Button>(window, "NpcapLink").Visibility == Visibility.Visible && npcapTimer.IsEnabled, "once the pause is over the reader starts again by itself");
        Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "StateText").Text != "Paused" && Control<Button>(window, "ReportButton").IsEnabled, "the announcement returns above the card");
        npcapTimer.Stop(); Npcap.Probe = Npcap.Installed; Call(window, "ShowAnnouncement", new object[] { null! }); Field("readerWanted").SetValue(window, false);
    }
    static async Task HandoverScenario(MainWindow window)
    {
        Field("capture").SetValue(window, new PacketCapture()); window.Link.Api = new PartyApi("http://127.0.0.1:8090"); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
        Field("accountId").SetValue(window, "u"); Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "") }); Field("currentCharacterId").SetValue(window, "c1");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "") });
        window.Attribution.GameKey = window.Attribution.DetectedKey = window.Attribution.OwnerKey = "TestHero|2304";
        window.Attribution.GameName = "TestHero"; window.Attribution.Latest = new PacketCandidate(390, OdyleDecoder.Version) { Base = 300, Bonus = 90 };
        Check(Call(window, "LiveHandover") is null, "a reading not sent yet is not handed over");
        Field("activeCharacterId").SetValue(window, "c1");
        var handover = (Handover?)Call(window, "LiveHandover");
        Check(handover is { Endpoint: "http://127.0.0.1:8090", AccountId: "u", CharacterId: "c1", CharacterKey: "TestHero|2304", Total: 390, ParserVersion: OdyleDecoder.Version, Base: 300, Bonus: 90 }, "a live reading is handed to the restart, with the game's two numbers");
        // Its party and force go along when the rosters that said them listed the entity the game names: the game sends
        // the next roster only when the group changes.
        var groups = (GroupTracker)Field("groups").GetValue(window)!; window.Attribution.GameId = 5069;
        groups.Observe(new(GroupChange.Party, "ba0b0600b2dff723", 7, 606)); groups.Observe(new(GroupChange.Force, "591a0000b2dff723", 7, 606));
        Check(((Handover?)Call(window, "LiveHandover")) is { Party: "", Force: "", GroupOwner: 0 }, "rosters of another entity than the one the game names stay behind");
        window.Attribution.GameId = 606; handover = (Handover?)Call(window, "LiveHandover");
        Check(handover is { Party: "ba0b0600b2dff723", Force: "591a0000b2dff723", GroupOwner: 606, GroupMember: 7 }, "the party and force go along with the character");
        HandoverStore.Save(handover!); await (Task)Call(window, "StopCaptureAsync", false)!;
        Check(groups is { Party: "", Force: "", Owner: 0 }, "stopping the reader forgets them here");
        // The restarted build: connected again, the reader running, nothing named by the game yet.
        Field("capture").SetValue(window, new PacketCapture()); Field("currentCharacterId").SetValue(window, "");
        await (Task)Call(window, "TakeOverAsync")!;
        Check(!File.Exists(HandoverStore.FilePath) && window.Attribution.OwnerKey == "TestHero|2304" && (string)Field("currentCharacterId").GetValue(window)! == "c1" && window.Attribution.Latest is PacketCandidate { Total: 390 }, "the next start takes the character over, once");
        Check(groups is { Party: "ba0b0600b2dff723", Force: "591a0000b2dff723", Owner: 606, Member: 7 }, "and its party and force with it, until a roster says otherwise");
        Check(window.Attribution.GameId == 0 && ((Handover?)Call(window, "LiveHandover")) is { Party: "ba0b0600b2dff723", Force: "591a0000b2dff723", GroupOwner: 606 }, "a restart before the game names the character hands them on again");
        Check(Shown(Control<TextBlock>(window, "HeroTitle")) == "TestHero · Lumiel" && Control<TextBlock>(window, "ValueLabel").Text == "390", "the taken-over character is shown with its value without a relog");
        // The rings and the Odyle clock show at once: the hand-over carries the game's two numbers.
        Check(window.Attribution.Latest is { Base: 300, Bonus: 90 } && Math.Abs(Control<Ring>(window, "EnergyTrack").Value - 300 * 100.0 / 560) < 0.001 && Math.Abs(Control<Ring>(window, "BonusTrack").Value - 90 * 100.0 / 2000) < 0.001 && Control<TextBlock>(window, "RefillCountdown").Text != "—",
            "after a restart the rings and the clock show the taken-over reading: " + Control<Ring>(window, "EnergyTrack").Value + " / " + Control<Ring>(window, "BonusTrack").Value + " / " + Control<TextBlock>(window, "RefillCountdown").Text);
        // A hand-over from a build before 1.2.1 carries the total only: the saved reading's two numbers stand in when they add up to it.
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "", Parts: new OdyleParts(300, 90)) });
        await (Task)Call(window, "StopCaptureAsync", false)!; HandoverStore.Save(handover! with { Base = null, Bonus = null, SavedAt = DateTimeOffset.UtcNow });
        Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
        Check(window.Attribution.Latest is { Total: 390, Base: 300, Bonus: 90 } && Math.Abs(Control<Ring>(window, "BonusTrack").Value - 90 * 100.0 / 2000) < 0.001, "an older build's hand-over takes the saved reading's two numbers");
        await (Task)Call(window, "StopCaptureAsync", false)!; HandoverStore.Save(handover! with { Total = 395, Base = null, Bonus = null, SavedAt = DateTimeOffset.UtcNow });
        Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
        Check(window.Attribution.Latest is { Total: 395, Base: null, Bonus: null }, "but not when they add up to another total (nor does a server that cannot be reached say them)");
        // Else the latest change in the character's history says them: a reading sent again without them cleared the saved ones.
        var latest = """{"changes":[{"value":395,"at":"2026-11-27 16:00:00.000Z","adapter":"global-reference-v2","kind":"regen","delta":15,"parts":{"base":305,"bonus":90},"change":15}],"page":1,"perPage":1,"total":9}""";
        var asked = new List<string>();
        window.Link.Api = new PartyApi("http://127.0.0.1:8090", "token", null, new Answering(request =>
        {
            asked.Add(request.RequestUri!.PathAndQuery);
            var body = request.RequestUri.AbsolutePath == "/api/odyle/history/c1/changes" ? latest : "{}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }));
        await (Task)Call(window, "StopCaptureAsync", false)!; HandoverStore.Save(handover! with { Total = 395, Base = null, Bonus = null, SavedAt = DateTimeOffset.UtcNow });
        Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
        Check(window.Attribution.Latest is { Total: 395, Base: 305, Bonus: 90 } && asked.Contains("/api/odyle/history/c1/changes?perPage=1") && Control<TextBlock>(window, "RefillCountdown").Text != "—",
            "else the latest change in the history gives them, and the clock shows: " + string.Join(", ", asked));
        latest = latest.Replace("\"value\":395", "\"value\":380");
        await (Task)Call(window, "StopCaptureAsync", false)!; HandoverStore.Save(handover! with { Total = 395, Base = null, Bonus = null, SavedAt = DateTimeOffset.UtcNow });
        Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
        Check(window.Attribution.Latest is { Total: 395, Base: null, Bonus: null }, "but not a change that left another value");
        window.Link.Api = new PartyApi("http://127.0.0.1:8090");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "") });
        HandoverStore.Save(handover! with { SavedAt = DateTimeOffset.UtcNow - HandoverStore.Lifetime - TimeSpan.FromSeconds(1) });
        Check(HandoverStore.Take(DateTimeOffset.UtcNow) is null && !File.Exists(HandoverStore.FilePath), "an old hand-over is dropped");
        HandoverStore.Save(handover! with { AccountId = "someone-else" }); await (Task)Call(window, "StopCaptureAsync", false)!;
        Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
        Check(!File.Exists(HandoverStore.FilePath) && window.Attribution.OwnerKey == "" && window.Attribution.Latest is null, "another account's hand-over is ignored");
        // The reader starts before the server is asked (#86): a snapshot it read meanwhile came with a world load,
        // so it waits for the game's name and the character is not taken over.
        await (Task)Call(window, "StopCaptureAsync", false)!; HandoverStore.Save(handover!);
        var loading = new PacketCapture(); Field("capture").SetValue(window, loading);
        Call(window, "OnCandidate", loading, new PacketCandidate(400, OdyleDecoder.Version, CandidateSource.Snapshot) { Base = 10, Bonus = 390 });
        await (Task)Call(window, "TakeOverAsync")!; ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
        Check(!File.Exists(HandoverStore.FilePath) && window.Attribution.OwnerKey == "" && window.Attribution.Latest is PacketCandidate { Total: 400 }, "a snapshot read before the connection waits for the game's name instead of the hand-over");
        // A hand-over from the build before v2 (with its stat owner fields) is taken over and its reading sent
        // again under the adapter that read it. A server that no longer takes that adapter drops the reading
        // without asking for an update, and this build reads the next Odyle change.
        await (Task)Call(window, "StopCaptureAsync", false)!;
        File.WriteAllText(HandoverStore.FilePath, $$"""{"Endpoint":"http://127.0.0.1:8090","AccountId":"u","CharacterId":"c1","CharacterKey":"TestHero|2304","CharacterName":"TestHero","Total":390,"EntityId":51591,"OwnerConfirmed":true,"ParserVersion":"global-reference-v1","Source":0,"SavedAt":"{{DateTimeOffset.UtcNow:O}}"}""");
        var refusing = new FakeServer(); window.Link.Api = new PartyApi("http://127.0.0.1:8090", "token", null, refusing); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
        Field("capture").SetValue(window, new PacketCapture()); await (Task)Call(window, "TakeOverAsync")!;
        Check(!File.Exists(HandoverStore.FilePath) && refusing.Reports == 1 && window.Attribution.OwnerKey == "TestHero|2304" && (string)Field("currentCharacterId").GetValue(window)! == "c1" && window.Attribution.Latest is null && !Conn(window).AdapterOutdated, "a refused reading of the previous adapter is dropped without asking for an update");
        Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Updated · the value is read again at the next Odyle change.", "the reader line says the value is read again");
        window.Link.Api = new PartyApi("http://127.0.0.1:8090"); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
        await (Task)Call(window, "StopCaptureAsync", false)!;
        window.Link.Api = null; Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue); Field("activeCharacterId").SetValue(window, "");
        Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("accountId").SetValue(window, ""); Call(window, "RenderHero");
    }
    static void TrayAndCharactersScenario(MainWindow window)
    {
        // An update restart hands the live character to the next start, which takes it over without a relog.
        Field("capture").SetValue(window, new PacketCapture()); window.Link.Api = new PartyApi("http://127.0.0.1:8090"); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
        Field("accountId").SetValue(window, "u"); Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "") }); Field("currentCharacterId").SetValue(window, "c1");
        // The tray menu (W#17): the reader's state line, Stop while it reads, and the characters, the
        // fullest first, each with its value and cubes; nameless records are left out. (WinForms
        // items are read through reflection: the checks reference WPF only.)
        string ItemText(object item) => (string)item.GetType().GetProperty("Text")!.GetValue(item)!;
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "2026-10-17T10:00:00Z"), new Character("c2", "u", "Altie", "Lumiel", "GLOBAL_EU", 1090, true, true, "2026-10-17T09:00:00Z"), new Character("c3", "u", "", "Lumiel", "GLOBAL_EU", 0, false, false, "") });
        Call(window, "TrayMenuOpening");
        var trayRows = ((System.Collections.IEnumerable)TrayItem(window, "Characters").GetType().GetProperty("DropDownItems")!.GetValue(TrayItem(window, "Characters"))!).Cast<object>().Select(ItemText).ToArray();
        Check(ItemText(TrayItem(window, "Reader")) == "Stop reader" && ItemText(TrayItem(window, "Status")) == Control<TextBlock>(window, "PacketStateLabel").Text, "the tray menu says what the reader does and offers to stop it");
        Check(trayRows.Length == 2 && trayRows[0].StartsWith("Altie · ", StringComparison.Ordinal) && trayRows[0].Contains(" Odyle · ") && trayRows[1].StartsWith("TestHero · ", StringComparison.Ordinal), "the tray lists the characters, the fullest first: " + string.Join(" | ", trayRows));
        // The card's "Your characters" (W#17): closed until asked, then the tray's rows; it opens by
        // itself while the game switches characters and closes once the game names the next one.
        var altToggle = Control<Button>(window, "AltToggle"); var alts = Control<ItemsControl>(window, "AltList");
        Call(window, "RenderHero");
        Check(altToggle.Visibility == Visibility.Visible && alts.Visibility == Visibility.Collapsed && altToggle.Content as string == "Your characters (2) ▾", "with two characters the card offers them, closed: " + altToggle.Content);
        altToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var altRows = (alts.ItemsSource?.Cast<object>() ?? []).Select(r => (string)r.GetType().GetProperty("Name")!.GetValue(r)! + " · " + (string)r.GetType().GetProperty("Detail")!.GetValue(r)!).ToArray();
        Check(alts.Visibility == Visibility.Visible && altRows.Length == 2 && altRows[0] == trayRows[0] && altRows[1] == trayRows[1], "opened, it lists them as the tray does: " + string.Join(" | ", altRows));
        var altSource = alts.ItemsSource; Call(window, "RenderHero");
        Check(ReferenceEquals(altSource, alts.ItemsSource), "rendering the card again keeps the characters' rows");
        // Hidden in the tray the card does not tick (#84); showing it renders it at once.
        Control<TextBlock>(window, "CaptureStatsLabel").Text = "hidden"; Call(window, "Tick");
        Check(Control<TextBlock>(window, "CaptureStatsLabel").Text == "hidden", "a hidden window's card does not tick");
        altToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var namedKey = window.Attribution.GameKey!;
        window.Attribution.GameKey = "?"; Call(window, "RenderHero");
        Check(alts.Visibility == Visibility.Visible && altToggle.Content as string == "Your characters (2) ▴", "while the game switches characters the list opens by itself");
        window.Attribution.GameKey = "Altie|2304"; Call(window, "RenderHero");
        Check(alts.Visibility == Visibility.Collapsed, "and closes once the game names the next character");
        window.Attribution.GameKey = namedKey;
        // The tray icon carries the reader's state: a dot while it reads, none when stopped, each drawn once.
        var readerStates = typeof(MainWindow).GetNestedType("ReaderState", BindingFlags.NonPublic)!;
        var stateBefore = Field("readerState").GetValue(window)!; var lineBefore = Control<TextBlock>(window, "PacketStateLabel").Text;
        object TrayIconNow() => window.TrayIcon.CurrentIcon;
        Call(window, "SetReaderState", Enum.Parse(readerStates, "Live"), "Live"); var liveIcon = TrayIconNow();
        Call(window, "SetReaderState", Enum.Parse(readerStates, "Stale"), "Stale"); var staleIcon = TrayIconNow();
        Call(window, "SetReaderState", Enum.Parse(readerStates, "Stopped"), "Stopped"); var stoppedIcon = TrayIconNow();
        Call(window, "SetReaderState", Enum.Parse(readerStates, "Live"), "Live again");
        Check(!ReferenceEquals(liveIcon, stoppedIcon) && !ReferenceEquals(liveIcon, staleIcon) && !ReferenceEquals(staleIcon, stoppedIcon) && ReferenceEquals(TrayIconNow(), liveIcon), "the tray icon shows whether the reader reads, waits or is stopped");
        Call(window, "SetReaderState", stateBefore, lineBefore);
    }
}
