using Odyle.Core;

// Start with Windows, the start in the tray and where the window reopens.
static class DesktopChecks
{
    public static void Run()
    {
        const string exe = @"C:\Users\Player\AppData\Local\Programs\Odyle\Odyle.exe";
        Check(StartupCommand.For(exe) == "\"" + exe + "\" --tray" && StartupCommand.ValueName == "Odyle", "the Run entry starts this executable in the tray");
        Check(StartupCommand.PathOf(StartupCommand.For(exe)) == exe && StartupCommand.PathOf(@"C:\Odyle\Odyle.exe --tray") == @"C:\Odyle\Odyle.exe" && StartupCommand.PathOf("\"broken") is null && StartupCommand.PathOf(" ") is null, "the executable of a Run entry");
        // --build-info <file>: the publish script asks a package what it is, and releases only one that updates itself.
        Check(StartupCommand.BuildInfoPath(["--build-info", "build.json"]) == "build.json" && StartupCommand.BuildInfoPath(["--build-info"]) is null && StartupCommand.BuildInfoPath(["--tray"]) is null, "the build-info argument names its file");
        Check(StartupCommand.BuildInfo(new Version(0, 8, 1, 0), true, false, "win-x64") == """{"version":"0.8.1","release":true,"selfContained":false,"runtime":"win-x64"}""", "a build says its version, whether it updates itself and its package kind");
        Check(StartupCommand.InTray(["--TRAY"]) && !StartupCommand.InTray([]) && !StartupCommand.InTray(["--update-probe"]), "the tray argument");
        Check(StartupCommand.ShowsWindow(tray: false, paired: true) && StartupCommand.ShowsWindow(tray: true, paired: false) && !StartupCommand.ShowsWindow(tray: true, paired: true), "a paired start with Windows stays in the tray");
        // Two screens side by side; the right one is shorter.
        ScreenArea[] screens = [new(0, 0, 1920, 1040), new(1920, 0, 3200, 760)];
        Check(Placement.OnScreen(100, 100, screens) && Placement.OnScreen(2000, 700, screens) && Placement.OnScreen(-30, 0, screens), "a position whose caption is on a screen is restored");
        Check(!Placement.OnScreen(2000, 900, screens) && !Placement.OnScreen(5000, 100, screens) && !Placement.OnScreen(-2000, 100, screens) && !Placement.OnScreen(100, -100, screens), "a position on a disconnected screen is not");
        Check(Placement.Clamp(1800, 900, 280, 300, screens[0]) == (1640, 740) && Placement.Clamp(100, 100, 280, 300, screens[0]) == (100, 100) && Placement.Clamp(-30, -5, 280, 300, screens[0]) == (0, 0) && Placement.Clamp(10, 10, 2500, 1200, screens[0]) == (0, 0), "a window is kept on its screen");
        // Compact mode: the other members' characters online in your rooms, each once, live first; and its shortcuts.
        var moment = DateTimeOffset.Parse("2026-10-16T12:00:00Z"); string Ago(int seconds) => moment.AddSeconds(-seconds).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Character Seen(string id, string user, string name, int value, int seconds, bool? shared = null) => new(id, user, name, "Lumiel", "GLOBAL_EU", value, true, true, Ago(600), Ago(seconds), "packet", Shared: shared);
        RoomSnapshot Room(string id, int cost, params RoomMember[] members) => new(new Room(id, "Room " + id, cost, "reporter", true), members, Ago(0));
        RoomMember Member(string user, string name, params Character[] characters) => new(user + "-m", user, "reporter", name, characters);
        var lumina = Seen("l1", "l", "Lumina", 180, 5);
        var rooms = new[] { Room("r1", 40, Member("u", "Me", Seen("u1", "u", "Hero", 300, 5)), Member("l", "Lu", lumina), Member("k", "Kaelen", Seen("k1", "k", "Kael", 235, 60), Seen("k2", "k", "KaelAlt", 500, 200), Seen("k3", "k", "Hidden", 900, 5, shared: false))),
            Room("r2", 0, Member("l", "Lu", lumina), Member("n", "Nyx", Seen("n1", "n", "Nyx", 90, 10))) };
        var (mates, more) = CompactStrip.Mates(rooms, "u", moment, 30);
        Check(mates.Select(m => (m.Name, m.Value, m.Cubes, m.Live, m.Player)).SequenceEqual([("Lumina", 180, 4, true, "Lu"), ("Nyx", 90, 3, true, "Nyx"), ("Kael", 235, 5, false, "Kaelen")]) && more == 0,
            "compact mode lists the others' live and stale characters once, live first, with their room's cubes; not yours, offline or unshared ones");
        Check(CompactStrip.Mates(rooms, "u", moment, 30, limit: 2) is ({ Length: 2 }, 1) && CompactStrip.Mates([], "u", moment, 40).Mates.Length == 0, "beyond the limit the rest are counted");
        Check(CompactStrip.Parse("Ctrl+Alt+O") == (0x4003u, 'O') && CompactStrip.Parse("Ctrl+Shift+O") == (0x4006u, 'O') && CompactStrip.Parse("Ctrl+Alt+F12") == (0x4003u, 0x7Bu) && CompactStrip.Parse("") is null && CompactStrip.Parse("Ctrl+Alt+Delete") is null && CompactStrip.Parse("Alt+F4") is null,
            "the shortcuts map to RegisterHotKey, never repeating, and only offered ones");
        Check(CompactStrip.Hotkeys[0] == CompactStrip.DefaultHotkey && CompactStrip.Hotkeys.All(h => h.Length == 0 || CompactStrip.Parse(h) is not null), "every offered shortcut can be registered");
        // Planned runs as the room snapshot lists them; the account "u" goes to two of them.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RoomSnapshot>("""{"room":{"id":"r","name":"Evening","cost":40},"members":[],"wishlist":[],"dungeons":[],"serverNow":"2026-10-14T18:40:00.000Z","runs":[{"id":"a","dungeon":"Fire Temple","startsAt":"2026-10-14 19:00:00.000Z","note":"Bring potions","planner":"u","attendees":["u","l"]},{"id":"b","dungeon":"Ice Cave","startsAt":"2026-10-14 18:50:00.000Z","note":"","planner":"l","attendees":["l"]},{"id":"c","dungeon":"Sky Tower","startsAt":"2026-10-14 18:55:00.000Z","note":"","planner":"","attendees":["u"]}]}""", options)!;
        Check(snapshot.Runs is [{ Id: "a", Dungeon: "Fire Temple", Note: "Bring potions", Planner: "u", Attendees: ["u", "l"] }, _, _], "runs are read from the room snapshot");
        var at = (string stamp) => Readiness.Parse(stamp)!.Value; var none = new HashSet<string>();
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T18:40:00Z"), none).Select(d => d.Run.Id).SequenceEqual(["c"]), "only joined runs within 15 minutes are due");
        var due = Reminders.Due([snapshot], "u", at("2026-10-14T18:46:00Z"), none);
        Check(due.Select(d => d.Run.Id).SequenceEqual(["c", "a"]) && due[1] is { Room.Name: "Evening", StartsAt.Hour: 19, Key: "a|2026-10-14 19:00:00.000Z|15", Lead: 15 }, "due runs come soonest first with their room");
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T18:46:00Z"), new HashSet<string>(due.Select(d => d.Key))).Length == 0, "a run is reminded once");
        var moved = snapshot with { Runs = [snapshot.Runs![0] with { StartsAt = "2026-10-14 18:58:00.000Z" }] };
        Check(Reminders.Due([moved], "u", at("2026-10-14T18:46:00Z"), new HashSet<string>(due.Select(d => d.Key))).Single().Run.Id == "a", "a re-timed run reminds again");
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T19:00:00Z"), none).Length == 0 && Reminders.Due([snapshot], "", at("2026-10-14T18:50:00Z"), none).Length == 0 && Reminders.Due([snapshot with { Runs = null }], "u", at("2026-10-14T18:50:00Z"), none).Length == 0, "started runs, no account or no runs remind nothing");
        Check(Reminders.Prune(["a|2026-10-14 19:00:00.000Z|15", "old|2026-10-12 19:00:00.000Z|15", "a|2026-10-14 19:00:00.000Z|15", "b|2026-10-14 19:00:00.000Z"], at("2026-10-14T20:00:00Z")).SequenceEqual(["a|2026-10-14 19:00:00.000Z|15", "b|2026-10-14 19:00:00.000Z"]), "reminders of old runs are forgotten");
        // The account's reminder times: a run within 2 hours and 15 minutes when first seen is
        // reminded once, at 15; a run 1 hour away at 2 hours, then again at 15 minutes.
        var times = new[] { 120, 15 };
        var early = Reminders.Due([snapshot], "u", at("2026-10-14T18:00:00Z"), none, times);
        Check(early.Select(r => (r.Run.Id, r.Lead)).SequenceEqual([("c", 120), ("a", 120)]) && early[1].Marks!.SequenceEqual(["a|2026-10-14 19:00:00.000Z|120"]), "a run within a larger reminder time is due at it");
        var marked = new HashSet<string>(early.SelectMany(r => r.Marks!));
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T18:10:00Z"), marked, times).Length == 0, "each reminder time reminds once");
        var late = Reminders.Due([snapshot], "u", at("2026-10-14T18:50:00Z"), none, times);
        Check(late.Single(r => r.Run.Id == "a") is { Lead: 15, Marks.Length: 2 }, "a run first seen within 15 minutes is reminded once, at the smallest time");
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T18:50:00Z"), new HashSet<string>(["a|2026-10-14 19:00:00.000Z"]), times).All(r => r.Run.Id != "a"), "a run reminded by an older app is not reminded again");
        // Quiet hours in the account's time zone, over midnight.
        var quiet = NotificationSettings.Default with { Quiet = new("22:00", "07:00", "Europe/Prague") };
        Check(!quiet.QuietAt(at("2026-10-15T19:59:00Z")) && quiet.QuietAt(at("2026-10-15T20:00:00Z")) && quiet.QuietAt(at("2026-10-16T04:59:00Z")) && !quiet.QuietAt(at("2026-10-16T05:00:00Z")), "quiet hours wrap midnight in their time zone");
        Check(!NotificationSettings.Default.QuietAt(at("2026-10-15T20:00:00Z")) && !(quiet with { Quiet = new("22:00", "07:00", "Mars/Olympus") }).QuietAt(at("2026-10-15T20:00:00Z")), "no quiet hours, or an unknown zone, keep reminders on");
        var listed = System.Text.Json.JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[],"notifications":{"channels":{"tray":false,"browser":true,"push":true},"leadMinutes":[120,15],"kinds":{"runs":true,"cap":false},"quiet":{"from":"","to":"","timeZone":""},"saved":true}}""", options)!;
        Check(listed.Notifications is { Saved: true, RunsInTray: false } n && n.Leads.SequenceEqual([120, 15]), "the rooms answer carries the account's notification settings");
        Check(System.Text.Json.JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[]}""", options)!.Notifications is null && NotificationSettings.Default is { RunsInTray: true } && NotificationSettings.Default.Leads.SequenceEqual([15]), "older backends send none, and the defaults remind 15 minutes ahead");
        Console.WriteLine("Start with Windows command, tray start, saved window position, keep-on-screen, compact mode and planned-run reminders at the account's times and quiet hours passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Desktop check failed: " + message); }
}
