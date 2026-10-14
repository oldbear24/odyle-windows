using Odyle.Core;

// Start with Windows, the start in the tray and where the window reopens.
static class DesktopChecks
{
    public static void Run()
    {
        const string exe = @"C:\Users\Player\AppData\Local\Programs\Odyle\Odyle.exe";
        Check(StartupCommand.For(exe) == "\"" + exe + "\" --tray" && StartupCommand.ValueName == "Odyle", "the Run entry starts this executable in the tray");
        Check(StartupCommand.PathOf(StartupCommand.For(exe)) == exe && StartupCommand.PathOf(@"C:\Odyle\Odyle.exe --tray") == @"C:\Odyle\Odyle.exe" && StartupCommand.PathOf("\"broken") is null && StartupCommand.PathOf(" ") is null, "the executable of a Run entry");
        Check(StartupCommand.InTray(["--TRAY"]) && !StartupCommand.InTray([]) && !StartupCommand.InTray(["--update-probe"]), "the tray argument");
        Check(StartupCommand.ShowsWindow(tray: false, paired: true) && StartupCommand.ShowsWindow(tray: true, paired: false) && !StartupCommand.ShowsWindow(tray: true, paired: true), "a paired start with Windows stays in the tray");
        // Two screens side by side; the right one is shorter.
        ScreenArea[] screens = [new(0, 0, 1920, 1040), new(1920, 0, 3200, 760)];
        Check(Placement.OnScreen(100, 100, screens) && Placement.OnScreen(2000, 700, screens) && Placement.OnScreen(-30, 0, screens), "a position whose caption is on a screen is restored");
        Check(!Placement.OnScreen(2000, 900, screens) && !Placement.OnScreen(5000, 100, screens) && !Placement.OnScreen(-2000, 100, screens) && !Placement.OnScreen(100, -100, screens), "a position on a disconnected screen is not");
        Check(Placement.Clamp(1800, 900, 280, 300, screens[0]) == (1640, 740) && Placement.Clamp(100, 100, 280, 300, screens[0]) == (100, 100) && Placement.Clamp(-30, -5, 280, 300, screens[0]) == (0, 0) && Placement.Clamp(10, 10, 2500, 1200, screens[0]) == (0, 0), "a window is kept on its screen");
        // Planned runs as the room snapshot lists them; the account "u" goes to two of them.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RoomSnapshot>("""{"room":{"id":"r","name":"Evening","cost":40},"members":[],"wishlist":[],"dungeons":[],"serverNow":"2026-10-14T18:40:00.000Z","runs":[{"id":"a","dungeon":"Fire Temple","startsAt":"2026-10-14 19:00:00.000Z","note":"Bring potions","planner":"u","attendees":["u","l"]},{"id":"b","dungeon":"Ice Cave","startsAt":"2026-10-14 18:50:00.000Z","note":"","planner":"l","attendees":["l"]},{"id":"c","dungeon":"Sky Tower","startsAt":"2026-10-14 18:55:00.000Z","note":"","planner":"","attendees":["u"]}]}""", options)!;
        Check(snapshot.Runs is [{ Id: "a", Dungeon: "Fire Temple", Note: "Bring potions", Planner: "u", Attendees: ["u", "l"] }, _, _], "runs are read from the room snapshot");
        var at = (string stamp) => Readiness.Parse(stamp)!.Value; var none = new HashSet<string>();
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T18:40:00Z"), none).Select(d => d.Run.Id).SequenceEqual(["c"]), "only joined runs within 15 minutes are due");
        var due = Reminders.Due([snapshot], "u", at("2026-10-14T18:46:00Z"), none);
        Check(due.Select(d => d.Run.Id).SequenceEqual(["c", "a"]) && due[1] is { Room.Name: "Evening", StartsAt.Hour: 19, Key: "a|2026-10-14 19:00:00.000Z" }, "due runs come soonest first with their room");
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T18:46:00Z"), new HashSet<string>(due.Select(d => d.Key))).Length == 0, "a run is reminded once");
        var moved = snapshot with { Runs = [snapshot.Runs![0] with { StartsAt = "2026-10-14 18:58:00.000Z" }] };
        Check(Reminders.Due([moved], "u", at("2026-10-14T18:46:00Z"), new HashSet<string>(due.Select(d => d.Key))).Single().Run.Id == "a", "a re-timed run reminds again");
        Check(Reminders.Due([snapshot], "u", at("2026-10-14T19:00:00Z"), none).Length == 0 && Reminders.Due([snapshot], "", at("2026-10-14T18:50:00Z"), none).Length == 0 && Reminders.Due([snapshot with { Runs = null }], "u", at("2026-10-14T18:50:00Z"), none).Length == 0, "started runs, no account or no runs remind nothing");
        Check(Reminders.Prune(["a|2026-10-14 19:00:00.000Z", "old|2026-10-12 19:00:00.000Z", "a|2026-10-14 19:00:00.000Z"], at("2026-10-14T20:00:00Z")).SequenceEqual(["a|2026-10-14 19:00:00.000Z"]), "reminders of old runs are forgotten");
        Console.WriteLine("Start with Windows command, tray start, saved window position, keep-on-screen and planned-run reminders passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Desktop check failed: " + message); }
}
