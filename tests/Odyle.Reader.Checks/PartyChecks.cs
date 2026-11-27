using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Odyle.Core;
using Odyle.Reader;

// The party: the list of the account's rooms, the server's announcement and news, planned runs and their
// reminders, the account's notification settings, the run chip and the places of location sharing.
static partial class ReaderChecks
{
    // The tray reminders on their own (TrayReminders), without a window: open tasks an hour before the daily reset are
    // told once, also across restarts (preferences.json), and quiet hours mark them without a notification.
    static void ResetRemindersScenario()
    {
        var saved = new Preferences(); var told = new List<(string Title, string Text)>();
        var reminders = new TrayReminders(() => saved, next => saved = next, (title, text) => told.Add((title, text)));
        // The weekly reset falls three days after the next daily one, so neither that daily reset nor the one a day
        // later (in quiet hours below) comes an hour before a weekly reset too: on the day before the default
        // Wednesday reset the weekly task was reminded as well, and the check failed for a day each week.
        var due = Checklist.NextReset("daily", DateTimeOffset.UtcNow, new TaskResets());
        var resets = new TaskResets(WeeklyResetDay: Checklist.Days[(int)due.AddDays(3).DayOfWeek]); var now = Checklist.NextReset("daily", DateTimeOffset.UtcNow, resets).AddHours(-1);
        UserTask[] tasks = [new("t1", "u", "Daily dungeon", "daily"), new("t2", "u", "Weekly raid", "weekly")];
        reminders.Resets(tasks, resets, now);
        Check(told is [{ Title: var title, Text: "Still open: Daily dungeon" }] && title.StartsWith("Daily reset in ", StringComparison.Ordinal) && title.EndsWith(" · 1 task open", StringComparison.Ordinal) && saved.RemindedResets is [_], "an open daily task is told an hour before the reset: " + string.Join(" | ", told));
        reminders.Resets(tasks, resets, now.AddMinutes(10));
        Check(told.Count == 1, "and only once");
        var tomorrow = now.AddDays(1); var hhmm = (DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm");
        reminders.Settings = NotificationSettings.Default with { Quiet = new(hhmm(tomorrow.AddMinutes(-30)), hhmm(tomorrow.AddMinutes(30)), "UTC") };
        reminders.Resets(tasks, resets, tomorrow);
        Check(told.Count == 1 && saved.RemindedResets is [_, _], "in quiet hours the next reset is marked without a notification");
    }
    static void PartyListScenario(MainWindow window)
    {
        window.Link.Api = new PartyApi("http://127.0.0.1:8090"); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
        // The party list shows other members' live and stale characters, never your own or offline ones.
        Field("accountId").SetValue(window, "u"); Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", true) });
        Field("snapshots").SetValue(window, new[] { PartySnapshot() }); Call(window, "RenderParty");
        var party = (PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource;
        Check(Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Visible && party.Length == 1 && party[0].Name == "Evening", "a connected account sees the party of its rooms");
        Check(party[0].Mates.Select(m => m.Name).SequenceEqual(new[] { "Lumina", "KaelenAlt" }), "only others' online characters are listed, live first");
        Check(party[0].Mates[0].Value == "180" && party[0].Mates[0].Cubes == "4 cubes" && party[0].Mates[1].Cubes == "5 cubes" && System.Text.RegularExpressions.Regex.IsMatch(party[0].Mates[1].Detail, @"^stale · seen \d+s ago · Kaelen · Lumiel$"), "each mate shows Odyle and cubes, and a stale one says when it was last seen: " + party[0].Mates[1].Detail);
        Check(party[0].Mates[0] is { Detail: "Chanter · Lv 45 · Lumiel · EU", Hint: "Chanter · Lv 45 · Lumina · live from the game" } , "a mate PLAYNC listed leads with its class and level: " + party[0].Mates[0].Detail);
        Check(party[0].Shown.SequenceEqual(party[0].Mates), "outside a band the list shows the cards alone");
        // A render that shows the same rows keeps the list's items, so a tooltip open on one stays open (#84).
        Call(window, "RenderParty");
        Check(ReferenceEquals(party, Control<ItemsControl>(window, "PartyList").ItemsSource), "rendering the same party again keeps its rows");
        // The players the game put together (GameBands): your party framed inside your force's frame first,
        // with the force's other player, then another party's frame, each labelled with its players.
        Field("snapshots").SetValue(window, new[] { PartySnapshot(inGame: true) }); Call(window, "RenderParty");
        var together = ((PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource)[0];
        Check(together.Mates.Select(m => m.Name).SequenceEqual(["Lumina", "Rin", "Tor", "Vex", "KaelenAlt"]) && together.Mates[0].Hint.EndsWith("live from the game\nIn your party in game\nIn your force in game", StringComparison.Ordinal) && together.Mates[2].Hint.EndsWith("game\nIn one party in game", StringComparison.Ordinal),
            "the party list puts the players of one party or force together, yours first, and the tooltips say so: " + string.Join(", ", together.Mates.Select(m => m.Name)));
        Check(together.Mates[0].Tag == "party" && together.Mates[1].Tag == "" && together.Mates[2].Tag == "", "your party's players are tagged; your force's others are not, its frame says it: " + string.Join(", ", together.Mates.Select(m => m.Name + "=" + m.Tag)));
        Check(together.Shown is [MateBand { Label: "In your force in game", Count: "3 players", Items: [MateBand { Label: "In your party in game", Count: "2 players", Items: [PartyMate { Name: "Lumina" }] }, PartyMate { Name: "Rin" }] },
            MateBand { Label: "In one party in game", Count: "2 players", Items: [PartyMate { Name: "Tor" }, PartyMate { Name: "Vex" }] }, PartyMate { Name: "KaelenAlt" }],
            "a party's frame sits in its force's, each labelled with how many players it holds, you included");
        Field("capture").SetValue(window, null); window.Link.Api = null; Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
        Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); window.Attribution.Latest = null;
        Field("accountId").SetValue(window, ""); Field("rooms").SetValue(window, Array.Empty<Room>()); Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>()); Call(window, "RenderParty");
        Check(Control<StackPanel>(window, "PartyPanel").Visibility == Visibility.Collapsed, "no party list without a connection");
    }
    static async Task AnnouncementsScenario(MainWindow window)
    {
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
        Check(Field("hiddenAnnouncement").GetValue(window) is "Maintenance at 10:00 UTC" && PreferencesStore.Load().HiddenAnnouncement == "Maintenance at 10:00 UTC", "hiding is remembered while the app runs, and after a restart");
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
        Check(Control<Border>(window, "AnnouncementBar").Visibility == Visibility.Collapsed && !window.Updates.Checking, "a cleared announcement disappears at once; an older release starts no update check");
    }
    static void RunRemindersScenario(MainWindow window)
    {
        // Planned runs: one reminder 15 minutes before a run the account joined, once per start time.
        string RunAt(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        var soon = DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(-20);
        RoomSnapshot Planned(string startsAt) => new(new Room("r1", "Evening", 40, "owner", true), [], DateTime.UtcNow.ToString("O"), [new PlannedRun("run1", "Fire Temple", startsAt, "Bring potions", "u", ["u", "l"]), new PlannedRun("run2", "Ice Cave", RunAt(soon), "", "l", ["l"])]);
        (string Title, string Text)? Notice() => ((string Title, string Text)?)typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void ClearNotice() => typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
        Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon)) }); Call(window, "CheckReminders");
        Check(Notice() is { Title: "Fire Temple starts in 10 minutes", Text: var text } && text.StartsWith("Evening · starts at ", StringComparison.Ordinal) && text.EndsWith("\nBring potions", StringComparison.Ordinal), "a joined run is announced with its dungeon, room, local time and note");
        ClearNotice(); Call(window, "CheckReminders");
        Check(Notice() is null && PreferencesStore.Load().RemindedRuns is [var reminded] && reminded.StartsWith("run1|", StringComparison.Ordinal), "a run is announced once, also across restarts, and runs others joined are not");
        Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(3))) }); Call(window, "CheckReminders");
        Check(Notice() is { Title: "Fire Temple starts in 13 minutes" }, "a run moved to another time is announced again");
        // The account's notification settings (the dashboard's Account & data): no tray reminders, quiet hours.
        ClearNotice(); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(1))) });
        window.Reminders.Settings = NotificationSettings.Default with { Channels = new(Tray: false) }; Call(window, "CheckReminders");
        Check(Notice() is null, "reminders can be switched off for the tray on the dashboard");
        string Hhmm(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm");
        window.Reminders.Settings = NotificationSettings.Default with { Quiet = new(Hhmm(DateTimeOffset.UtcNow.AddMinutes(-30)), Hhmm(DateTimeOffset.UtcNow.AddMinutes(30)), "UTC") }; Call(window, "CheckReminders");
        Check(Notice() is null && PreferencesStore.Load().RemindedRuns!.Contains(Reminders.Key(new PlannedRun("run1", "Fire Temple", RunAt(soon.AddMinutes(1)), "", "u", []), 15)), "quiet hours show no reminder, and it does not come later");
        ClearNotice(); Field("snapshots").SetValue(window, new[] { Planned(RunAt(soon.AddMinutes(2))) }); window.Reminders.Settings = NotificationSettings.Default with { LeadMinutes = [5] }; Call(window, "CheckReminders");
        Check(Notice() is null, "a run beyond the account's reminder times waits");
    }
    static async Task NotificationSettingsScenario(MainWindow window)
    {
        var settings = Settings(window);
        (string Title, string Text)? Notice() => ((string Title, string Text)?)typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void ClearNotice() => typeof(MainWindow).GetProperty("LastNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
        // The settings move to the dashboard: Settings links there, and an old local "off" moves to the account once.
        Check(settings.FindName("RemindersToggle") is null && settings.FindName("NotificationsButton") is Button, "settings link to the dashboard's notification settings");
        var moving = new FakeServer(); window.Link.Api = new PartyApi("http://127.0.0.1:8090", "token", null, moving);
        Field("preferences").SetValue(window, PreferencesStore.Load() with { RemindRuns = false, RemindRunsMoved = false });
        await window.Reminders.FollowAsync(NotificationSettings.Default, window.Link.Api);
        Check(moving.LastNotifications.Contains("\"tray\":false") && window.Reminders.Settings is NotificationSettings { RunsInTray: false, Saved: true } && PreferencesStore.Load().RemindRunsMoved, "an old local switch-off moves to the account");
        moving.LastNotifications = ""; await window.Reminders.FollowAsync(NotificationSettings.Default, window.Link.Api);
        Check(moving.LastNotifications == "" && window.Reminders.Settings is NotificationSettings { RunsInTray: true }, "once: from then on the account's settings count");
        await window.Reminders.FollowAsync(null, window.Link.Api);
        Check(window.Reminders.Settings is NotificationSettings { RunsInTray: false }, "a backend without them keeps the local choice");
        // Cap alerts from the server show in the tray when the account wants them there.
        var capNews = RealtimeNews.Parse(System.Text.Json.JsonDocument.Parse("""{"capAlerts":[{"characterId":"c","characterName":"Lumina","state":"full","base":840,"cap":840,"settled":true,"tick":15}]}""").RootElement.Clone());
        ClearNotice(); window.Reminders.Settings = NotificationSettings.Default; await (Task)Call(window, "OnNewsAsync", "odyle.user.u", capNews)!;
        Check(Notice() is null, "cap alerts stay off until the account turns them on");
        window.Reminders.Settings = NotificationSettings.Default with { Kinds = new(Cap: true) }; await (Task)Call(window, "OnNewsAsync", "odyle.user.u", capNews)!;
        Check(Notice() is { Title: "Lumina is losing Odyle", Text: "Its natural pool is full: each tick of 15 is lost until you spend some." }, "a cap alert shows in the tray");
    }
    static async Task RunChipAndPlacesScenario(MainWindow window)
    {
        string RunAt(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        // The party line of each room, the next run with Going, and a run of yours that was cancelled.
        var seenNow = Stamp(DateTimeOffset.UtcNow);
        Character Member(string id, string user, string name, int value) => new(id, user, name, "Lumiel", "GLOBAL_EU", value, true, true, Stamp(DateTimeOffset.UtcNow.AddHours(-1)), seenNow, "packet", Shared: true);
        RoomSnapshot Room(params PlannedRun[] runs) => new(new Room("r1", "Evening", 40, "owner", true), [new RoomMember("m1", "u", "owner", "You", [Member("c1", "u", "Ayla", 130)], true), new RoomMember("m2", "k", "reporter", "Kaelen", [Member("c2", "k", "Kael", 85)], true), new RoomMember("m3", "n", "reporter", "Nyx", [], false)], DateTime.UtcNow.ToString("O"), runs);
        var planned = new PlannedRun("run9", "Fire Temple", RunAt(DateTimeOffset.UtcNow.AddMinutes(50)), "", "u", ["u", "k"]);
        var planServer = new FakeServer(); window.Link.Api = new PartyApi("http://127.0.0.1:8090", "token", null, planServer); Field("accountId").SetValue(window, "u");
        Field("snapshots").SetValue(window, new[] { Room(planned) }); Call(window, "RenderParty");
        var partyRooms = (PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource;
        Check(partyRooms is [{ Summary: "Party · 2 cubes · lowest Kaelen · 1 not live", Mates.Length: 1 }], "a room's party line counts your character with your mates': " + partyRooms.FirstOrDefault()?.Summary);
        Check(Control<Border>(window, "RunChipBox").Visibility == Visibility.Visible && System.Text.RegularExpressions.Regex.IsMatch(Control<TextBlock>(window, "RunChipLabel").Text, @"^Fire Temple · in (49|50)m · 2 going · Evening$") && (string)Control<Button>(window, "RunGoingButton").Content == "Going ✓", "the next run you go to, with its countdown: " + Control<TextBlock>(window, "RunChipLabel").Text);
        Call(window, "RunGoing_Click", Control<Button>(window, "RunGoingButton"), new RoutedEventArgs()); await Task.Delay(300);
        Check(planServer.LastPlan.Contains("\"roomId\":\"r1\"") && planServer.LastPlan.Contains("\"id\":\"run9\"") && planServer.LastPlan.Contains("\"going\":false"), "Going ✓ leaves the run: " + planServer.LastPlan);
        Field("snapshots").SetValue(window, new[] { Room() }); Call(window, "RenderRunChip");
        Check(Control<TextBlock>(window, "RunChipNotice") is { Visibility: Visibility.Visible, Text: var cancelled } && cancelled.StartsWith("Fire Temple at ", StringComparison.Ordinal) && cancelled.EndsWith(" was cancelled", StringComparison.Ordinal) && Control<TextBlock>(window, "RunChipLabel").Visibility == Visibility.Collapsed, "a cancelled run of yours says so");
        Field("chipNoticeUntil").SetValue(window, DateTimeOffset.MinValue); Call(window, "RenderRunChip");
        Check(Control<Border>(window, "RunChipBox").Visibility == Visibility.Collapsed, "no run, no chip");
        // A character none of your rooms can see says so on the card, with a link to the dashboard.
        Field("characters").SetValue(window, new[] { Member("c1", "u", "Ayla", 130) }); Field("currentCharacterId").SetValue(window, "c1");
        Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", false, []) }); Call(window, "RenderHero");
        Check(Control<Button>(window, "ShareHintButton").Visibility == Visibility.Visible, "a character no room sees says so");
        Field("rooms").SetValue(window, new[] { new Room("r1", "Evening", 40, "owner", true, ["c1"]) }); Call(window, "RenderHero");
        Check(Control<Button>(window, "ShareHintButton").Visibility == Visibility.Collapsed, "a shared character does not");
        // Location sharing: the card's character and your mates show where they were last seen, by the map's
        // name (its id until superusers name it) and how long ago; without a place nothing changes.
        Check(Control<TextBlock>(window, "PlaceLabel").Visibility == Visibility.Collapsed, "no place, no line");
        string Placed() => ((System.Windows.Documents.Run)window.FindName("PlaceText")).Text;
        Field("maps").SetValue(window, new[] { new GameMap(1110, "Altgard") });
        Field("characters").SetValue(window, new[] { Member("c1", "u", "Ayla", 130) with { Location = new Place(1110, 1, 2, 3, 90, "load", Stamp(DateTimeOffset.UtcNow.AddSeconds(-250))) } }); Call(window, "RenderHero");
        Check(Control<TextBlock>(window, "PlaceLabel").Visibility == Visibility.Visible && Placed() == "Altgard · 4m ago" && Control<TextBlock>(window, "PlaceLabel").ToolTip as string == "Last place the game reported · loading screen", "the card shows where its character was last seen: " + Placed());
        Call(window, "ShowOwnPlace", new LocationReport("c1", 2220, 4, 5, 6, null, GameLocation.Arrival));
        Check(System.Text.RegularExpressions.Regex.IsMatch(Placed(), @"^Map 2220 · \ds ago$") && Control<TextBlock>(window, "PlaceLabel").ToolTip as string == "Last place the game reported · arrived", "a place the server stored shows at once, an unnamed map by its id: " + Placed());
        Field("snapshots").SetValue(window, new[] { new RoomSnapshot(new Room("r1", "Evening", 40, "owner", true), [new RoomMember("m2", "k", "reporter", "Kaelen", [Member("c2", "k", "Kael", 85) with { Level = 45, ClassName = "Chanter", Location = new Place(1110, 1, 2, 3, null, "flight", Stamp(DateTimeOffset.UtcNow.AddSeconds(-70))) }], true)], DateTime.UtcNow.ToString("O")) });
        Call(window, "RenderParty");
        Check(((PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource) is [{ Mates: [{ Detail: "Chanter · Lv 45", PlaceGap: " · ", Pin: "\uE707 ", Place: "Altgard · 1m ago", Hint: "Chanter · Lv 45 · Kaelen · live from the game" }] }], "a mate's place takes the server's: " + ((PartyRoom[])Control<ItemsControl>(window, "PartyList").ItemsSource)[0].Mates[0]);
        // Live updates go unmentioned; a party fetched every 15 seconds says so.
        Check(Control<TextBlock>(window, "PartyModeLabel") is { Visibility: Visibility.Visible, Text: "refreshed every 15 s" }, "a party without live updates says it is refreshed every 15 seconds");
        Field("maps").SetValue(window, Array.Empty<GameMap>());
    }
}
