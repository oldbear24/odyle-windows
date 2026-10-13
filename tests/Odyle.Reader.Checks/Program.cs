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
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(false);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (args.Length == 2 && args[0] == "--screenshots") { Screenshots(window, args[1]); return; }
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
                // A decoded reading is displayed immediately, before its owner is known or saved.
                var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 0, false, false, "") }); Field("currentCharacterId").SetValue(window, "c1");
                Call(window, "OnCandidate", inert, new PacketCandidate(390, 7, ExperimentalOdyleDecoder.Version) { Primary = 0, Bonus = 390 });
                Check(Control<TextBlock>(window, "ValueLabel").Text == "390", "decoded reading is shown in the value card");
                Check(Control<TextBlock>(window, "CubeLabel").Text.StartsWith("9 cubes"), "cube count derives from the reading");
                Check(Control<TextBlock>(window, "StateText").Text == "Sending", "unsent reading is marked as sending");
                Check(Control<TextBlock>(window, "HeroTitle").Text == "Detecting character…", "an unattributed reading is never shown under a character's name");
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
            finally { try { await (Task)Call(window, "QuitAsync")!; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }
        });
        Dispatcher.Run();
    }
    static void Screenshots(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = Settings(window);
        Control<TextBlock>(window, "StatusLabel").Text = "Pair the app in settings to start.";
        Snapshot(window, Path.Combine(directory, "connect-preview.png"));
        Snapshot(settings, Path.Combine(directory, "settings-connect-preview.png"), 320);
        Field("accountName").SetValue(window, "Sample account");
        Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090"));
        var stamp = DateTime.UtcNow.AddSeconds(-40).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, stamp), new Character("c2", "u", "TestAlt", "Lumiel", "GLOBAL_EU", 120, true, false, stamp) });
        Field("rooms").SetValue(window, new[] { new Room("r1", "The evening expedition", 40, "owner", true, ["c1"]), new Room("r2", "Weekend raid", 30, "reporter", true, ["c1", "c2"]) });
        Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { PartySnapshot() });
        ((TextBlock)settings.FindName("AccountDetail")).Text = "Signed in as Sample account via Discord · odyle.example";
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
        Field("latest").SetValue(window, null); Call(window, "RenderHero");
        Call(window, "SetReaderState", Enum.Parse(state, "Midstream"), "Started after login · change channel or relog once so the game resends Odyle.");
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
