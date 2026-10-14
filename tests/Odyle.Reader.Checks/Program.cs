using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
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
        // The pairing, stat owners and the log go to temporary files too.
        var scratch = Directory.CreateTempSubdirectory("odyle-check-").FullName;
        SessionStore.FilePath = Path.Combine(scratch, "session.bin"); OwnerStore.FilePath = Path.Combine(scratch, "stat-owners.json"); Log.UseFolder(Path.Combine(scratch, "logs"));
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
                var settings = Settings(window);
                Check(window.FindName("CharactersInput") is null && window.FindName("CharacterInput") is null, "the main window offers no way to pick or type a character");
                Check(settings.FindName("CodeInput") is PasswordBox && window.FindName("CodeInput") is null, "pairing lives in the settings window");
                Check(settings.FindName("SharingList") is ItemsControl && settings.FindName("InviteInput") is null && settings.FindName("RoomsList") is null, "rooms and sharing are managed on the dashboard; settings only summarizes them");
                Check(Control<Button>(window, "ReportButton").Content as string == "Connect", "an unpaired reader points to settings");
                Check(Control<TextBlock>(window, "ValueLabel").Text == "—", "no value is shown before a reading");
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
                await (Task)Call(window, "RestoreAsync")!;
                Check(Field("api").GetValue(window) is null && File.Exists(SessionStore.FilePath) && ((Button)settings.FindName("RetryButton")).Visibility == Visibility.Visible, "an unreachable server keeps the pairing and offers Reconnect saved account");
                Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 15 s") && ((DispatcherTimer)Field("restoreTimer").GetValue(window)!).IsEnabled, "the first automatic retry comes after 15 seconds and is shown");
                await (Task)Call(window, "RestoreAsync")!;
                Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 30 s"), "the next retry waits longer");
                await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
                Check(!((DispatcherTimer)Field("restoreTimer").GetValue(window)!).IsEnabled && Field("retryAt").GetValue(window) is null && !File.Exists(SessionStore.FilePath), "disconnecting stops the retries and forgets the pairing");
                Check(File.ReadAllText(Log.FilePath).Contains("next in 15 s") && File.ReadAllText(Log.FilePath).Contains("Status: ") && !File.ReadAllText(Log.FilePath).Contains("device-key"), "the log records retries and status lines but never the device key");
                // A decoded reading is displayed immediately, before its owner is known or saved.
                var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 0, false, false, "") }); Field("currentCharacterId").SetValue(window, "c1");
                Call(window, "OnCandidate", inert, new PacketCandidate(390, 7, ExperimentalOdyleDecoder.Version) { Primary = 0, Bonus = 390 });
                Check(Control<TextBlock>(window, "ValueLabel").Text == "390", "decoded reading is shown in the value card");
                Check(Control<TextBlock>(window, "CubeLabel").Text.StartsWith("9 cubes"), "cube count derives from the reading");
                Check(Control<TextBlock>(window, "StateText").Text == "Sending", "unsent reading is marked as sending");
                Check(Control<TextBlock>(window, "HeroTitle").Text == "Detecting character…", "an unattributed reading is never shown under a character's name");
                // A reader started after login shows the next live update and asks for one relog so the game names the character.
                Call(window, "OnCandidate", inert, new PacketCandidate(380, 7, ExperimentalOdyleDecoder.Version, CandidateSource.Update) { OwnerConfirmed = false });
                Check(Control<TextBlock>(window, "ValueLabel").Text == "380" && Control<TextBlock>(window, "ReadingLabel").Text.StartsWith("Relog"), "a live update read without a snapshot is shown and asks for one relog");
                // The party list shows other members' live and stale characters, never your own or offline ones.
                Field("accountId").SetValue(window, "u"); Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", true) });
                Field("snapshots").SetValue(window, new[] { PartySnapshot() }); Call(window, "RenderParty");
                var party = (PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource;
                Check(Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Visible && party.Length == 1 && party[0].Name == "Evening", "a connected account sees the party of its rooms");
                Check(party[0].Mates.Select(m => m.Name).SequenceEqual(new[] { "Lumina", "KaelenAlt" }), "only others' online characters are listed, live first");
                Check(party[0].Mates[0].Value == "180" && party[0].Mates[0].Cubes == "4 cubes" && party[0].Mates[1].Cubes.EndsWith("stale"), "each mate shows Odyle, cubes and staleness");
                Field("capture").SetValue(window, null); Field("api").SetValue(window, null); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
                Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("latest").SetValue(window, null);
                Field("accountId").SetValue(window, ""); Field("rooms").SetValue(window, Array.Empty<Room>()); Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>()); Call(window, "RenderParty");
                Check(Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Collapsed, "no party list without a connection");
                // Window preferences: pinned above other windows and the checklist beside the card, remembered on this PC.
                Check(!window.Topmost && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && !File.Exists(PreferencesStore.FilePath), "the window starts unpinned and compact");
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
                var shell = (FrameworkElement)window.Content; shell.Width = 520; shell.Measure(new Size(520, double.PositiveInfinity)); shell.Arrange(new Rect(0, 0, 520, shell.DesiredSize.Height)); shell.UpdateLayout();
                Check(Control<Grid>(window, "Card").ActualWidth == 280 && Control<Border>(window, "TasksPanel").ActualWidth == 240, "the card keeps its width and the checklist takes its own column");
                shell.Width = double.NaN;
                Call(window, "SetTasksShown", false, true); Call(window, "SetStayOnTop", false, true);
                Check(!window.Topmost && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && PreferencesStore.Load() is { StayOnTop: false, ShowTasks: false }, "unpinning and hiding the checklist are remembered too");
                // Language: automatic by default; a choice is remembered and applies after a restart.
                var language = (ComboBox)settings.FindName("LanguageInput");
                Check((string)language.SelectedValue == Localizer.Automatic && language.Items.Count == 3, "the language follows Windows until one is chosen");
                language.SelectedValue = "cs";
                Check(PreferencesStore.Load().Language == "cs" && ((FrameworkElement)settings.FindName("LanguageRestartPanel")).Visibility == Visibility.Visible && Localizer.Language == "en", "choosing Czech is remembered and offers a restart");
                language.SelectedValue = Localizer.Automatic; Check(PreferencesStore.Load().Language == Localizer.Automatic, "automatic can be chosen again");
                // Start with Windows: "<Odyle.exe>" --tray in the Run key, removed again when switched off.
                Call(window, "SetAutostart", true);
                Check(runKey.Values.Single() is { Key: "Odyle", Value: "\"C:\\Odyle\\Odyle.exe\" --tray" } && ((CheckBox)settings.FindName("AutostartToggle")).IsChecked == true && Autostart.Enabled, "Start with Windows writes the Run entry for a start in the tray");
                Call(window, "SetAutostart", false);
                Check(runKey.Values.Count == 0 && ((CheckBox)settings.FindName("AutostartToggle")).IsChecked == false, "switching it off removes the entry");
                runKey.Values["Odyle"] = "\"C:\\Moved away\\Odyle.exe\" --tray"; Autostart.Repair();
                Check(runKey.Values["Odyle"] == "\"C:\\Odyle\\Odyle.exe\" --tray", "an entry for an executable that is gone starts this one");
                runKey.Values["Odyle"] = StartupCommand.For(Environment.ProcessPath!); Autostart.Repair();
                Check(runKey.Values["Odyle"] == StartupCommand.For(Environment.ProcessPath!), "an entry for another existing Odyle is left alone"); runKey.Values.Clear();
                // Npcap is checked whenever settings open.
                Npcap.Probe = () => false; Call(window, "RenderNpcap");
                Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap not found · install it to read the game" && ((Button)settings.FindName("NpcapButton")).Visibility == Visibility.Visible, "a missing Npcap is pointed out with its download");
                Npcap.Probe = () => true; Call(window, "RenderNpcap"); Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap installed", "an installed Npcap is confirmed");
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
                ClearNotice(); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(1))) }); ((CheckBox)settings.FindName("RemindersToggle")).IsChecked = false;
                Field("preferences").SetValue(window, PreferencesStore.Load() with { RemindRuns = false }); Call(window, "CheckReminders");
                Check(Notice() is null, "reminders can be switched off");
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
                    await (Task)Call(window, "SaveDetectedCharacterAsync", capture, detected)!;
                    Check((await api.GetCharactersAsync()).Length == 1, "repeated identity is deduplicated");
                    // A snapshot is held until the game confirms its owner, then saved to that character.
                    Call(window, "OnCandidate", capture, new PacketCandidate(390, 7, "test-fixture-v1") { Primary = 0, Bonus = 390 });
                    await Until(() => (int)Field("publishedTotal").GetValue(window)! == 390, 400);
                    var published = (await api.GetCharactersAsync()).Single(c => c.Id == automatic.Id);
                    Check(published.Confirmed && published.Valid && published.Value == 390, "decoded reading is saved to the detected character");
                    // Odyle is sent only on first read and on change; the heartbeat keeps it live.
                    var sent = (long)Field("sequence").GetValue(window)!;
                    await (Task)Call(window, "PublishLatestAsync")!;
                    Call(window, "OnCandidate", capture, new PacketCandidate(390, 7, "test-fixture-v1", CandidateSource.Update) { Primary = 0, Bonus = 390 });
                    await Task.Delay(300);
                    Check((long)Field("sequence").GetValue(window)! == sent, "an unchanged value is not sent again");
                    Call(window, "OnCandidate", capture, new PacketCandidate(395, 7, "test-fixture-v1", CandidateSource.Update) { Primary = 5, Bonus = 390 });
                    await Until(() => (int)Field("publishedTotal").GetValue(window)! == 395, 200);
                    Check((long)Field("sequence").GetValue(window)! == sent + 1, "a changed value is sent once");
                    // In-game switch on a fresh connection: the new character's snapshot arrives before its identity.
                    Call(window, "OnCandidate", capture, new PacketCandidate(120, 8, "test-fixture-v1") { Primary = 0, Bonus = 120 });
                    await Task.Delay(300);
                    Check((long)Field("sequence").GetValue(window)! == sent + 1, "a snapshot is not sent before the game names its character");
                    await (Task)Call(window, "SaveDetectedCharacterAsync", capture, new GameIdentity(44, "SwitchedHero", 2304, "Lumiel", "GLOBAL_EU"))!;
                    var afterSwitch = await api.GetCharactersAsync();
                    Check(afterSwitch.Single(c => c.Id == automatic.Id) is { Value: 395, Valid: false }, "a switch never moves the new character's Odyle onto the previous one");
                    var switched = afterSwitch.Single(c => c.CharacterName == "SwitchedHero");
                    Check(switched is { Value: 120, Valid: true, Confirmed: true }, "the new character receives its own snapshot");
                    Check((string)Field("currentCharacterId").GetValue(window)! == switched.Id, "the card switches with the game");
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
                    Check(((RoomSnapshot[])Field("snapshots").GetValue(window)!).Single().Members.Single().Characters.Length == 2, "the room snapshot is read from the server");
                    Check(((PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource).Length == 0 && Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Visible, "your own characters are not listed as party members");
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
                    Check((await api.GetCharactersAsync()).Length == 2, "unmapped server creates no character");
                    Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Value: 120, Valid: false }, "leaving for an unsupported character ends the previous reading's freshness");
                    await (Task)Call(window, "StopReaderAsync")!;
                    Check((await api.GetCharactersAsync()).Single(c => c.Id == switched.Id) is { Confirmed: true, Value: 120 }, "stopping the reader keeps the value");
                    Console.WriteLine("Windows UI game-driven characters, change-only sending, in-game switch attribution, dashboard-managed room sharing, party list and persistence passed.");
                }
                Console.WriteLine("Windows UI compact window, separate settings, reading card, party list and narrow layout passed.");
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
        ((TextBlock)settings.FindName("UpdateLabel")).Text = "Odyle 0.5.0.2 · looks for new releases at every start and once a day and installs them on restart.";
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
        Call(window, "OnCandidate", inert, new PacketCandidate(390, 7, ExperimentalOdyleDecoder.Version) { Primary = 0, Bonus = 390 });
        Field("readingOwnerKey").SetValue(window, "TestHero|2304"); Field("activeCharacterId").SetValue(window, "c1");
        var state = typeof(MainWindow).GetNestedType("ReaderState", BindingFlags.NonPublic)!;
        Call(window, "SetReaderState", Enum.Parse(state, "Live"), "Live · 390 Odyle saved to your account.");
        Call(window, "RenderHero"); Call(window, "RenderParty");
        Control<TextBlock>(window, "CaptureStatsLabel").Text = "1,284 packets · 96 KB from the game";
        Control<TextBlock>(window, "StatusLabel").Text = "TestHero · Lumiel saved to your account.";
        Snapshot(window, Path.Combine(directory, "compact-preview.png"));
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
        Call(window, "SetReaderState", Enum.Parse(state, "Midstream"), "Started after login · the value appears at the next Odyle change; relog or change channel once so the game names your character.");
        Snapshot(window, Path.Combine(directory, "midstream-preview.png"));
        Field("capture").SetValue(window, null);
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
            new RoomMember("m2", "l", "reporter", "Lumina", [Hero("c3", "l", "Lumina", 180, true, 5)]),
            new RoomMember("m3", "k", "reporter", "Kaelen", [Hero("c4", "k", "KaelenAlt", 235, false, 10)]),
            new RoomMember("m4", "n", "reporter", "Nyx", [Hero("c5", "n", "Nyx", 400, true, 300)])], DateTime.UtcNow.ToString("O"));
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
    static void Check(bool condition, string message) { if (!condition) throw new Exception("UI check failed: " + message); }
    static async Task Until(Func<bool> condition, int attempts)
    {
        for (var i = 0; i < attempts && !condition(); i++) await Task.Delay(25);
        Check(condition(), "waited condition holds");
    }
}
