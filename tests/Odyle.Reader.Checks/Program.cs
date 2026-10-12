using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                var reader = Control<StackPanel>(window, "ReaderPanel"); reader.Visibility = Visibility.Visible; reader.IsEnabled = true;
                Control<StackPanel>(window, "JoinPanel").Visibility = Visibility.Collapsed;
                Call(window, "BeginCharacterDraft", false);
                Check(Control<Button>(window, "ReportButton").IsEnabled, "empty account can start automatic detection");
                Check(!Control<Expander>(window, "CharacterDetails").IsExpanded, "automatic detection does not require typing");
                Call(window, "BeginCharacterDraft", true);
                Check(Control<Button>(window, "SaveCharacterButton").Visibility == Visibility.Visible, "manual fallback exposes Save character");
                var scroll = (ScrollViewer)window.Content;
                scroll.Measure(new Size(324, 420)); scroll.Arrange(new Rect(0, 0, 324, 420)); scroll.UpdateLayout();
                Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "small window has no horizontal overflow");
                var region = Control<ComboBox>(window, "RegionInput"); region.ApplyTemplate();
                Check(region.Template.FindName("SelectorButton", region) is not null, "dark selector template applied");
                Check(((SolidColorBrush)region.Background).Color == Color.FromRgb(17, 27, 21), "selector has readable dark background");
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
                    await (Task)Call(window, "SaveDetectedCharacterAsync", inertCapture, new GameIdentity(43, "UnmappedHero", 2360, null, "GLOBAL_EU"))!;
                    Check(Control<TextBox>(window, "ServerInput").Text == "", "unmapped server does not reuse the previous server");
                    await (Task)Call(window, "SaveDetectedCharacterAsync", inertCapture, new GameIdentity(43, "UnmappedHero", 2004, "Lumiel", null))!;
                    Check(Control<ComboBox>(window, "RegionInput").SelectedItem is null && (await api.GetCharactersAsync()).Length == 3, "unmapped region never creates an assumed account identity");
                    Console.WriteLine("Windows UI character creation, alt, cancel, validation and persistence passed.");
                }
                Console.WriteLine("Windows UI automatic setup, dark selectors, Save action and small-window layout passed.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { try { await (Task)Call(window, "QuitAsync")!; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }
        });
        Dispatcher.Run();
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
