using System.IO;
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
                var reader = Control<StackPanel>(window, "ReaderPanel"); reader.Visibility = Visibility.Visible; reader.IsEnabled = true;
                Control<StackPanel>(window, "JoinPanel").Visibility = Visibility.Collapsed;
                Call(window, "BeginCharacterDraft", false);
                Check(Control<Button>(window, "ReportButton").IsEnabled, "empty account can start automatic detection");
                Check(!Control<Expander>(window, "CharacterDetails").IsExpanded, "automatic detection does not require typing");
                Call(window, "BeginCharacterDraft", true);
                Check(Control<Button>(window, "SaveCharacterButton").Visibility == Visibility.Visible, "manual fallback exposes Save character");
                var root = (FrameworkElement)window.Content;
                root.Width = 344; root.Height = 480; root.Measure(new Size(344, 480)); root.Arrange(new Rect(0, 0, 344, 480)); root.UpdateLayout();
                var scroll = Control<ScrollViewer>(window, "Scroller");
                Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "small window has no horizontal overflow");
                var region = Control<ComboBox>(window, "RegionInput"); region.ApplyTemplate();
                Check(region.Template.FindName("SelectorButton", region) is not null, "dark selector template applied");
                var fieldColor = ((SolidColorBrush)region.Background).Color;
                Check(fieldColor.R < 40 && fieldColor.G < 40 && fieldColor.B < 40, "selector has a dark field background");
                Check(Control<TextBlock>(window, "ValueLabel").Text == "—", "no value is shown before a reading");
                // A decoded reading is displayed immediately, even before the backend confirms it.
                var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
                Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090")); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MaxValue);
                Control<ComboBox>(window, "CharactersInput").ItemsSource = new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 0, false, false, "") };
                Field("loading").SetValue(window, true); Control<ComboBox>(window, "CharactersInput").SelectedIndex = 0; Field("loading").SetValue(window, false);
                Field("addingCharacter").SetValue(window, false);
                Call(window, "OnCandidate", inert, new PacketCandidate(390, 7, ExperimentalOdyleDecoder.Version) { Primary = 0, Bonus = 390 });
                Check(Control<TextBlock>(window, "ValueLabel").Text == "390", "decoded reading is shown in the value card");
                Check(Control<TextBlock>(window, "CubeLabel").Text.StartsWith("9 cubes"), "cube count derives from the reading");
                Check(Control<TextBlock>(window, "StateText").Text == "Sending", "unsent reading is marked as sending");
                Field("capture").SetValue(window, null); Field("api").SetValue(window, null); Field("publishBlockedUntil").SetValue(window, DateTimeOffset.MinValue);
                if (args.Length == 2)
                {
                    var api = new PartyApi(args[0]); var auth = await api.PairAsync(args[1]);
                    Field("api").SetValue(window, api);
                    await (Task)Call(window, "ConnectAsync", auth)!;
                    Check((await api.GetCharactersAsync()).Length == 0, "test account starts empty");
                    await Click(window, "Switch_Click");
                    Control<TextBox>(window, "CharacterInput").Text = "Main";
                    Control<TextBox>(window, "ServerInput").Text = "Lumiel";
                    await Click(window, "SaveCharacter_Click");
                    var first = (await api.GetCharactersAsync()).Single();
                    Check(!first.Confirmed && !first.Valid && first.LastReadingAt == "", "save does not fabricate an Odyle reading");
                    Check(((Character)Control<ComboBox>(window, "CharactersInput").SelectedItem).Id == first.Id, "saved character is selected");
                    await Click(window, "Switch_Click");
                    Check(Control<TextBox>(window, "ServerInput").Text == "Lumiel", "adding an alt retains server");
                    Control<TextBox>(window, "CharacterInput").Text = "Alt";
                    await Click(window, "SaveCharacter_Click");
                    Check((await api.GetCharactersAsync()).Length == 2, "second character saved");
                    await Click(window, "Switch_Click");
                    await Click(window, "SaveCharacter_Click");
                    Check((bool)Field("addingCharacter").GetValue(window)! && !(bool)Field("needsReconnect").GetValue(window)!, "validation preserves the editable draft and connection");
                    Check(Control<Button>(window, "SaveCharacterButton").IsEnabled, "validation re-enables Save");
                    await Click(window, "CancelCharacter_Click");
                    Check(Control<ComboBox>(window, "CharactersInput").SelectedItem is Character, "cancel restores selected character");
                    await Click(window, "Switch_Click");
                    Control<TextBox>(window, "CharacterInput").Text = "Main";
                    await Click(window, "SaveCharacter_Click");
                    Check((await api.GetCharactersAsync()).Length == 2, "saving an existing identity never duplicates it");
                    var inertCapture = new PacketCapture(); Field("capture").SetValue(window, inertCapture);
                    var detected = new GameIdentity(42, "DetectedHero", 2304, "Lumiel", "GLOBAL_EU");
                    await (Task)Call(window, "SaveDetectedCharacterAsync", inertCapture, detected)!;
                    var automatic = (await api.GetCharactersAsync()).Single(c => c.CharacterName == "DetectedHero");
                    Check(automatic.Server == "Lumiel" && automatic.Region == "GLOBAL_EU" && !automatic.Confirmed, "detected character saved with unknown energy");
                    Check(((Character)Control<ComboBox>(window, "CharactersInput").SelectedItem).Id == automatic.Id, "detected character automatically selected");
                    await (Task)Call(window, "SaveDetectedCharacterAsync", inertCapture, detected)!;
                    Check((await api.GetCharactersAsync()).Length == 3, "repeated identity is deduplicated");
                    // A reading from the game is published to the selected character and kept fresh.
                    Call(window, "OnCandidate", inertCapture, new PacketCandidate(390, 7, "test-fixture-v1") { Primary = 0, Bonus = 390 });
                    for (var i = 0; i < 200 && (string)Field("activeCharacterId").GetValue(window)! == ""; i++) await Task.Delay(25);
                    var published = (await api.GetCharactersAsync()).Single(c => c.Id == automatic.Id);
                    Check(published.Confirmed && published.Valid && published.Value == 390, "decoded reading is saved to the detected character");
                    Check(((Character)Control<ComboBox>(window, "CharactersInput").SelectedItem).Value == 390, "selector reflects the saved value");
                    await (Task)Call(window, "StopReaderAsync")!;
                    var stopped = (await api.GetCharactersAsync()).Single(c => c.Id == automatic.Id);
                    Check(stopped.Confirmed && !stopped.Valid && stopped.Value == 390, "stopping the reader keeps the value but ends its freshness");
                    Field("capture").SetValue(window, inertCapture);
                    await (Task)Call(window, "SaveDetectedCharacterAsync", inertCapture, new GameIdentity(43, "UnmappedHero", 2360, null, "GLOBAL_EU"))!;
                    Check(Control<TextBox>(window, "ServerInput").Text == "", "unmapped server does not reuse the previous server");
                    await (Task)Call(window, "SaveDetectedCharacterAsync", inertCapture, new GameIdentity(43, "UnmappedHero", 2004, "Lumiel", null))!;
                    Check(Control<ComboBox>(window, "RegionInput").SelectedItem is null && (await api.GetCharactersAsync()).Length == 3, "unmapped region never creates an assumed account identity");
                    Field("capture").SetValue(window, null);
                    Console.WriteLine("Windows UI character creation, alt, cancel, validation, publishing and persistence passed.");
                }
                Console.WriteLine("Windows UI automatic setup, dark selectors, Save action, reading card and small-window layout passed.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { try { await (Task)Call(window, "QuitAsync")!; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }
        });
        Dispatcher.Run();
    }
    static void Screenshots(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        Control<TextBlock>(window, "StatusLabel").Text = "Your connection is remembered on this PC.";
        Snapshot(window, Path.Combine(directory, "connect-preview.png"));
        var reader = Control<StackPanel>(window, "ReaderPanel"); reader.Visibility = Visibility.Visible; reader.IsEnabled = true;
        Control<StackPanel>(window, "JoinPanel").Visibility = Visibility.Collapsed;
        Field("accountName").SetValue(window, "Sample account");
        Field("api").SetValue(window, new PartyApi("http://127.0.0.1:8090"));
        Control<ComboBox>(window, "RoomsInput").ItemsSource = new[] { new Room("r1", "The evening expedition", 40, "owner", true) };
        Control<ComboBox>(window, "RoomsInput").SelectedIndex = 0;
        Call(window, "BeginCharacterDraft", false);
        Control<TextBlock>(window, "StatusLabel").Text = "Connected · readings are saved to your account.";
        Snapshot(window, Path.Combine(directory, "auto-detect-preview.png"));
        Call(window, "BeginCharacterDraft", true);
        Control<TextBox>(window, "CharacterInput").Text = "TestHero"; Control<TextBox>(window, "ServerInput").Text = "Lumiel";
        Snapshot(window, Path.Combine(directory, "add-character-preview.png"));
        var hero = new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, DateTime.UtcNow.AddSeconds(-40).ToString("yyyy-MM-dd HH:mm:ss.fffZ"));
        Control<ComboBox>(window, "CharactersInput").ItemsSource = new[] { hero };
        Field("loading").SetValue(window, true); Control<ComboBox>(window, "CharactersInput").SelectedIndex = 0; Field("loading").SetValue(window, false);
        Call(window, "LoadCharacter", hero);
        var inert = new PacketCapture(); Field("capture").SetValue(window, inert);
        Control<Button>(window, "ReportButton").Content = "Stop packet reader"; Control<TextBlock>(window, "CaptureStatsLabel").Visibility = Visibility.Visible;
        Call(window, "OnCandidate", inert, new PacketCandidate(390, 7, ExperimentalOdyleDecoder.Version) { Primary = 0, Bonus = 390 });
        Field("activeCharacterId").SetValue(window, "c1");
        var state = typeof(MainWindow).GetNestedType("ReaderState", BindingFlags.NonPublic)!;
        Call(window, "SetReaderState", Enum.Parse(state, "Live"), "Live · 390 Odyle saved to your account.");
        Call(window, "RenderHero");
        Control<TextBlock>(window, "CaptureStatsLabel").Text = "1,284 packets · 96 KB from the game";
        Control<TextBlock>(window, "StatusLabel").Text = "Detected TestHero · Lumiel. Saved to your account.";
        Snapshot(window, Path.Combine(directory, "compact-preview.png"));
        Field("capture").SetValue(window, null);
        Console.WriteLine("Rendered previews to " + directory);
    }
    static void Snapshot(MainWindow window, string path, int width = 400, int height = 690)
    {
        var root = (FrameworkElement)window.Content;
        // An unshown window measures its content without a height; pin the size so the footer stays in view.
        root.Width = width; root.Height = height; root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static void Check(bool condition, string message) { if (!condition) throw new Exception("UI check failed: " + message); }
    static async Task Click(MainWindow window, string handler)
    {
        Call(window, handler, window, new RoutedEventArgs());
        for (var i = 0; i < 400 && (bool)Field("busy").GetValue(window)!; i++) await Task.Delay(25);
        Check(!(bool)Field("busy").GetValue(window)!, "UI action finishes");
    }
}
