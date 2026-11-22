using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Odyle.Core;
using Odyle.Reader;

// What the player sees when things go wrong (#96): the server ends the pairing (401) or asks the app to wait (429),
// the operators hold a version back or ask every app to update now, quitting with a reader that will not stop, and
// the Discord status's own failures.
static partial class ReaderChecks
{
    static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var answer = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) answer.Headers.Add("Retry-After", retryAfter);
        return answer;
    }
    const string SignedIn = """{"token":"check-token","record":{"id":"u","displayName":"Player"},"deviceKey":"device-key"}""";
    // A connected window as after ConnectAsync: the pairing saved, a session, the heartbeat running.
    static PartyApi Connect(MainWindow window, HttpMessageHandler server)
    {
        SessionStore.Save(new SavedSession("https://party.example", "check-token", "device-key"));
        var api = new PartyApi("https://party.example", "check-token", "device-key", server);
        Call(window, "UseApi", api); Call(window, "ShowConnected", true);
        window.Link.SessionId = "s1"; window.Link.StartHeartbeat(DateTimeOffset.UtcNow);
        return api;
    }
    // The heartbeat's 401: only the server's own answer ends the pairing (#96). A 401 from something in front of the
    // server keeps it and starts a new session; the server's own unpairs this PC, forgets the saved pairing and says
    // so, in the tray too while the window is hidden.
    static async Task HeartbeatUnpairedScenario(MainWindow window)
    {
        var settings = Settings(window); var own = false; var heartbeats = 0;
        Connect(window, new Answering(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/odyle/heartbeat") heartbeats++;
            return request.RequestUri.AbsolutePath switch
            {
                "/api/odyle/heartbeat" or "/api/odyle/desktop-sign-in" when own => Json(HttpStatusCode.Unauthorized, """{"status":401,"message":"The device is not paired."}"""),
                "/api/odyle/heartbeat" or "/api/odyle/desktop-sign-in" => Json(HttpStatusCode.Unauthorized, "<html>Sign in to the proxy</html>"),
                _ => Json(HttpStatusCode.OK, "{}"),
            };
        }));
        await (Task)Call(window, "PulseAsync")!;
        Check(heartbeats == 1 && window.Link.Api is not null && File.Exists(SessionStore.FilePath) && window.Link.NeedsReconnect && window.Link.Heartbeat.IsEnabled
            && Control<TextBlock>(window, "StatusLabel").Text == "The server could not be reached: something in front of it answered 401.", "a 401 from something in front of the server keeps the pairing: " + Control<TextBlock>(window, "StatusLabel").Text);
        own = true; window.Link.NeedsReconnect = false; ClearNotice(window);
        await (Task)Call(window, "PulseAsync")!;
        Check(window.Link.Api is null && !File.Exists(SessionStore.FilePath) && !window.Link.Heartbeat.IsEnabled && window.Link.Endpoint == "" && window.Link.RetryAt is null,
            "the server's own 401 unpairs this PC and forgets the saved pairing");
        Check(Control<TextBlock>(window, "StatusLabel").Text == "This PC is no longer paired with your account. Copy a new connection link from the dashboard." && Notice(window) is { Title: "Odyle is no longer paired" }
            && ((Button)settings.FindName("RetryButton")).Visibility == Visibility.Collapsed && ((FrameworkElement)settings.FindName("JoinPanel")).Visibility == Visibility.Visible && (string)Control<Button>(window, "ReportButton").Content == "Connect",
            "the window and the tray say so, and pairing is offered again");
    }
    // The server asks the app to wait (429): the footer counts down, nothing is sent meanwhile, and once the wait is over
    // the next heartbeat clears the line. A saved pairing a busy server could not confirm waits as long as it asks.
    static async Task ServerBusyScenario(MainWindow window)
    {
        var busy = true; var heartbeats = 0;
        var server = new Answering(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/odyle/heartbeat") heartbeats++;
            return request.RequestUri.AbsolutePath switch
            {
                "/api/odyle/heartbeat" when busy => Json(HttpStatusCode.TooManyRequests, """{"status":429,"message":"Too Many Requests."}""", "2"),
                "/api/odyle/heartbeat" => Json(HttpStatusCode.OK, """{"serverNow":"2026-11-22 12:00:00.000Z"}"""),
                "/api/odyle/desktop-sign-in" => Json(HttpStatusCode.TooManyRequests, """{"status":429,"message":"Too Many Requests."}""", "120"),
                _ => Json(HttpStatusCode.OK, "{}"),
            };
        });
        var api = Connect(window, server); var status = Control<TextBlock>(window, "StatusLabel");
        await (Task)Call(window, "PulseAsync")!;
        Check(heartbeats == 1 && api.BusyUntil > DateTimeOffset.UtcNow && window.Link.LastTick == api.BusyUntil && System.Text.RegularExpressions.Regex.IsMatch(status.Text, @"^Server busy · retrying in [12] s\.$") && window.Link.Api is not null && !window.Link.NeedsReconnect,
            "a 429 keeps the session and the footer counts down: " + status.Text);
        await (Task)Call(window, "PulseAsync")!;
        Check(heartbeats == 1, "while the server asks to wait the heartbeat sends nothing");
        await Task.Delay(api.BusyUntil - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100)); Call(window, "RenderBusy");
        Check(status.Text == "Server busy · retrying now.", "once the wait is over the footer says it tries again: " + status.Text);
        busy = false; await (Task)Call(window, "PulseAsync")!;
        Check(heartbeats == 2 && status.Text == "", "the next heartbeat that goes through clears the line: " + status.Text);
        // A start while the server is busy: the saved pairing is kept and tried again when the server allows it.
        await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
        SessionStore.Save(new SavedSession("https://party.example", "check-token", "device-key"));
        Field("newApi").SetValue(window, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, server))); Npcap.Probe = () => false;
        await (Task)Call(window, "RestoreAsync")!;
        ((System.Windows.Threading.DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop();
        Check(window.Link.Offline && window.Link.RetryProblem == "busy" && window.Link.RetryAt > DateTimeOffset.UtcNow.AddSeconds(100) && File.Exists(SessionStore.FilePath) && status.Text == "Server busy · retrying in 2 min.",
            "a busy server at start keeps the pairing and waits as long as it asks: " + status.Text);
    }
    // Updates the operators steer (#19, #96): a downloaded version they hold back is dropped, and a build they hold back
    // goes back to the latest release at once.
    static async Task HeldBackScenario(MainWindow window)
    {
        var updates = window.Updates; var folder = Path.GetTempPath(); var forgotten = new List<Version[]>(); var applied = new List<Version>(); var asked = 0;
        AppRelease? latest = null;
        updates.IsRelease = () => true; updates.FindStaged = () => null; updates.Forget = held => forgotten.Add(held.ToArray());
        updates.Check = (held, _) => { asked++; return Task.FromResult(latest); };
        updates.Download = (release, _, _) => Task.FromResult(new StagedUpdate(release.Version, folder, release.Url));
        updates.Apply = update => applied.Add(update.Version);
        var newer = new Version(99, 0, 0); updates.Staged = new StagedUpdate(newer, folder, "https://github.com/oldbear24/odyle-windows/releases"); Call(window, "RenderUpdate");
        Check(Control<Button>(window, "UpdateButton").Visibility == Visibility.Visible, "a downloaded update is offered");
        await updates.HeldBackAsync([newer]);
        Check(updates.Staged is null && forgotten.Count == 1 && forgotten[0].SequenceEqual([newer]) && asked == 0 && Control<Button>(window, "UpdateButton").Visibility == Visibility.Collapsed,
            "a downloaded version the server holds back is dropped and no longer offered");
        // This build held back: the latest release, older than it, is looked for and downloaded at once.
        var older = new Version(0, 1, 0); latest = new AppRelease(older, "0.1.0", "https://github.com/oldbear24/odyle-windows/releases/tag/0.1.0", null, []);
        await updates.HeldBackAsync([Updater.Current]);
        Check(asked == 1 && updates.Staged?.Version == older, "a build the server holds back looks for the latest release at once and downloads it");
    }
    // "Update all apps now" (#96): a build that is current does nothing; one found later than the start's check waits
    // and is told once; asked to update now, a downloaded update installs at once, and a failed copy says so and stays.
    static async Task UpdateNowScenario(MainWindow window)
    {
        var updates = window.Updates; var folder = Path.GetTempPath(); var applied = new List<Version>(); var asked = 0; var failCopy = true;
        var newer = new Version(99, 0, 0);
        updates.IsRelease = () => true; updates.FindStaged = () => null; updates.Forget = _ => { };
        updates.Check = (_, _) => { asked++; return Task.FromResult<AppRelease?>(new AppRelease(newer, "99.0.0", "https://github.com/oldbear24/odyle-windows/releases/tag/99.0.0", null, [])); };
        updates.Download = (release, _, _) => Task.FromResult(new StagedUpdate(release.Version, folder, release.Url));
        updates.Apply = update => { if (failCopy) throw new IOException("The file is in use."); applied.Add(update.Version); };
        await updates.UpdateNowAsync(new UpdateRequest(Updater.Current));
        Check(asked == 0 && updates.Staged is null, "a build that is up to date ignores the request to update now");
        // The start's check is over: what a later check finds waits, and the player hears of it once.
        updates.Schedule.Reached(DateTimeOffset.UtcNow, upToDate: true); ClearNotice(window);
        await updates.CheckAsync(manual: false);
        Check(asked == 1 && updates.Staged?.Version == newer && applied.Count == 0 && Notice(window) is { Title: "Odyle 99.0.0 is ready" } && PreferencesStore.Load().NotifiedUpdate == newer.ToString(),
            "an update found later waits for a restart and is told once");
        Check(((TextBlock)Settings(window).FindName("UpdateLabel")).Text.Contains("Odyle 99.0.0 is downloaded and verified"), "Settings offer it");
        Ended = null;
        await updates.UpdateNowAsync(new UpdateRequest(newer));
        Check(Ended is null && applied.Count == 0 && updates.Staged is not null && Control<TextBlock>(window, "StatusLabel").Text == "Could not install the update: The file is in use. Download it from the release page instead.",
            "a copy that fails says so and keeps the download: " + Control<TextBlock>(window, "StatusLabel").Text);
        failCopy = false;
        await updates.UpdateNowAsync(new UpdateRequest(null));
        Check(applied.SequenceEqual([newer]) && Ended == (true, true) && asked == 1, "asked to update now, the downloaded update installs at once and Odyle restarts");
    }
    // Quitting (#96): a capture that will not stop holds the quit for at most 4 seconds.
    static async Task QuitCapScenario(MainWindow window)
    {
        var gate = new TaskCompletionSource(); var invalidations = 0;
        Connect(window, new Responding(async request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/odyle/invalidate") return Json(HttpStatusCode.OK, "{}");
            invalidations++; await gate.Task; return Json(HttpStatusCode.OK, "{}");
        }));
        Field("capture").SetValue(window, new PacketCapture()); Field("activeCharacterId").SetValue(window, "c1"); Ended = null;
        var clock = Stopwatch.StartNew();
        await (Task)Call(window, "QuitAsync", false)!;
        clock.Stop(); gate.SetResult();
        Check(invalidations == 1 && clock.Elapsed >= TimeSpan.FromSeconds(3.9) && clock.Elapsed < TimeSpan.FromSeconds(8) && Ended == (false, true), $"a reading that cannot be marked stale holds quitting for 4 seconds at most ({clock.Elapsed.TotalSeconds:0.0} s)");
    }
    // A restart hands the live character to the next start instead of marking it stale.
    static async Task QuitRestartScenario(MainWindow window)
    {
        var invalidations = 0;
        Connect(window, new Answering(request => { if (request.RequestUri!.AbsolutePath == "/api/odyle/invalidate") invalidations++; return Json(HttpStatusCode.OK, "{}"); }));
        Field("capture").SetValue(window, new PacketCapture()); Field("activeCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u"); Field("currentCharacterId").SetValue(window, "c1");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, "") });
        window.Attribution.GameKey = window.Attribution.DetectedKey = window.Attribution.OwnerKey = "TestHero|2304"; window.Attribution.Latest = new PacketCandidate(390, ExperimentalOdyleDecoder.Version);
        Ended = null;
        await (Task)Call(window, "QuitAsync", true)!;
        Check(Ended == (true, true) && invalidations == 0 && HandoverStore.Take(DateTimeOffset.UtcNow) is { CharacterId: "c1", Total: 390 }, "a restart hands the live character over and does not mark it stale");
    }
    // A quit that comes while an update was copied in restarts into the new build.
    static async Task QuitAfterInstallScenario(MainWindow window)
    {
        window.Updates.Installed = true; Ended = null;
        await (Task)Call(window, "QuitAsync", false)!;
        Check(Ended == (true, true), "a quit after an update was installed restarts into it");
    }
    // The Discord status on its own (#96), against a stand-in for Discord's pipe (never Discord's own): it connects and
    // shows the status, sends a change at most every 15 seconds, connects again after Discord closed its pipe, and stops
    // trying an application Discord turned down.
    static async Task DiscordStatusScenario()
    {
        var prefix = "odyle-check-discord-" + Guid.NewGuid().ToString("N") + "-";
        var fake = new FakeDiscord(prefix + "0");
        var status = new DiscordStatus { PipePrefix = prefix, SendEvery = TimeSpan.FromSeconds(15), LookEvery = TimeSpan.FromSeconds(30) };
        try
        {
            var first = new DiscordActivity("TestHero · 390 Odyle", "9 cubes", null); var second = first with { Details = "TestHero · 405 Odyle" };
            status.Show("100000000000000001", first);
            await Until(() => status.Now == DiscordStatus.State.Shown && fake.Activities.Count == 1, 200);
            Check(fake.Handshakes == 1 && fake.Activities[0].Contains("390 Odyle") && fake.Activities[0].Contains($"\"pid\":{Environment.ProcessId}"), "the status is shown for Odyle's own process");
            status.Show("100000000000000001", second); await Task.Delay(200);
            Check(fake.Activities.Count == 1, "a change within 15 seconds of the last waits");
            status.SendEvery = TimeSpan.FromMilliseconds(100); await Task.Delay(150); status.Show("100000000000000001", second);
            await Until(() => fake.Activities.Count == 2, 200);
            Check(fake.Activities[1].Contains("405 Odyle"), "after the wait the change is sent");
            // Discord quits: the status turns not running, Discord is looked for again after the wait, and found again.
            fake.Drop();
            await Until(() => status.Now == DiscordStatus.State.NotRunning, 200);
            status.Show("100000000000000001", first); await Task.Delay(200);
            Check(status.Now == DiscordStatus.State.NotRunning, "a Discord that is gone is not looked for again before the wait");
            fake.Listen(); status.LookEvery = TimeSpan.Zero; status.Show("100000000000000001", first);
            await Until(() => status.Now == DiscordStatus.State.Shown && fake.Activities.Count == 3, 200);
            Check(fake.Handshakes == 2, "once Discord is back the status connects again and is shown");
            status.Show("100000000000000001", null);
            await Until(() => status.Now == DiscordStatus.State.Off, 200);
            Check(status.Now == DiscordStatus.State.Off, "clearing the status closes the pipe");
            // Discord turns the application down: said once, and that application is not tried again.
            fake.Refuse = true; fake.Listen(); var refused = new DiscordStatus { PipePrefix = prefix, LookEvery = TimeSpan.Zero };
            refused.Show("123", first);
            await Until(() => refused.Now == DiscordStatus.State.Refused, 200);
            Check(refused.Problem == "Invalid Client ID" && fake.Handshakes == 3, "a status Discord turns down says why: " + refused.Problem);
            refused.Show("123", second); await Task.Delay(300);
            Check(fake.Handshakes == 3 && refused.Now == DiscordStatus.State.Refused, "and the application Discord turned down is not tried again");
            refused.Dispose();
        }
        finally { status.Dispose(); fake.Dispose(); }
    }
    // A stand-in for the Discord app's pipe: answers the handshake with READY (or, while Refuse, closes with an error)
    // and records each status it is sent. Listen takes the next connection; Drop closes the current one.
    sealed class Responding(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => answer(request);
    }
    static (string Title, string Text)? Notice(MainWindow window) => window.LastNotice;
    static void ClearNotice(MainWindow window) => window.LastNotice = null;
    sealed class FakeDiscord : IDisposable
    {
        readonly string name;
        NamedPipeServerStream? pipe;
        public readonly List<string> Activities = [];
        public int Handshakes;
        public bool Refuse;
        public FakeDiscord(string name) { this.name = name; Listen(); }
        public void Listen()
        {
            pipe?.Dispose();
            var next = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe = next;
            _ = Task.Run(async () =>
            {
                try
                {
                    await next.WaitForConnectionAsync();
                    while (await DiscordPresence.ReadFrameAsync(next, CancellationToken.None) is { } frame)
                    {
                        if (frame.Opcode == DiscordPresence.Handshake)
                        {
                            Interlocked.Increment(ref Handshakes);
                            var answer = Refuse ? DiscordPresence.Frame(DiscordPresence.Close, """{"code":4000,"message":"Invalid Client ID"}""") : DiscordPresence.Frame(DiscordPresence.Message, """{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}""");
                            await next.WriteAsync(answer); await next.FlushAsync();
                            // Refused: the pipe stays open for the app to read the answer and close it.
                            if (Refuse) return;
                        }
                        else if (frame.Opcode == DiscordPresence.Message) lock (Activities) Activities.Add(frame.Json);
                    }
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
        public void Drop() { var old = pipe; pipe = null; old?.Dispose(); }
        public void Dispose() => Drop();
    }
}
