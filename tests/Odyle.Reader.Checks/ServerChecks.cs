using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Odyle.Core;
using Odyle.Reader;

// What 1.0.0 needs of the server (#93): dashboard 1.0.0 or later, which names its version. An older server keeps
// working with a notice in the footer and in Settings, and a pairing saved before device keys pairs again.
static partial class ReaderChecks
{
    const string OlderServerNotice = "This server runs an older Odyle; ask its operator to update.";
    // The log written since start (the checks share one log).
    static string LogSince(int start) { var text = File.ReadAllText(Log.FilePath); return start <= text.Length ? text[start..] : text; }
    static int LogLength() => File.Exists(Log.FilePath) ? File.ReadAllText(Log.FilePath).Length : 0;

    // The notice for a server that names no version, one that names an older one and one that names 1.0.0 or later,
    // also when the server is updated while the app runs; it never stops the app from working.
    static async Task ServerVersionScenario(MainWindow window)
    {
        var server = new PairingServer(); var settings = Settings(window);
        var status = Control<TextBlock>(window, "StatusLabel"); var label = (TextBlock)settings.FindName("ServerNoticeLabel");
        var probeNpcap = Npcap.Probe; Npcap.Probe = () => false;
        var makeApi = Field("newApi").GetValue(window);
        Field("newApi").SetValue(window, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, server)));
        bool Shown() => status.Text == OlderServerNotice && label.Text == OlderServerNotice && label.Visibility == Visibility.Visible;
        bool Hidden() => status.Text != OlderServerNotice && label.Visibility == Visibility.Collapsed;
        Task Pulse() => (Task)Call(window, "PulseAsync")!;
        try
        {
            // A dashboard before 1.0.0 names no version: the app connects and works, and says the server is older.
            var log = LogLength();
            SessionStore.Save(new SavedSession("https://party.example", "pairing-check-token", "pairing-check-key"));
            await (Task)Call(window, "RestoreAsync")!;
            Check(window.Link.Api is not null && window.Link.SessionId == "s1" && window.Link.Heartbeat.IsEnabled && Shown(), "a server that names no version keeps working, with the notice in the footer and Settings: " + status.Text);
            Check(LogSince(log).Contains("The server names no version: it runs a dashboard older than 1.0.0"), "the log says the server names no version");
            await Pulse(); var noted = LogSince(log);
            Check(Shown() && noted.IndexOf("names no version", StringComparison.Ordinal) == noted.LastIndexOf("names no version", StringComparison.Ordinal), "the version is logged once per connection, not at every heartbeat");
            // One that names an older version: the same notice, and the version in the log.
            log = LogLength(); server.Heartbeat = """{"serverNow":"2026-11-22 12:00:00.000Z","serverVersion":"0.67.1"}""";
            await Pulse();
            Check(Shown() && LogSince(log).Contains("The server runs dashboard 0.67.1, older than 1.0.0"), "a server on dashboard 0.67.1 gets the notice too");
            // A status line wins over the notice; the notice comes back once the footer is free.
            Call(window, "Status", "Could not save the detected character: Server returned 500.");
            server.Heartbeat = """{"serverNow":"2026-11-22 12:00:20.000Z","serverVersion":"0.99.0"}"""; await Pulse();
            Check(status.Text == "Could not save the detected character: Server returned 500." && label.Visibility == Visibility.Visible, "a status line keeps the footer; Settings still says it");
            Call(window, "Status", "");
            Check(Shown(), "the notice comes back once the footer says nothing else");
            // The server is updated to dashboard 1.0.0 while the app runs: the notice goes within one heartbeat.
            log = LogLength(); server.Heartbeat = """{"serverNow":"2026-11-22 12:00:40.000Z","serverVersion":"1.0.0"}""";
            await Pulse();
            Check(Hidden() && status.Text == "" && LogSince(log).Contains("The server runs dashboard 1.0.0."), "a server updated to dashboard 1.0.0 drops the notice within one heartbeat: " + status.Text);
            // Disconnected, nothing is said about a server.
            await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
            Check(Hidden(), "no notice once disconnected");
            // A server on dashboard 1.0.0 or later from the start shows none.
            server.Version = "1.2.0"; server.Heartbeat = """{"serverNow":"2026-11-22 12:01:00.000Z","serverVersion":"1.2.0"}""";
            SessionStore.Save(new SavedSession("https://party.example", "pairing-check-token", "pairing-check-key"));
            await (Task)Call(window, "RestoreAsync")!;
            Check(window.Link.Api is not null && Hidden() && status.Text == "", "a server on dashboard 1.2.0 shows no notice: " + status.Text);
        }
        finally
        {
            await (Task)Call(window, "DisconnectAsync", "Disconnected.", true)!;
            ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop(); Npcap.Probe = probeNpcap;
            Field("newApi").SetValue(window, makeApi);
        }
    }

    // Previews of what 1.0.0 shows about the server, for the owner's look ("--server-screenshots <dir>"): the main window
    // and Settings on a server before dashboard 1.0.0 (with the notice) and on a current one (without), in English and
    // Czech, and a pairing saved before device keys, in English.
    static async Task ServerScreenshots(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var language in new[] { "en", "cs" })
        {
            Localizer.Use(language);
            try
            {
                foreach (var (version, name) in new[] { ((string?)null, "old-server"), ("1.0.0", "current-server") })
                {
                    var window = NewWindow();
                    try
                    {
                        await ConnectedPreview(window, version);
                        Snapshot(window, Path.Combine(directory, $"{name}-window-{language}.png"));
                        Snapshot(Settings(window), Path.Combine(directory, $"{name}-settings-{language}.png"), 320);
                    }
                    finally { await Retire(window); }
                }
            }
            finally { Localizer.Use("en"); }
        }
        // A pairing saved before device keys: the window asks to pair again, and Settings offers pairing with the address kept.
        var keyless = NewWindow(); var probeNpcap = Npcap.Probe; Npcap.Probe = () => false;
        try
        {
            Field("newApi").SetValue(keyless, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, new PairingServer())));
            SessionStore.Save(new SavedSession("https://odyle.example", "token-from-before-device-keys"));
            await (Task)Call(keyless, "RestoreAsync")!;
            ((DispatcherTimer)Field("npcapTimer").GetValue(keyless)!).Stop();
            ((Expander)Settings(keyless).FindName("ServerOptions")).IsExpanded = true;
            Snapshot(keyless, Path.Combine(directory, "keyless-window-en.png"));
            Snapshot(Settings(keyless), Path.Combine(directory, "keyless-settings-en.png"), 320);
        }
        finally { Npcap.Probe = probeNpcap; await Retire(keyless); }
        Console.WriteLine("Rendered the server previews to " + directory);
    }
    // A connected window reading a live character, as the screenshots show it, on a server that names version (null:
    // a dashboard before 1.0.0): the session answer says it, and the window notes it as after connecting.
    static async Task ConnectedPreview(MainWindow window, string? version)
    {
        var settings = Settings(window);
        var api = new PartyApi("https://odyle.example", "token", "key", new PairingServer { Version = version });
        Call(window, "UseApi", api); await api.StartSessionAsync();
        Field("accountName").SetValue(window, "Sample account");
        var stamp = DateTime.UtcNow.AddSeconds(-40).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, stamp), new Character("c2", "u", "TestAlt", "Lumiel", "GLOBAL_EU", 120, true, false, stamp) });
        Field("rooms").SetValue(window, new[] { new Room("r1", "The evening expedition", 40, "owner", true, ["c1"]), new Room("r2", "Weekend raid", 30, "reporter", true, ["c1", "c2"]) });
        Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { PartySnapshot() });
        ((TextBlock)settings.FindName("AccountDetail")).Text = Localizer.T("Signed in as {0} via Discord · {1}", "Sample account", "odyle.example");
        ((TextBlock)settings.FindName("UpdateLabel")).Text = Localizer.T("{0} · installs new releases at every start and looks for them once a day.", Localizer.T("Odyle {0}", "1.0.0"));
        ((Button)settings.FindName("CheckUpdateButton")).Visibility = Visibility.Visible;
        Call(window, "ShowConnected", true); Call(window, "RenderRooms");
        var inert = new PacketCapture(); Field("capture").SetValue(window, inert); Call(window, "RenderReaderButton");
        window.Attribution.DetectedKey = "TestHero|2304"; window.Attribution.GameKey = "TestHero|2304";
        Call(window, "OnCandidate", inert, new PacketCandidate(390, OdyleDecoder.Version) { Base = 0, Bonus = 390 });
        window.Attribution.OwnerKey = "TestHero|2304"; Field("activeCharacterId").SetValue(window, "c1");
        var state = typeof(MainWindow).GetNestedType("ReaderState", System.Reflection.BindingFlags.NonPublic)!;
        Call(window, "SetReaderState", Enum.Parse(state, "Live"), Localizer.T("Live"));
        ((DispatcherTimer)Field("publishDelay").GetValue(window)!).Stop();
        Call(window, "NoteServerVersion"); Call(window, "Status", "");
        Call(window, "RenderHero"); Call(window, "RenderParty");
    }

    // A pairing saved before device keys (Oct 2026) cannot sign in once its token ends, and dashboard 1.0.0 refuses it:
    // the app treats this PC as unpaired, asks nothing of the server, keeps its address and asks to pair again.
    static async Task KeylessSessionScenario(MainWindow window)
    {
        var settings = Settings(window); var requests = 0;
        var server = new Answering(_ => { requests++; return Json(HttpStatusCode.OK, """{"token":"t","record":{"id":"u","displayName":"Player"}}"""); });
        var probeNpcap = Npcap.Probe; Npcap.Probe = () => false;
        var makeApi = Field("newApi").GetValue(window);
        Field("newApi").SetValue(window, (Func<string, string?, string?, PartyApi>)((endpoint, token, key) => new PartyApi(endpoint, token, key, server)));
        try
        {
            var log = LogLength();
            SessionStore.Save(new SavedSession("https://old.example", "token-from-before-device-keys"));
            await (Task)Call(window, "RestoreAsync")!;
            Check(requests == 0 && window.Link.Api is null && window.Link.RetryAt is null && !window.Link.RetryTimer.IsEnabled && window.Link.Endpoint == "" && !File.Exists(SessionStore.FilePath),
                "a saved pairing without a device key is removed without asking the server, and nothing is retried");
            Check(Control<TextBlock>(window, "StatusLabel").Text == "This PC was paired with an older Odyle and needs to pair again. Copy a new connection link from the dashboard."
                && ((FrameworkElement)settings.FindName("JoinPanel")).Visibility == Visibility.Visible && ((Button)settings.FindName("RetryButton")).Visibility == Visibility.Collapsed
                && ((TextBox)settings.FindName("EndpointInput")).Text == "https://old.example", "the window asks to pair again, with the server's address kept");
            Check(Field("capture").GetValue(window) is null && (string)Control<Button>(window, "ReportButton").Content == "Connect" && LogSince(log).Contains("no device key") && !LogSince(log).Contains("token-from-before-device-keys"),
                "unpaired, the reader does not start, and the log says why without the token");
            // A start in the tray then opens the window and Settings, as for any unpaired PC.
            Check(StartupCommand.ShowsWindow(tray: true, paired: window.Link.Api is not null || window.Link.RetryAt is not null), "a start with Windows shows the window to pair again");
            // The next start finds nothing saved and stays quiet.
            await (Task)Call(window, "RestoreAsync")!;
            Check(requests == 0 && Control<TextBlock>(window, "StatusLabel").Text.StartsWith("This PC was paired with an older Odyle", StringComparison.Ordinal), "nothing is left to restore");
        }
        finally
        {
            ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); Npcap.Probe = probeNpcap;
            Field("newApi").SetValue(window, makeApi);
        }
    }
}
