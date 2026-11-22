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
using System.Windows.Shell;
using System.Windows.Threading;
using Odyle.Core;
using Odyle.Reader;

// The Windows app's UI checks. Each scenario gets a fresh window and fresh preferences, pairing and hand-over
// files, so one regression never hides the checks after it; every failed check is reported, and the run ends
// non-zero when any failed.
//   (no arguments)         every offline scenario: what CI runs
//   <url> <pair code>      the offline scenarios, then the online one against a live backend (the dashboard's
//                          integration suite runs this and asserts the online scenario's last line)
//   --only <a,b>           only the scenarios whose names contain one of these (with any of the above)
//   --list                 the scenarios' names
//   --screenshots <dir>    previews of the window (writes PNGs; see Screenshots)
//   --update-probe, --compact-probe: opt-in probes on a desktop
static partial class ReaderChecks
{
    [STAThread] static void Main(string[] args)
    {
        var only = Option(ref args, "--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The checks read English texts whatever the language of the Windows running them.
        Localizer.Use("en");
        // Preferences, the pairing, the hand-over and the log go to temporary files so the checks never touch the user's.
        var scratch = Directory.CreateTempSubdirectory("odyle-check-").FullName;
        Fresh(scratch, "start"); Log.UseFolder(Path.Combine(scratch, "logs"));
        // Start with Windows writes to a Run key kept in memory, never to the real one.
        Autostart.Key = runKey; Autostart.Executable = @"C:\Odyle\Odyle.exe";
        Register(args is [var url, var code] && !url.StartsWith("--") ? (url, code) : null);
        if (args is ["--list"]) { foreach (var scenario in scenarios) Console.WriteLine(scenario.Name); return; }
        App.Headless = true; var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            MainWindow? probe = null;
            try
            {
                if (args is ["--screenshots", var directory]) { probe = NewWindow(); Screenshots(probe, directory); return; }
                if (args is ["--update-probe"]) { await UpdateProbe(); return; }
                if (args is ["--compact-probe"]) { probe = NewWindow(); CompactProbe(probe); return; }
                await RunAsync(only, scratch);
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                try { if (probe is not null) await Retire(probe); }
                finally
                {
                    if (failures.Count > 0) Environment.ExitCode = 1;
                    try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }
        });
        Dispatcher.Run();
    }
    // ----- Scenarios -----
    // A scenario with a window gets a fresh one; one without needs none. Add a group of scenarios in its own file
    // (a static partial class ReaderChecks) and register it below with Add or AddStatic.
    sealed record Scenario(string Name, Func<MainWindow?, Task> Run, bool Window);
    static readonly List<Scenario> scenarios = [];
    static void Add(string name, Func<MainWindow, Task> run) => scenarios.Add(new(name, window => run(window!), true));
    static void Add(string name, Action<MainWindow> run) => Add(name, window => { run(window); return Task.CompletedTask; });
    static void AddStatic(string name, Func<Task> run) => scenarios.Add(new(name, _ => run(), false));
    static void AddStatic(string name, Action run) => AddStatic(name, () => { run(); return Task.CompletedTask; });
    static void Register((string Url, string Code)? online)
    {
        Add("layout", LayoutScenario);
        AddStatic("updates", UpdateChecks);
        AddStatic("session-store", SessionStoreScenario);
        AddStatic("connection-lifecycle", ConnectionLifecycleScenario);
        Add("offline-restore", OfflineRestoreScenario);
        Add("pairing", PairingChecks);
        Add("early-reading", EarlyReadingChecks);
        AddStatic("capture-looks", CaptureLooks);
        AddStatic("attribution-state", AttributionStateScenario);
        Add("attribution", AttributionScenario);
        Add("publishing", PublishingScenario);
        Add("party-list", PartyListScenario);
        Add("announcements", AnnouncementsScenario);
        Add("tray-and-characters", TrayAndCharactersScenario);
        Add("follow-and-fade", FollowAndFadeScenario);
        Add("handover", HandoverScenario);
        Add("preferences-and-tasks", PreferencesAndTasksScenario);
        Add("compact-mode", CompactModeScenario);
        Add("cap-mismatch", CapMismatchScenario);
        Add("language", LanguageScenario);
        Add("autostart", AutostartScenario);
        Add("npcap", NpcapScenario);
        Add("diagnostics", DiagnosticsScenario);
        AddStatic("unpair", UnpairScenario);
        Add("position", PositionScenario);
        AddStatic("czech", CzechScenario);
        Add("run-reminders", RunRemindersScenario);
        Add("notification-settings", NotificationSettingsScenario);
        Add("run-chip-and-places", RunChipAndPlacesScenario);
        Add("party-refresh", PartyRefreshChecks);
        if (online is { } live) Add("online", window => OnlineScenario(window, live.Url, live.Code));
        Add("heartbeat-unpaired", HeartbeatUnpairedScenario);
        Add("server-busy", ServerBusyScenario);
        Add("held-back", HeldBackScenario);
        Add("update-now", UpdateNowScenario);
        Add("quit-cap", QuitCapScenario);
        Add("quit-restart", QuitRestartScenario);
        Add("quit-after-install", QuitAfterInstallScenario);
        AddStatic("discord-status", DiscordStatusScenario);
        AddStatic("open-in-odyle", OpenInOdyleScenario);
    }
    static readonly TimeSpan ScenarioLimit = TimeSpan.FromMinutes(2);
    static readonly MemoryRunKey runKey = new();
    static async Task RunAsync(string[]? only, string scratch)
    {
        var chosen = scenarios.Where(s => only is null || only.Any(o => s.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (chosen.Length == 0) Fail("no scenario's name contains " + string.Join(" or ", only ?? []));
        var probeNpcap = Npcap.Probe; var restricted = Npcap.Restricted; var number = 0;
        foreach (var scenario in chosen)
        {
            // What one scenario changed of the app's static state never reaches the next.
            Fresh(scratch, $"{++number:00}-{scenario.Name}"); Npcap.Probe = probeNpcap; Npcap.Restricted = restricted; Localizer.Use("en"); runKey.Values.Clear(); Autostart.Key = runKey;
            scenarioName = scenario.Name; var failed = failures.Count; var counted = checks;
            var window = scenario.Window ? NewWindow() : null;
            try
            {
                var run = scenario.Run(window);
                if (await Task.WhenAny(run, Task.Delay(ScenarioLimit)) != run) Fail($"did not finish within {ScenarioLimit.TotalSeconds:0} s");
                else await run;
            }
            catch (Exception error) { Fail("stopped: " + error); }
            finally { if (window is not null) await Retire(window); }
            var ok = failures.Count == failed;
            Console.WriteLine($"{(ok ? "passed" : "FAILED")}  {scenario.Name} ({checks - counted} checks{(ok ? "" : $", {failures.Count - failed} failed")})");
        }
        Console.WriteLine($"Reader checks: {chosen.Length} scenarios, {checks} checks, {failures.Count} failed.");
        if (failures.Count > 0) { Console.Error.WriteLine("Failed checks:"); foreach (var failure in failures) Console.Error.WriteLine("  " + failure); }
        else if (only is null) Console.WriteLine("Windows UI compact window, compact mode, What's new, separate settings, reading card, party list, narrow layout, reader pause, Npcap setup, Start with Windows offer, diagnostics and --unpair passed.");
    }
    // A window as the app builds it, without a tray icon; quitting it ends nothing (Retire), so the next scenario runs.
    static MainWindow NewWindow()
    {
        var window = new MainWindow(false);
        window.EndApp = (restart, tray) => Ended = (restart, tray);
        return window;
    }
    // How the last window that quit would have ended the app: restarted (in the tray or not) or shut down.
    static (bool Restart, bool Tray)? Ended;
    static async Task Retire(MainWindow window)
    {
        var quit = (Task)Call(window, "QuitAsync", false)!;
        if (await Task.WhenAny(quit, Task.Delay(TimeSpan.FromSeconds(10))) != quit) Fail("the window did not quit within 10 s");
    }
    static void Fresh(string scratch, string name)
    {
        var folder = Path.Combine(scratch, name); Directory.CreateDirectory(folder);
        PreferencesStore.FilePath = Path.Combine(folder, "preferences.json"); SessionStore.FilePath = Path.Combine(folder, "session.bin"); HandoverStore.FilePath = Path.Combine(folder, "handover.json");
    }
    // Takes "--name value" out of the arguments.
    static string? Option(ref string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        if (at < 0 || at + 1 >= args.Length) return null;
        var value = args[at + 1]; args = [.. args[..at], .. args[(at + 2)..]];
        return value;
    }
    static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
    // Pairing (#79): a pairing whose session start fails keeps the saved pairing and tries again by itself
    // instead of staying half-connected, and an action from outside the window waits for a running
    // operation instead of being dropped. Party and force members' names (#81) follow the account switch.
    static async Task PairingChecks(MainWindow window)
    {
        var server = new PairingServer { FailSession = true }; var settings = Settings(window);
        var probeNpcap = Npcap.Probe; Npcap.Probe = () => false;
        var makeApi = Field("newApi").GetValue(window);
        Field("newApi").SetValue(window, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, server)));
        var restore = window.Link.RetryTimer;
        try
        {
            // One operation at a time: a click while one runs is dropped, an action from outside waits and then runs.
            var gate = new TaskCompletionSource(); var ran = new List<string>();
            var first = (Task)Call(window, "ExecuteAsync", (Func<Task>)(() => gate.Task), false)!;
            var dropped = (Task)Call(window, "ExecuteAsync", (Func<Task>)(() => { ran.Add("click"); return Task.CompletedTask; }), false)!;
            var queued = (Task)Call(window, "ExecuteAsync", (Func<Task>)(() => { ran.Add("queued"); return Task.CompletedTask; }), true)!;
            await dropped;
            Check(ran.Count == 0 && (bool)Field("busy").GetValue(window)! && !queued.IsCompleted, "while an operation runs a click is dropped and a queued action waits");
            gate.SetResult(); await first; await queued;
            Check(ran.SequenceEqual(["queued"]) && !(bool)Field("busy").GetValue(window)!, "the queued action runs once the operation before it ended");
            // Paired, but the session cannot start: the pairing is saved, the app is not half-connected, and it tries again.
            ((PasswordBox)settings.FindName("CodeInput")).Password = "https://party.example/#odyle-pair=ABCDEFGH23456789";
            await (Task)Call(window, "PairAsync")!;
            Check(server.Paired == 1 && window.Link.Api is null && restore.IsEnabled && window.Link.RetryAt is not null && SessionStore.Load(out _) is { Endpoint: "https://party.example", DeviceKey: "pairing-check-key" }
                && ((Button)settings.FindName("RetryButton")).Visibility == Visibility.Visible && !window.Link.Heartbeat.IsEnabled, "a pairing whose session fails keeps the saved pairing and tries again by itself");
            Check(Control<TextBlock>(window, "StatusLabel").Text.StartsWith("Server unavailable · retrying in 15 s"), "the footer counts down to the next try: " + Control<TextBlock>(window, "StatusLabel").Text);
            server.FailSession = false;
            await (Task)Call(window, "RestoreAsync")!;
            Check(window.Link.Api is PartyApi && window.Link.SessionId == "s1" && window.Link.Heartbeat.IsEnabled && !restore.IsEnabled && window.Link.RetryAt is null, "the next try connects the saved pairing");
            // Party and force members' names: an answer { shared: false } turns them off until the server says otherwise.
            var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
            Field("currentCharacterId").SetValue(window, "c1"); window.Attribution.GameId = 7UL; window.Attribution.DetectedKey = "Hero|2304"; window.Attribution.GameKey = "Hero|2304";
            List<string> Waiting() => Conn(window).TeammatesWaiting;
            bool Off() => Conn(window).TeammatesOff;
            server.Teammates = """{"shared":false}""";
            Call(window, "OnTeammates", inert, new[] { "Mate" });
            await Until(() => server.TeammateRequests == 1 && Off(), 200);
            Call(window, "OnTeammates", inert, new[] { "Other" });
            Check(server.TeammateRequests == 1 && Waiting().Count == 0, "while the account has the names off none are kept or sent");
            server.Heartbeat = """{"serverNow":"2026-11-21 12:00:00.000Z","shareTeammates":true}""";
            await (Task)Call(window, "PulseAsync")!;
            Check(!Off(), "a heartbeat that says shareTeammates: true turns the names on again");
            server.Teammates = """{"shared":true,"queued":1,"serverNow":"x"}""";
            Call(window, "OnTeammates", inert, new[] { "Other" });
            await Until(() => server.TeammateRequests == 2, 200);
            server.Heartbeat = """{"serverNow":"2026-11-21 12:00:20.000Z","shareTeammates":false}""";
            await (Task)Call(window, "PulseAsync")!;
            Call(window, "OnTeammates", inert, new[] { "Third" });
            Check(Off() && server.TeammateRequests == 2 && Waiting().Count == 0, "a heartbeat that says shareTeammates: false stops them");
            // A backend from before the switch says nothing, which means on (it has no switch to turn off).
            server.Heartbeat = """{"serverNow":"2026-11-21 12:00:40.000Z"}""";
            await (Task)Call(window, "PulseAsync")!;
            Check(!Off(), "a heartbeat without shareTeammates means on");
            // A route that refuses this build's adapter stops for the rest of the connection (#92).
            server.RefuseGroup = true;
            await (Task)Call(window, "SendGroupAsync")!; await (Task)Call(window, "SendGroupAsync")!;
            Check(server.GroupRequests == 1 && Conn(window).Group.Refused && Conn(window).TeammatesSent.Contains("Other"), "a refused group adapter stops the parties and forces for this connection");
            Conn(window).AdapterOutdated = true; Conn(window).NoOdyleSent = "c1";
            // A new publisher session is asked for without the previous one only when the server answered it (#86).
            async Task<int> Reconnect(HttpStatusCode? answer, bool unreachable = false)
            {
                server.PreviousSession = answer; server.PreviousUnreachable = unreachable; var before = server.Sessions;
                try { await (Task)Call(window, "ReconnectAsync")!; } catch (HttpRequestException) { }
                server.PreviousSession = null; server.PreviousUnreachable = false;
                return server.Sessions - before;
            }
            Check(await Reconnect(HttpStatusCode.NotFound) == 2 && await Reconnect(HttpStatusCode.BadRequest) == 2 && window.Link.SessionId == "s1", "a server that answered for the previous session is asked for a fresh one");
            Check(await Reconnect(HttpStatusCode.ServiceUnavailable) == 1 && await Reconnect(HttpStatusCode.BadGateway) == 1 && await Reconnect(null, unreachable: true) == 1, "a server error or no connection is not asked twice");
            Check(Conn(window).Group.Refused && Conn(window).AdapterOutdated, "a new session on the same connection keeps what the server refused");
            server.Heartbeat = """{"serverNow":"2026-11-21 12:01:00.000Z","shareTeammates":false}"""; await (Task)Call(window, "PulseAsync")!;
            Field("capture").SetValue(window, null); window.Attribution.Reset();
            await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
            Check(!Off() && !File.Exists(SessionStore.FilePath), "signing out resets the switch to the default");
            // Pairing with another server in the same run starts from nothing the first one refused or was sent (#92).
            var other = new PairingServer();
            Field("newApi").SetValue(window, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, other)));
            ((PasswordBox)settings.FindName("CodeInput")).Password = "https://other.example/#odyle-pair=ABCDEFGH23456789";
            await (Task)Call(window, "PairAsync")!;
            var fresh = Conn(window);
            Check(window.Link.Api is PartyApi { Endpoint: "https://other.example" } && !fresh.Group.Refused && fresh.TeammatesSent.Count == 0 && !fresh.AdapterOutdated && fresh.NoOdyleSent == "" && !fresh.TeammatesOff, "pairing with another server forgets what the first one refused and was sent");
            Field("capture").SetValue(window, inert);
            Field("currentCharacterId").SetValue(window, "c1"); window.Attribution.GameId = 7UL; window.Attribution.DetectedKey = "Hero|2304"; window.Attribution.GameKey = "Hero|2304";
            await (Task)Call(window, "SendGroupAsync")!;
            Check(other.GroupRequests == 1 && server.GroupRequests == 1, "and the new server gets the party and force");
            Field("capture").SetValue(window, null); window.Attribution.Reset();
        }
        finally
        {
            Field("capture").SetValue(window, null); window.Attribution.Reset();
            if (window.Link.Api is not null || restore.IsEnabled) await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
            ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); Npcap.Probe = probeNpcap;
            Field("newApi").SetValue(window, makeApi); window.Link.SessionId = ""; Field("currentCharacterId").SetValue(window, "");
        }
    }
    // The reader starts before the server is asked (#86): what it reads meanwhile, the login snapshot included,
    // waits for the connection, then is saved to the character the game named and sent, once.
    static async Task EarlyReadingChecks(MainWindow window)
    {
        var server = new PairingServer { SignInGate = new(), SignInArrived = new() };
        var makeApi = Field("newApi").GetValue(window); var startCapture = Field("startCapture").GetValue(window); var probeNpcap = Npcap.Probe;
        var toServer = (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, server));
        Field("newApi").SetValue(window, toServer);
        Field("startCapture").SetValue(window, (Action<PacketCapture>)(_ => { })); Npcap.Probe = () => true;
        try
        {
            SessionStore.Save(new SavedSession("https://party.example", "token", "pairing-check-key"));
            var restoring = (Task)Call(window, "ExecuteAsync", (Func<Task>)(() => (Task)Call(window, "RestoreAsync")!), false)!;
            await server.SignInArrived!.Task;
            var reading = Field("capture").GetValue(window) as PacketCapture;
            Check(reading is not null && window.Link.Api is PartyApi && window.Link.SessionId is "", "the reader runs while the server is still being asked");
            Call(window, "OnCandidate", reading!, new PacketCandidate(512, ExperimentalOdyleDecoder.Version, CandidateSource.Snapshot) { Base = 12, Bonus = 500 });
            var naming = (Task)Call(window, "SaveDetectedCharacterAsync", reading!, new GameIdentity(46, "EarlyHero", 2304, "Lumiel", "GLOBAL_EU"))!;
            await Task.Delay(100);
            Check(!naming.IsCompleted && server.Saved == 0 && server.Reports == 0 && Control<TextBlock>(window, "ValueLabel").Text == "512", "what it reads is shown and waits for the connection");
            server.SignInGate!.SetResult(); await restoring; await naming;
            ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop(); await (Task)Call(window, "PublishLatestAsync")!;
            Check(server.Saved == 1 && server.Reports == 1 && server.LastReport.Contains("\"characterId\":\"c7\"") && server.LastReport.Contains("\"value\":512") && window.Attribution.OwnerKey == "EarlyHero|2304" && (string)Field("activeCharacterId").GetValue(window)! == "c7",
                "the login snapshot read before the connection is saved to the character the game named and sent once: " + server.LastReport);
            // Pairing with another server while the reader reads: the game's character is saved there again and its reading
            // goes to that character, never to the new account's character read last (#92).
            var other = new PairingServer { CharacterId = "b7", HasAlt = true };
            Field("newApi").SetValue(window, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, other)));
            ((PasswordBox)Settings(window).FindName("CodeInput")).Password = "https://other.example/#odyle-pair=ABCDEFGH23456789";
            await (Task)Call(window, "ExecuteAsync", (Func<Task>)(() => (Task)Call(window, "PairAsync")!), false)!;
            await Until(() => other.Reports == 1, 200);
            ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop(); await (Task)Call(window, "PublishLatestAsync")!;
            Check(other.Saved == 1 && other.Reports == 1 && other.LastReport.Contains("\"characterId\":\"b7\"") && server.Reports == 1 && (string)Field("currentCharacterId").GetValue(window)! == "b7" && (string)Field("activeCharacterId").GetValue(window)! == "b7",
                "after pairing with another server the reading goes to the game's character saved there: " + other.LastReport);
            Field("newApi").SetValue(window, toServer);
            // A pairing the server ended (its own 401 at sign-in) stops the reader that started for the login snapshot.
            await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
            server.EndPairing = true; SessionStore.Save(new SavedSession("https://party.example", "token", "pairing-check-key"));
            await (Task)Call(window, "ExecuteAsync", (Func<Task>)(() => (Task)Call(window, "RestoreAsync")!), false)!;
            Check(Field("capture").GetValue(window) is null && !(bool)Field("readerWanted").GetValue(window)! && window.Link.Api is null && !File.Exists(SessionStore.FilePath), "a pairing the server ended stops the reader that started for the login snapshot");
            // A server that cannot be reached at start turns the card offline, also when the reader started before it was
            // asked (the failure itself renders the card, not only the operation around it).
            server.EndPairing = false; server.FailSignIn = true; SessionStore.Save(new SavedSession("https://party.example", "token", "pairing-check-key"));
            await (Task)Call(window, "RestoreAsync")!;
            Check(Field("capture").GetValue(window) is PacketCapture && window.Link.Api is null && Control<TextBlock>(window, "AccountLabel").Text == "OFFLINE" && Control<TextBlock>(window, "ReadingLabel").Text.StartsWith("Offline · "),
                "an unreachable server at start leaves the card offline, not connecting: " + Control<TextBlock>(window, "AccountLabel").Text);
        }
        finally
        {
            await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
            ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop(); Npcap.Probe = probeNpcap;
            Field("newApi").SetValue(window, makeApi); Field("startCapture").SetValue(window, startCapture); window.Link.SessionId = ""; Field("currentCharacterId").SetValue(window, "");
        }
    }
    // Answers pairing, sign-in, the session (503 while FailSession), the account's characters and rooms, the
    // heartbeat and the teammates route with what the check sets; the realtime connection is refused.
    // A route refuses its adapter while RefuseGroup is set; the session answers PreviousSession (or no connection)
    // to a request naming the previous one; SignInGate holds the sign-in until released; the character the game
    // names is saved as c7 and its readings are counted.
    sealed class PairingServer : HttpMessageHandler
    {
        public bool FailSession, RefuseGroup, PreviousUnreachable, EndPairing, FailSignIn, HasAlt;
        public string CharacterId = "c7";
        public HttpStatusCode? PreviousSession;
        public int Paired, TeammateRequests, GroupRequests, Sessions, Saved, Reports;
        public string LastReport = "";
        public TaskCompletionSource? SignInGate, SignInArrived;
        public string Teammates = """{"serverNow":"x"}""", Heartbeat = """{"serverNow":"2026-11-21 12:00:00.000Z"}""";
        const string Auth = """{"token":"pairing-check-token","record":{"id":"u","displayName":"Player"},"deviceKey":"pairing-check-key"}""";
        string Early => $$"""{"id":"{{CharacterId}}","user":"u","characterName":"EarlyHero","server":"Lumiel","region":"GLOBAL_EU","value":0,"confirmed":false,"valid":false,"lastReadingAt":""}""";
        string EarlyRead => $$"""{"id":"{{CharacterId}}","user":"u","characterName":"EarlyHero","server":"Lumiel","region":"GLOBAL_EU","value":512,"confirmed":true,"valid":true,"lastReadingAt":"2026-11-21 12:00:00.000Z"}""";
        // HasAlt: the account already has a character, read more recently than any other, which the card shows until the game names one.
        const string Alt = """{"id":"z1","user":"u","characterName":"OldAlt","server":"Lumiel","region":"GLOBAL_EU","value":90,"confirmed":true,"valid":false,"lastReadingAt":"2026-11-20 12:00:00.000Z"}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath;
            var sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            if (route == "/api/odyle/pair") Paired++;
            if (route == "/api/odyle/teammates") TeammateRequests++;
            if (route == "/api/odyle/game-group") GroupRequests++;
            if (route == "/api/odyle/character") Saved++;
            if (route == "/api/odyle/report") { Reports++; LastReport = sent; }
            if (route == "/api/odyle/desktop-sign-in" && SignInGate is { } gate) { SignInArrived?.TrySetResult(); await gate.Task; }
            var previous = route == "/api/odyle/session" && sent.Contains("previousSessionId");
            if (route == "/api/odyle/session") Sessions++;
            if (previous && PreviousUnreachable) throw new HttpRequestException("No such host is known.");
            var (status, body) = route switch
            {
                "/api/odyle/desktop-sign-in" when EndPairing => (HttpStatusCode.Unauthorized, """{"status":401,"message":"The device is not paired."}"""),
                "/api/odyle/desktop-sign-in" when FailSignIn => (HttpStatusCode.ServiceUnavailable, """{"status":503,"message":"Something went wrong."}"""),
                "/api/odyle/pair" or "/api/odyle/desktop-sign-in" => (HttpStatusCode.OK, Auth),
                "/api/odyle/session" when FailSession => (HttpStatusCode.ServiceUnavailable, """{"status":503,"message":"Something went wrong."}"""),
                "/api/odyle/session" when previous && PreviousSession is { } answer => (answer, $$"""{"status":{{(int)answer}},"message":"Unknown session."}"""),
                "/api/odyle/session" => (HttpStatusCode.OK, """{"sessionId":"s1","sequence":0}"""),
                "/api/collections/characters/records" => (HttpStatusCode.OK, "{\"items\":[" + string.Join(",", new[] { HasAlt ? Alt : "", Saved > 0 ? Early : "" }.Where(c => c.Length > 0)) + "]}"),
                "/api/odyle/character" => (HttpStatusCode.OK, $$"""{"character":{{Early}}}"""),
                "/api/odyle/report" => (HttpStatusCode.OK, $$"""{"character":{{EarlyRead}}}"""),
                "/api/odyle/rooms" => (HttpStatusCode.OK, """{"rooms":[],"odylePerCube":40,"announcement":null}"""),
                "/api/odyle/heartbeat" => (HttpStatusCode.OK, Heartbeat),
                "/api/odyle/teammates" => (HttpStatusCode.OK, Teammates),
                "/api/odyle/game-group" when RefuseGroup => (HttpStatusCode.BadRequest, System.Text.Json.JsonSerializer.Serialize(new { status = 400, message = PartyApi.GroupAdapterRefused })),
                "/api/realtime" => (HttpStatusCode.ServiceUnavailable, """{"status":503,"message":"Something went wrong."}"""),
                _ => (HttpStatusCode.OK, "{}"),
            };
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    // Refreshing the party: one room that times out or fails keeps what it showed instead of failing
    // the whole refresh, a room's realtime news fetches only that room, and a refresh of one room never
    // runs beside a refresh of the whole party, so an older answer can never replace a newer one.
    static async Task PartyRefreshChecks(MainWindow window)
    {
        var server = new PartyServer(); window.Link.Api = new PartyApi("http://127.0.0.1:8090", "token", null, server); Field("accountId").SetValue(window, "u");
        int Value(string room) => ((RoomSnapshot[])Field("snapshots").GetValue(window)!).Single(s => s.Room.Id == room).Members.Single().Characters.Single().Value;
        string[] Shown() => ((RoomSnapshot[])Field("snapshots").GetValue(window)!).Select(s => s.Room.Id).ToArray();
        Task Refresh() => (Task)Call(window, "RefreshPartyAsync", new object[] { null! })!;
        Task News(string topic, string json) => (Task)Call(window, "OnNewsAsync", topic, RealtimeNews.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement.Clone()))!;
        // The first answers come from a server whose clock is an hour behind.
        server.Skew = TimeSpan.FromHours(-1); await Refresh();
        Check(Shown().SequenceEqual(new[] { "r2", "r1" }) && Value("r1") == 100 && Value("r2") == 50, "the party lists each room's snapshot");
        server.Skew = TimeSpan.Zero; server.Values["r1"] = 110; server.TimesOut.Add("r2"); await Refresh();
        Check(Shown().SequenceEqual(new[] { "r2", "r1" }) && Value("r1") == 110 && Value("r2") == 50, "a room that times out keeps what it showed and the others still refresh");
        Check(((TimeSpan)Field("serverOffset").GetValue(window)!).Duration() < TimeSpan.FromMinutes(1), "the server's clock comes from a fresh snapshot, never from one kept from before");
        server.TimesOut.Clear(); server.Fails["r2"] = HttpStatusCode.ServiceUnavailable; server.Values["r1"] = 115; await Refresh();
        Check(Shown().SequenceEqual(new[] { "r2", "r1" }) && Value("r1") == 115 && Value("r2") == 50, "a room the server fails to answer stays in the party");
        server.Fails.Clear(); server.Rooms.Remove("r2"); await Refresh();
        Check(Shown().SequenceEqual(new[] { "r1" }), "a room no longer listed is dropped");
        server.Rooms.Insert(0, "r2"); await Refresh();
        // A room's news without characters fetches that room only.
        server.Requests.Clear(); server.Values["r1"] = 120; await News("odyle.room.r1", """{"refresh":true}""");
        Check(server.Requests.SequenceEqual(new[] { "/api/odyle/snapshot/r1" }) && Value("r1") == 120 && Value("r2") == 50, "a room's news fetches that room's snapshot only: " + string.Join(", ", server.Requests));
        // News while the whole party is being fetched waits for it and fetches its room after it.
        server.Requests.Clear(); server.Values["r1"] = 130; server.Gate = new(); server.Arrived = new();
        var full = Refresh(); await server.Arrived.Task;
        server.Values["r1"] = 140; var news = News("odyle.room.r1", """{"refresh":true}""");
        await Task.Delay(100);
        Check(server.Requests.Count(r => r == "/api/odyle/snapshot/r1") == 1, "a room's news does not fetch beside a running refresh of the party");
        server.Gate.SetResult(); await Task.WhenAll(full, news); server.Gate = null;
        Check(Value("r1") == 140 && server.Requests.Last() == "/api/odyle/snapshot/r1", "the room's news is fetched after the party's refresh, so the newer answer stays: " + string.Join(", ", server.Requests));
        // Leaving a room, and news of a room the app does not list yet, fetch the whole party.
        server.Requests.Clear(); await News("odyle.room.r2", """{"refresh":true,"removed":true}""");
        Check(server.Requests.FirstOrDefault() == "/api/odyle/rooms", "leaving a room fetches the account's rooms");
        server.Requests.Clear(); await News("odyle.room.r3", """{"refresh":true}""");
        Check(server.Requests.FirstOrDefault() == "/api/odyle/rooms", "news of a room not listed yet fetches the account's rooms");
        // A room the server no longer shows the account (403) is dropped, and the rooms are fetched again.
        server.Requests.Clear(); server.Fails["r2"] = HttpStatusCode.Forbidden; await News("odyle.room.r2", """{"refresh":true}""");
        Check(server.Requests.Contains("/api/odyle/rooms") && !Shown().Contains("r2"), "a room the account no longer sees is dropped");
        Field("rooms").SetValue(window, Array.Empty<Room>()); Field("snapshots").SetValue(window, Array.Empty<RoomSnapshot>()); Field("serverOffset").SetValue(window, TimeSpan.Zero);
    }
    // Answers the rooms and room snapshots like the backend: each room's one character has the room's
    // value; rooms can time out or fail, and a gate holds the answer for r1 until released.
    sealed class PartyServer : HttpMessageHandler
    {
        public readonly List<string> Rooms = ["r2", "r1"];
        public readonly Dictionary<string, int> Values = new() { ["r1"] = 100, ["r2"] = 50 };
        public readonly HashSet<string> TimesOut = [];
        public readonly Dictionary<string, HttpStatusCode> Fails = [];
        public readonly List<string> Requests = [];
        public TimeSpan Skew;
        public TaskCompletionSource? Gate, Arrived;
        static string Room(string id) => $$"""{"id":"{{id}}","name":"Room {{id}}","cost":40,"role":"owner","sharing":true}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath; lock (Requests) Requests.Add(route);
            HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (route == "/api/odyle/rooms") return Answer(HttpStatusCode.OK, $$"""{"rooms":[{{string.Join(",", Rooms.Select(Room))}}],"odylePerCube":40,"announcement":null}""");
            if (!route.StartsWith("/api/odyle/snapshot/")) return Answer(HttpStatusCode.OK, "{}");
            var id = route["/api/odyle/snapshot/".Length..];
            if (TimesOut.Contains(id)) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing.", new TimeoutException());
            if (Fails.TryGetValue(id, out var failure)) return Answer(failure, """{"message":"Something went wrong."}""");
            var value = Values.GetValueOrDefault(id); var now = DateTimeOffset.UtcNow + Skew;
            if (id == "r1" && Gate is { } gate) { Arrived?.TrySetResult(); await gate.Task; }
            var seen = now.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
            return Answer(HttpStatusCode.OK, $$"""{"room":{{Room(id)}},"serverNow":"{{now:O}}","members":[{"id":"m-{{id}}","user":"k","role":"reporter","displayName":"Kaelen","characters":[{"id":"c-{{id}}","user":"k","characterName":"Kael","server":"Lumiel","region":"GLOBAL_EU","value":{{value}},"confirmed":true,"valid":true,"source":"packet","shared":true,"lastReadingAt":"{{seen}}","lastSeenAt":"{{seen}}"}]}]}""");
        }
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
            RollbackChecks(root);
            // A download reports its progress once per whole percent, not once per 64 KB read (#87).
            var reported = new List<double>(); var whole = new WholePercent(new Recorder(reported.Add));
            for (var done = 0; done <= 10_000; done += 7) whole.Report(done / 10_000.0);
            whole.Report(1);
            Check(reported.SequenceEqual(Enumerable.Range(0, 101).Select(p => p / 100.0)), "download progress is reported once per whole percent: " + reported.Count + " reports");
        }
        finally { Updater.Staging = staging; Directory.Delete(root, true); }
    }
    // Start health (#80): a build an update installed keeps the one before it as Odyle.exe.old until a start
    // proved healthy, and after two failed starts puts it back. The checks are no released build, so the app
    // never ran any of this here.
    static void RollbackChecks(string root)
    {
        Check(!Updater.IsReleaseBuild && !Updater.StartPending, "a development build never counts its starts");
        var folder = Path.Combine(root, "rollback"); Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "Odyle.exe"), old = exe + ".old", starts = exe + ".starts", failed = exe + ".failed";
        var version = new Version(0, 36, 0);
        void Installed() { foreach (var file in new[] { exe, old, starts, failed }) if (File.Exists(file)) File.Delete(file); File.WriteAllText(exe, "new"); File.WriteAllText(old, "previous"); }
        // Without .old (a fresh install, or a start long after the update) nothing is counted.
        File.WriteAllText(exe, "new"); File.WriteAllText(starts, "0.36.0.0 1");
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Settled && !Updater.StartPending && !File.Exists(starts), "a start without a previous build counts nothing");
        // A healthy start removes the previous build and its count; the next start is settled.
        Installed();
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Pending && Updater.StartPending && File.ReadAllText(starts) == "0.36.0.0 1" && File.Exists(old), "the first start after an update counts itself and keeps the previous build");
        Updater.ConfirmStart();
        Check(!Updater.StartPending && !File.Exists(old) && !File.Exists(starts) && File.ReadAllText(exe) == "new", "a healthy start removes the previous build");
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Settled, "after a healthy start nothing is counted");
        // Two starts that never proved healthy (a crash): the third start puts the previous build back.
        Installed();
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Pending && Updater.BeginStart(exe, version) == Updater.StartHealth.Pending && File.ReadAllText(starts) == "0.36.0.0 2", "each unconfirmed start is counted");
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.RolledBack && !Updater.StartPending && File.ReadAllText(exe) == "previous" && File.ReadAllText(failed) == "new" && !File.Exists(old) && !File.Exists(starts), "after two failed starts the previous build is back and the failed one kept aside");
        // A start that could not build its window: the first ends, the second puts the previous build back.
        Installed();
        Updater.BeginStart(exe, version);
        Check(!Updater.FailedStart(exe, version) && File.ReadAllText(exe) == "new", "the first failed start only ends");
        Updater.BeginStart(exe, version);
        Check(Updater.FailedStart(exe, version) && File.ReadAllText(exe) == "previous" && File.ReadAllText(failed) == "new", "the second failed start puts the previous build back");
        Check(!Updater.FailedStart(exe, version), "nothing is pending after a rollback");
        // A count left by another version starts over.
        Installed(); File.WriteAllText(starts, "0.35.1.0 2");
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Pending && File.ReadAllText(starts) == "0.36.0.0 1", "a count of another version starts over");
        // A healthy start whose previous build could not be removed yet is not counted again.
        File.WriteAllText(starts, "0.36.0.0 -1");
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Settled && !File.Exists(old) && !File.Exists(starts), "a start already proved healthy is settled and removes the previous build");
        // The version rolled back from is skipped until a newer one is out (this checks executable is stamped 1.0.0.0).
        File.Copy(Environment.ProcessPath!, failed, overwrite: true);
        Check(Updater.FailedVersion(exe, version) == new Version(1, 0, 0, 0) && Updater.FailedVersion(exe, new Version(1, 0, 0)) is null && Updater.FailedVersion(exe, new Version(2, 0, 0)) is null, "the failed version is skipped only while it is newer");
        // Installing an update starts the new build's count from none and forgets the rollback.
        var source = Path.Combine(root, "rollback-source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "Odyle.exe"), "newest");
        File.WriteAllText(old, "previous"); File.WriteAllText(starts, "0.36.0.0 1");
        Check(Updater.BeginStart(exe, version) == Updater.StartHealth.Pending, "an unconfirmed start before an update");
        Updater.Apply(source, folder);
        Check(File.ReadAllText(exe) == "newest" && File.ReadAllText(old) == "new" && !File.Exists(starts) && !File.Exists(failed) && !Updater.StartPending, "installing an update keeps the running build as the fallback and starts a fresh count");
        Updater.ConfirmStart();
        Check(File.Exists(old), "a quit after installing never removes the new build's fallback");
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
        // The window fits its content each time it shows (FitWindow): a fitting window is left as it is, and one whose
        // size missed its content (here size-to-content off and the window resized, as a resize from outside leaves it)
        // is fitted again when it shows, and the log says so.
        var fittingLog = Path.Combine(Log.Folder, "odyle.log");
        Check(!File.Exists(fittingLog) || !File.ReadAllText(fittingLog).Contains("fitting it again"), "a window that fits its content is left as it is");
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; GetWindowRect(handle, out var fits);
        window.SizeToContent = SizeToContent.Manual; SetWindowPos(handle, IntPtr.Zero, 0, 0, fits.Right - fits.Left + 120, fits.Bottom - fits.Top - 80, 0x0002 | 0x0004 | 0x0010); Settle();
        GetWindowRect(handle, out var off);
        Check(off.Right - off.Left == fits.Right - fits.Left + 120, "the probe made the window too wide and too short");
        window.Hide(); window.Show(); Settle(); Settle();
        GetWindowRect(handle, out var again);
        Check(window.SizeToContent == SizeToContent.WidthAndHeight && again.Right - again.Left == fits.Right - fits.Left && again.Bottom - again.Top == fits.Bottom - fits.Top && File.ReadAllText(fittingLog).Contains("fitting it again"),
            $"a window shown at a size that missed its content is fitted to it again ({again.Right - again.Left}×{again.Bottom - again.Top}, wanted {fits.Right - fits.Left}×{fits.Bottom - fits.Top})");
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
        // Previews show a released build, which keeps itself current.
        ((TextBlock)settings.FindName("UpdateLabel")).Text = "Odyle 0.5.0.2 · installs new releases at every start and looks for them once a day.";
        ((Button)settings.FindName("CheckUpdateButton")).Visibility = Visibility.Visible;
        Snapshot(window, Path.Combine(directory, "connect-preview.png"));
        Snapshot(settings, Path.Combine(directory, "settings-connect-preview.png"), 320);
        Field("accountName").SetValue(window, "Sample account");
        window.Link.Api = new PartyApi("http://127.0.0.1:8090");
        var stamp = DateTime.UtcNow.AddSeconds(-40).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, stamp), new Character("c2", "u", "TestAlt", "Lumiel", "GLOBAL_EU", 120, true, false, stamp) });
        Field("rooms").SetValue(window, new[] { new Room("r1", "The evening expedition", 40, "owner", true, ["c1"]), new Room("r2", "Weekend raid", 30, "reporter", true, ["c1", "c2"]) });
        Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { PartySnapshot(inGame: true) });
        ((TextBlock)settings.FindName("AccountDetail")).Text = "Signed in as Sample account via Discord · odyle.example";
        ((TextBlock)settings.FindName("UpdateLabel")).Text = "Odyle 0.5.0.2 · up to date · checked 20:14";
        Call(window, "ShowConnected", true); Call(window, "RenderRooms");
        Snapshot(settings, Path.Combine(directory, "settings-preview.png"), 320);
        Call(window, "SetDiscordStatus", true, false); Snapshot(settings, Path.Combine(directory, "settings-discord-preview.png"), 320); Call(window, "SetDiscordStatus", false, false);
        var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
        Call(window, "RenderReaderButton");
        window.Attribution.DetectedKey = "TestHero|2304"; window.Attribution.GameKey = "TestHero|2304";
        Call(window, "OnCandidate", inert, new PacketCandidate(390, ExperimentalOdyleDecoder.Version) { Base = 0, Bonus = 390 });
        window.Attribution.OwnerKey = "TestHero|2304"; Field("activeCharacterId").SetValue(window, "c1");
        var state = typeof(MainWindow).GetNestedType("ReaderState", BindingFlags.NonPublic)!;
        Call(window, "SetReaderState", Enum.Parse(state, "Live"), "Live");
        Call(window, "RenderHero"); Call(window, "RenderParty");
        Control<TextBlock>(window, "CaptureStatsLabel").Text = "1,284 packets · 96 KB from the game";
        Snapshot(window, Path.Combine(directory, "compact-preview.png"));
        // The whole party list with its frames, unscrolled (the window scrolls it past 190 px).
        var list = (ScrollViewer)Control<ItemsControl>(window, "PartyList").Parent; var listHeight = list.MaxHeight; list.MaxHeight = double.PositiveInfinity;
        Snapshot(window, Path.Combine(directory, "party-bands-preview.png")); list.MaxHeight = listHeight;
        // The account's characters on the card (W#17), opened.
        Field("altsOpened").SetValue(window, true); Call(window, "RenderHero");
        Snapshot(window, Path.Combine(directory, "characters-preview.png"));
        Field("altsOpened").SetValue(window, false); Call(window, "RenderHero");
        // The tray icon in each state, at the size it is drawn (W#17).
        foreach (var name in new[] { "Stopped", "Listening", "Live", "Stale", "Problem" })
        {
            Call(window, "SetReaderState", Enum.Parse(state, name), name);
            var tray = Field("tray").GetValue(window)!; var icon = tray.GetType().GetProperty("Icon")!.GetValue(tray)!;
            var bitmap = icon.GetType().GetMethod("ToBitmap")!.Invoke(icon, null)!;
            bitmap.GetType().GetMethod("Save", [typeof(string)])!.Invoke(bitmap, [Path.Combine(directory, $"tray-{name.ToLowerInvariant()}.png")]);
        }
        Call(window, "SetReaderState", Enum.Parse(state, "Live"), "Live");
        FadePreview(window, directory);
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
        window.Attribution.Latest = null; Call(window, "RenderHero");
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
    // Synthetic room: your own live character, two live/stale mates and one offline mate. In game,
    // you and Lumina are in a party inside a force, Rin in the force in another party, and two more
    // mates in a party of their own.
    static RoomSnapshot PartySnapshot(bool inGame = false)
    {
        string Ago(int seconds) => DateTime.UtcNow.AddSeconds(-seconds).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        GameGroup? ours = inGame ? new("0a0b0c0d0e0f0001", "0f0e0d0c0b0a0001") : null, theirs = new("0a0b0c0d0e0f0003");
        Character Hero(string id, string user, string name, int value, bool valid, int seen, GameGroup? group = null) => new(id, user, name, "Lumiel", "GLOBAL_EU", value, true, valid, Ago(600), Ago(seen), "packet", GameGroup: group);
        RoomMember[] members = [
            new RoomMember("m1", "u", "owner", "Sample account", [Hero("c1", "u", "TestHero", 390, true, 2, ours)]),
            new RoomMember("m2", "l", "reporter", "Lumina", [Hero("c3", "l", "Lumina", 180, true, 5, ours) with { Level = 45, ClassName = "Chanter" }]),
            new RoomMember("m3", "k", "reporter", "Kaelen", [Hero("c4", "k", "KaelenAlt", 235, false, 10)]),
            new RoomMember("m4", "n", "reporter", "Nyx", [Hero("c5", "n", "Nyx", 400, true, 300)])];
        if (inGame) members = [.. members,
            new RoomMember("m5", "r", "reporter", "Rin", [Hero("c6", "r", "Rin", 220, true, 4, new("0a0b0c0d0e0f0002", ours!.Force)) with { Level = 44, ClassName = "Ranger" }]),
            new RoomMember("m6", "t", "reporter", "Tor", [Hero("c7", "t", "Tor", 310, true, 6, theirs) with { Level = 43, ClassName = "Cleric" }]),
            new RoomMember("m7", "v", "reporter", "Vex", [Hero("c8", "v", "Vex", 95, true, 3, theirs)])];
        return new RoomSnapshot(new Room("r1", "Evening", 40, "", false), members, DateTime.UtcNow.ToString("O"));
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
    sealed class Recorder(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    sealed class MemoryRunKey : IRunKey
    {
        public readonly Dictionary<string, string> Values = new();
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }
    // A packet of a connection the capture does not know looks at the connection table again at
    // most every 100 ms; a SYN-ACK always does (up to 20 times a second), since the look just
    // before it may predate the connection, and missing it would join the connection midstream.
    static void CaptureLooks()
    {
        var capture = new PacketCapture(); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lookedAt = typeof(PacketCapture).GetField("knownFlowsAt", flags)!; var current = typeof(PacketCapture).GetMethod("CurrentFlows", flags)!;
        bool Looked(bool opening)
        {
            var recent = DateTimeOffset.UtcNow.AddMilliseconds(-10); lookedAt.SetValue(capture, recent);
            current.Invoke(capture, [true, opening]); return (DateTimeOffset)lookedAt.GetValue(capture)! != recent;
        }
        Check(!Looked(false), "an unknown connection's packet reuses a look younger than 100 ms");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Check(Enumerable.Range(0, 20).All(_ => Looked(true)), "a SYN-ACK looks at the connection table again");
        Check(clock.Elapsed > TimeSpan.FromMilliseconds(900) || !Looked(true), "at most 20 SYN-ACK looks a second");
    }
    static Window Settings(MainWindow window) => (Window)Field("settings").GetValue(window)!;
    // Fading while the game is in front (W#18), on the desktop: the real card, layered at 60 %, over a
    // stand-in for the game (both pinned on top), captured from the screen around the stand-in only.
    static void FadePreview(MainWindow window, string directory)
    {
        static void Settle() { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); Thread.Sleep(250); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); }
        var scene = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x4A, 0x7A), System.Windows.Media.Color.FromRgb(0xC8, 0x7A, 0x3A), 35);
        var game = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Left = 200, Top = 120, Width = 560, Height = 620, Background = scene,
            Content = new TextBlock { Text = "AION 2", FontSize = 96, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        game.Show();
        window.ShowActivated = false; window.Show(); window.Left = 340; window.Top = 150; window.Topmost = true; Settle();
        Field("gameInFront").SetValue(window, (Func<bool>)(() => true)); Call(window, "SetFadeInGame", true, false); Settle();
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(game).Handle, out var rect);
        var drawing = Type.GetType("System.Drawing.Bitmap, System.Drawing.Common")!; var graphics = Type.GetType("System.Drawing.Graphics, System.Drawing.Common")!;
        var size = Activator.CreateInstance(Type.GetType("System.Drawing.Size, System.Drawing.Primitives")!, rect.Right - rect.Left, rect.Bottom - rect.Top)!;
        var bitmap = Activator.CreateInstance(drawing, rect.Right - rect.Left, rect.Bottom - rect.Top)!;
        var g = graphics.GetMethod("FromImage")!.Invoke(null, [bitmap])!;
        graphics.GetMethod("CopyFromScreen", [typeof(int), typeof(int), typeof(int), typeof(int), size.GetType()])!.Invoke(g, [rect.Left, rect.Top, 0, 0, size]);
        drawing.GetMethod("Save", [typeof(string)])!.Invoke(bitmap, [Path.Combine(directory, "faded-preview.png")]);
        ((IDisposable)g).Dispose(); ((IDisposable)bitmap).Dispose();
        Call(window, "SetFadeInGame", false, false); window.Topmost = false; window.Hide(); window.ShowActivated = true; game.Close();
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct ScreenRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out ScreenRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    // The layered state and opacity of a window (W#18).
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr window, out uint key, out byte alpha, out uint flags);
    static bool Layered(IntPtr window) => (GetWindowLong(window, -20) & 0x80000) != 0;
    static byte Alpha(IntPtr window) => GetLayeredWindowAttributes(window, out _, out var alpha, out _) ? alpha : (byte)0;
    static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    // What the window's connection to the server answered and refused (#92).
    static ConnectionState Conn(MainWindow window) => (ConnectionState)Field("connection").GetValue(window)!;
    static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static IEnumerable<DependencyObject> Tree(DependencyObject at) { yield return at; foreach (var child in LogicalTreeHelper.GetChildren(at)) if (child is DependencyObject next) foreach (var below in Tree(next)) yield return below; }
    static IEnumerable<DependencyObject> Visuals(DependencyObject at) { yield return at; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++) foreach (var below in Visuals(VisualTreeHelper.GetChild(at, i))) yield return below; }
    // A failed check is reported and the scenario goes on; an exception ends the scenario (RunAsync).
    static int checks;
    static string scenarioName = "";
    static readonly List<string> failures = [];
    static void Check(bool condition, string message) { checks++; if (!condition) Fail(message); }
    static void Fail(string message)
    {
        failures.Add($"[{scenarioName}] {message}");
        Console.Error.WriteLine($"UI check failed: [{scenarioName}] {message}");
    }
    static async Task Until(Func<bool> condition, int attempts)
    {
        for (var i = 0; i < attempts && !condition(); i++) await Task.Delay(25);
        Check(condition(), "waited condition holds");
    }
}
