using Odyle.Core;

// Reader setup on the PC: why a running game shows no connection (from the connection table
// only), the diagnostics report and its redaction, the log tail and the uninstaller's --unpair.
static class SetupChecks
{
    public static void Run()
    {
        Check(GameConnection.IsGameProcess("AION2") && GameConnection.IsGameProcess("aion2-win64-shipping") && GameConnection.IsGameProcess("AION-2") && !GameConnection.IsGameProcess("Aion") && !GameConnection.IsGameProcess("Discord"), "the game's process names");
        TcpConnection Game(ushort port, bool ipv6 = false) => new(true, "AION2", port, ipv6);
        Check(GameConnection.Explain([]) is null && GameConnection.Explain([Game(443), Game(80)]) is null, "web connections alone say nothing");
        Check(GameConnection.Explain([Game(443), Game(GameConnection.Port, ipv6: true)]) == "AION2 connects to the game server over IPv6, which the reader cannot read yet.", "an IPv6 game connection is named");
        Check(GameConnection.Explain([Game(443), new(false, "ExitLag", GameConnection.Port, false)]) == "The game connection runs through ExitLag, not AION2 · an accelerator or proxy hides it from the reader.", "another program on the game port is named");
        Check(GameConnection.Explain([Game(443), Game(7001), Game(7000), Game(7001), Game(9000), Game(9100)]) == "AION2 is connected, but not on the game port 13328 (ports 7000, 7001, 9000) · the reader only reads 13328.", "other game ports are listed, at most three");
        Check(GameConnection.Explain([Game(7000), Game(GameConnection.Port, ipv6: true), new(false, "ExitLag", GameConnection.Port, false)])!.Contains("IPv6"), "IPv6 is the first reason");

        // The report masks the account id, this PC's name and the Windows user folder besides what the log masks.
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJpZCI6InUiLCJ0eXBlIjoiYXV0aCJ9.c2lnbmF0dXJlLXNpZ25hdHVyZQ";
        var report = Diagnostics.Report(new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), [("App", "Odyle 0.6.0.12"), ("Npcap", "installed 1.79")], ["20:01:02 Waiting · Waiting for the AION2 game connection…"],
            ["Connected to https://party.example as account abcdefghij12345 on device \"DESKTOP-PLAYER\".", @"Odyle 0.6.0.13 is waiting in C:\Users\Player Name\AppData\Local\Odyle\updates\0.6.0.13.", "Decoder: own character TestHero, server 2304.", "token " + jwt]);
        Check(report.StartsWith("Odyle diagnostics · 2026-10-15 20:00:00 +00:00") && report.Contains("App: Odyle 0.6.0.12") && report.Contains("Npcap: installed 1.79") && report.ReplaceLineEndings("\n").Contains("Last reader states:\n  20:01:02 Waiting"), "the report lists the facts and the reader's last states");
        Check(report.Contains("Log, last 4 lines:") && report.Contains("account [account]") && report.Contains("device \"[this PC]\"") && report.Contains(@"C:\Users\[user]\AppData") && report.Contains("[token]"), "the account, the PC's name, the user folder and tokens are masked");
        Check(!report.Contains("abcdefghij12345") && !report.Contains("DESKTOP-PLAYER") && !report.Contains("Player Name") && !report.Contains("eyJ") && report.Contains("TestHero") && report.Contains("https://party.example"), "nothing personal is left, the game's facts are kept");
        Check(Diagnostics.Report(DateTimeOffset.UnixEpoch, [], [], []).Contains("none this run"), "a report without reader states says so");

        // The log's tail spans the previous file and the current one.
        var folder = Directory.CreateTempSubdirectory("odyle-tail-check-").FullName;
        try
        {
            var log = new RotatingLog(Path.Combine(folder, "odyle.log"), limit: 600);
            for (var i = 0; i < 12; i++) log.Write("INFO", $"line {i:00} " + new string('x', 40));
            var tail = log.Tail(5);
            Check(tail.Length == 5 && tail[^1].EndsWith("line 11 " + new string('x', 40)) && tail.Select(l => l[^43..^41]).SequenceEqual(["07", "08", "09", "10", "11"]), "the tail is the newest lines in order, across the rotation");
            Check(log.Tail(1000).Length <= 12 && new RotatingLog(Path.Combine(folder, "missing.log")).Tail(10).Length == 0, "a short or missing log gives what there is");
        }
        finally { Directory.Delete(folder, true); }

        Check(StartupCommand.Unpairs(["--unpair"]) && StartupCommand.Unpairs(["--UNPAIR"]) && !StartupCommand.Unpairs(["--tray"]) && !StartupCommand.InTray(["--unpair"]), "the uninstaller's --unpair argument");
        Console.WriteLine("Missing game connection reasons, diagnostics report and redaction, log tail and --unpair passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Setup check failed: " + message); }
}
