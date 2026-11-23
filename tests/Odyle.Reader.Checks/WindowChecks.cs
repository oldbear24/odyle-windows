using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Odyle.Core;
using Odyle.Reader;

// The window itself: its layout, preferences, compact mode, following and fading with the game, the
// language, Start with Windows, Npcap, diagnostics, --unpair, its position and Open in Odyle links.
static partial class ReaderChecks
{
    static void LayoutScenario(MainWindow window)
    {
        var settings = Settings(window);
        Check(window.FindName("CharactersInput") is null && window.FindName("CharacterInput") is null, "the main window offers no way to pick or type a character");
        Check(settings.FindName("CodeInput") is PasswordBox && window.FindName("CodeInput") is null, "pairing lives in the settings window");
        Check(settings.FindName("SharingList") is ItemsControl && settings.FindName("InviteInput") is null && settings.FindName("RoomsList") is null, "rooms and sharing are managed on the dashboard; settings only summarizes them");
        Check(Control<Button>(window, "ReportButton").Content as string == "Connect", "an unpaired reader points to settings");
        Check(Control<TextBlock>(window, "ValueLabel").Text == "—", "no value is shown before a reading");
        // Screen readers (W#16): every icon button (a glyph, no words) is named from its tooltip, and the
        // reader's state line and the status line are polite live regions.
        var icons = Tree(window).Concat(Tree(settings)).OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => b.Content is string c && Translation.IsGlyph(c)).ToArray();
        Check(icons.Length >= 5 && icons.All(b => System.Windows.Automation.AutomationProperties.GetName(b).Length > 0), "every icon button has a name: " + string.Join(", ", icons.Where(b => System.Windows.Automation.AutomationProperties.GetName(b).Length == 0).Select(b => b.Name)));
        Check(System.Windows.Automation.AutomationProperties.GetName(Control<Button>(window, "PinButton")) == "Stay on top of other windows" && Translation.NameOf("Settings · account, rooms and reader setup") == "Settings" && !Translation.IsGlyph("OK") && Translation.IsGlyph("✕"), "the name is the tooltip's first part");
        Check(new[] { "PacketStateLabel", "StatusLabel" }.All(n => System.Windows.Automation.AutomationProperties.GetLiveSetting(Control<TextBlock>(window, n)) == System.Windows.Automation.AutomationLiveSetting.Polite), "status lines are live regions");
        var root = (FrameworkElement)settings.Content;
        root.Width = 300; root.Height = 480; root.Measure(new Size(300, 480)); root.Arrange(new Rect(0, 0, 300, 480)); root.UpdateLayout();
        var scroll = (ScrollViewer)settings.FindName("Scroller");
        Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "narrow settings window has no horizontal overflow");
        // Updates: nothing is offered before a verified package exists, and an unstamped build never replaces itself.
        Check(!Updater.IsReleaseBuild && ((TextBlock)settings.FindName("UpdateLabel")).Text.Contains("development build") && Control<Button>(window, "UpdateButton").Visibility == Visibility.Collapsed && ((Button)settings.FindName("InstallUpdateButton")).Visibility == Visibility.Collapsed, "an unstamped build shows no update and never replaces itself");
    }
    static void SessionStoreScenario()
    {
        Check(System.Text.Json.JsonSerializer.Deserialize<SavedSession>("""{"Endpoint":"https://party.example","Token":"t"}""") is { Token: "t", DeviceKey: null }, "a session saved before device keys still loads (RestoreAsync then asks to pair again)");
        SessionChecks();
    }
    static void PreferencesAndTasksScenario(MainWindow window)
    {
        var settings = Settings(window);
        // Window preferences: pinned above other windows and the checklist beside the card, remembered on this PC.
        Check(!window.Topmost && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && PreferencesStore.Load() is { StayOnTop: false, ShowTasks: false }, "the window starts unpinned and compact");
        // What's new: a first start begins at this version and says nothing; after an update the card says so once.
        var current = $"{Updater.Current.Major}.{Updater.Current.Minor}.{Updater.Current.Build}";
        Check(PreferencesStore.Load().SeenVersion == current && Control<Border>(window, "UpdatedBar").Visibility == Visibility.Collapsed && settings.FindName("WhatsNewButton") is Button, "a first start says nothing of an update; Settings offers What's new");
        Field("preferences").SetValue(window, PreferencesStore.Load() with { SeenVersion = "0.0.1" }); Call(window, "RenderUpdated");
        Check(Control<Border>(window, "UpdatedBar").Visibility == Visibility.Visible && Control<TextBlock>(window, "UpdatedText").Text == $"Updated to {current}.", "after an update the card names the version");
        Call(window, "OpenWhatsNew");
        var news = (Window)Field("whatsNew").GetValue(window)!; var listed = ((StackPanel)news.FindName("Entries")).Children;
        Check(PreferencesStore.Load().SeenVersion == current && Control<Border>(window, "UpdatedBar").Visibility == Visibility.Collapsed, "opening What's new is remembered and the line goes");
        Check(listed.Count > 3 && listed[0] is DockPanel head && string.Concat(head.Children.OfType<TextBlock>().Last().Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) == current + "  this version", "What's new lists the changelog built into the app, this version first and marked");
        news.Close();

        Call(window, "SetStayOnTop", true, true);
        Check(window.Topmost && ((CheckBox)settings.FindName("StayOnTopToggle")).IsChecked == true && PreferencesStore.Load().StayOnTop, "pinning stays on top and is remembered");
        Call(window, "SetTasksShown", true, true);
        Check(Control<Border>(window, "TasksPanel").Visibility == Visibility.Visible && ((CheckBox)settings.FindName("TasksToggle")).IsChecked == true && PreferencesStore.Load() is { StayOnTop: true, ShowTasks: true }, "the checklist opens beside the card and is remembered");
        Check(((TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource).Length == 0 && Control<TextBlock>(window, "TasksHint").Text.StartsWith("Pair", StringComparison.Ordinal), "without an account the panel asks for pairing");
        // Done follows the game's resets: a tick from before the current period no longer counts.
        window.Link.Api = new PartyApi("http://127.0.0.1:8090");
        string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Field("tasks").SetValue(window, new[] { new UserTask("t1", "u", "Daily dungeon", "daily", Stamp(DateTimeOffset.UtcNow)), new UserTask("t2", "u", "Daily bounty", "daily"), new UserTask("t3", "u", "Weekly raid", "weekly", Stamp(Checklist.PeriodStart("weekly", DateTimeOffset.UtcNow).AddMinutes(-1))), new UserTask("t4", "u", "Weekly boss", "weekly", Stamp(DateTimeOffset.UtcNow)) });
        Field("tasksLoaded").SetValue(window, true); Call(window, "RenderTasks");
        var groups = (TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource;
        Check(groups.Length == 2 && groups[0].Header == "DAILY" && groups[0].Tasks.Select(t => t.Done).SequenceEqual(new[] { true, false }) && groups[0].Progress.Trim() == "1 / 2", "daily tasks show done and open items with their count");
        Check(groups[1].Header == "WEEKLY" && groups[1].Tasks.Select(t => t.Done).SequenceEqual(new[] { false, true }) && groups[1].Percent == 50, "a weekly tick from before the current period no longer counts");
        Check(groups.All(g => g.Reset.StartsWith("resets in ", StringComparison.Ordinal)) && groups[0].Tasks[0].Hint.StartsWith("Done", StringComparison.Ordinal) && groups[0].Tasks[1].Hint == "Still to do today", "each list counts down to its reset");
        // A list per character (#38): the card's character's tasks join the account's, the others' stay on the dashboard.
        var accountTasks = (UserTask[])Field("tasks").GetValue(window)!;
        Field("tasks").SetValue(window, accountTasks.Concat(new[] { new UserTask("t4", "u", "Nightmare", "weekly", Character: "alt", CharacterName: "Altie"), new UserTask("t5", "u", "Main boss", "weekly", Character: "main", CharacterName: "Mainy") }).ToArray());
        Field("characters").SetValue(window, new[] { new Character("alt", "u", "Altie", "Lumiel", "GLOBAL_EU", 0, false, false, "") }); Field("currentCharacterId").SetValue(window, "alt");
        Call(window, "RenderHero");
        var scopedGroups = (TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource;
        Check(scopedGroups[1].Tasks.Length == 3 && scopedGroups[1].Tasks[2] is { Task.Title: "Nightmare", Hint: "Altie · Still to do this week" } && !scopedGroups[1].Tasks.Any(t => t.Task.Title == "Main boss"), "the card's character brings its own list, named, and other characters' stay out: " + string.Join(",", scopedGroups[1].Tasks.Select(t => t.Task.Title)));
        Field("characters").SetValue(window, Array.Empty<Character>()); Field("currentCharacterId").SetValue(window, ""); Field("tasks").SetValue(window, accountTasks); Call(window, "RenderHero");
        Check(((TaskGroup[])Control<ItemsControl>(window, "TaskGroups").ItemsSource)[1].Tasks.Length == 2, "without a character only the account's list shows");
        var shell = (FrameworkElement)window.Content; shell.Width = MainWindow.CardWidth + 240; shell.Measure(new Size(shell.Width, double.PositiveInfinity)); shell.Arrange(new Rect(0, 0, shell.Width, shell.DesiredSize.Height)); shell.UpdateLayout();
        Check(Control<Grid>(window, "Card").ActualWidth == MainWindow.CardWidth && Control<Border>(window, "TasksPanel").ActualWidth == 240, "the card keeps its width and the checklist takes its own column");
        shell.Width = double.NaN;
        Call(window, "SetTasksShown", false, true); Call(window, "SetStayOnTop", false, true);
        Check(!window.Topmost && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && PreferencesStore.Load() is { StayOnTop: false, ShowTasks: false }, "unpinning and hiding the checklist are remembered too");
    }
    static void CompactModeScenario(MainWindow window)
    {
        var settings = Settings(window); window.Link.Api = new PartyApi("http://127.0.0.1:8090");
        // Compact mode cuts the window down to your Odyle and your party's, in the same window and place, and is remembered.
        Check(!PreferencesStore.Load().CompactMode && Control<Border>(window, "CompactPanel").Visibility == Visibility.Collapsed && (string)Control<Button>(window, "CompactButton").ToolTip == "Compact mode · your Odyle and your party's, nothing else (Ctrl+Alt+O)", "the window starts with the full card, and the compact button names its shortcut");
        var mateSeen = Stamp(DateTimeOffset.UtcNow);
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 130, true, true, Stamp(DateTimeOffset.UtcNow.AddMinutes(-5))) }); Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u");
        Field("snapshots").SetValue(window, new[] { new RoomSnapshot(new Room("r1", "Evening", 40, "reporter", true), [new RoomMember("m1", "l", "reporter", "Lumina", [new Character("l1", "l", "Lumina", "Lumiel", "GLOBAL_EU", 180, true, true, mateSeen, mateSeen, "packet")])], mateSeen) });
        window.Left = 300; window.Top = 200; Call(window, "SetTasksShown", true, false);
        Call(window, "SetCompact", true, true); Call(window, "RenderHero");
        Check(PreferencesStore.Load().CompactMode && Control<Border>(window, "CompactPanel").Visibility == Visibility.Visible && Control<Grid>(window, "Card").Visibility == Visibility.Collapsed && Control<Border>(window, "TasksPanel").Visibility == Visibility.Collapsed && window.Left == 300 && window.Top == 200,
            "compact mode replaces the card and the checklist in the same window and place, and is remembered");
        Check(Control<TextBlock>(window, "CompactName").Text == "TestHero" && Control<TextBlock>(window, "CompactValue").Text == "130" && Control<TextBlock>(window, "CompactCubes").Text == "3 cubes", "your row shows your character, its Odyle and cubes");
        Check(Control<ItemsControl>(window, "CompactMates").ItemsSource is object[] and [CompactMate { Name: "Lumina", Value: "180", Cubes: "4 cubes", Hint: "Lumina · live from the game" }] && Control<TextBlock>(window, "CompactNote").Visibility == Visibility.Collapsed, "your party's rows follow, with their Odyle and cubes");
        var compactShell = (FrameworkElement)window.Content; compactShell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); compactShell.Arrange(new Rect(compactShell.DesiredSize)); compactShell.UpdateLayout();
        Check(Math.Abs(compactShell.ActualWidth - CompactStrip.Width) < 1 && compactShell.ActualHeight < 90, "compact mode is as wide as CompactStrip.Width and only as tall as its rows");
        Field("readerPaused").SetValue(window, true); Call(window, "RenderHero");
        Check(Control<TextBlock>(window, "CompactNote").Text == "Paused by the server", "a server pause shows under the rows");
        Field("readerPaused").SetValue(window, false);
        Check(Control<Border>(window, "CompactDivider").Visibility == Visibility.Visible && Control<Border>(window, "CompactOwnParty") is { BorderBrush: null, BorderThickness.Left: 0 } && Control<ItemsControl>(window, "CompactOwnPartyMates").Items.Count == 0,
            "outside a band the divider parts your row from the others'");
        // Your in-game party (GameBands) is framed around your row and its players, in place of the divider.
        var outside = Field("snapshots").GetValue(window); GameGroup ours = new("0a0b0c0d0e0f0001");
        Field("snapshots").SetValue(window, new[] { new RoomSnapshot(new Room("r1", "Evening", 40, "reporter", true), [new RoomMember("m0", "u", "reporter", "Me", [new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 130, true, true, mateSeen, mateSeen, "packet", GameGroup: ours)]),
            new RoomMember("m1", "l", "reporter", "Lumina", [new Character("l1", "l", "Lumina", "Lumiel", "GLOBAL_EU", 180, true, true, mateSeen, mateSeen, "packet", GameGroup: ours)]),
            new RoomMember("m2", "z", "reporter", "Zed", [new Character("z1", "z", "Zed", "Lumiel", "GLOBAL_EU", 900, true, true, mateSeen, mateSeen, "packet")])], mateSeen) });
        Call(window, "RenderParty");
        Check(Control<ItemsControl>(window, "CompactOwnPartyMates").ItemsSource is object[] and [CompactMate { Name: "Lumina" } lumina] && lumina.Hint.EndsWith("\nIn your party in game", StringComparison.Ordinal)
            && Control<ItemsControl>(window, "CompactMates").ItemsSource is object[] and [CompactMate { Name: "Zed", Gap.Top: 3 }] && Control<ItemsControl>(window, "CompactOwnForceMates").Items.Count == 0
            && ReferenceEquals(lumina.Edge, window.FindResource("PartyChipEdge")) && Control<Border>(window, "CompactOwnParty").ToolTip as string == "In your party in game" && Control<Border>(window, "CompactOwnForce").ToolTip is null
            && Control<Border>(window, "CompactDivider").Visibility == Visibility.Collapsed && ((string)Control<Border>(window, "CompactPanel").ToolTip).EndsWith("\nIn your party in game", StringComparison.Ordinal),
            "your in-game party's chips are edged in its colour and follow your pill, in place of the divider");
        compactShell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); compactShell.Arrange(new Rect(compactShell.DesiredSize)); compactShell.UpdateLayout();
        var nameAt = (FrameworkElement f) => f.TranslatePoint(new Point(0, 0), compactShell).X;
        var mateName = Visuals(Control<ItemsControl>(window, "CompactOwnPartyMates")).OfType<TextBlock>().First(t => t.Text == "Lumina");
        var otherName = Visuals(Control<ItemsControl>(window, "CompactMates")).OfType<TextBlock>().First(t => t.Text == "Zed");
        Check(mateName.ActualWidth > 0 && otherName.ActualWidth > 0 && nameAt(mateName) >= nameAt(Control<Border>(window, "CompactOwnParty")), "every chip shows its player's name");
        Field("snapshots").SetValue(window, outside); Call(window, "RenderParty");
        var hotkey = (ComboBox)settings.FindName("CompactHotkeyInput");
        Check((string)hotkey.SelectedValue == "Ctrl+Alt+O" && hotkey.Items.Count == CompactStrip.Hotkeys.Length, "Settings offer the compact mode shortcuts, Ctrl+Alt+O first");
        hotkey.SelectedValue = "";
        Check(PreferencesStore.Load().CompactHotkey == "" && (string)Control<Button>(window, "CompactButton").ToolTip == "Compact mode · your Odyle and your party's, nothing else", "the shortcut can be turned off, and that is remembered");
        hotkey.SelectedValue = CompactStrip.DefaultHotkey; Call(window, "SetCompact", false, true);
        Check(!PreferencesStore.Load().CompactMode && Control<Grid>(window, "Card").Visibility == Visibility.Visible && Control<Border>(window, "TasksPanel").Visibility == Visibility.Visible && window.Left == 300 && window.Top == 200 && PreferencesStore.Load().CompactHotkey == "Ctrl+Alt+O",
            "the full card and the checklist come back in the same place, and that is remembered");
        compactShell.Width = double.NaN; Call(window, "SetTasksShown", false, false);
    }
    static void CapMismatchScenario(MainWindow window)
    {
        window.Link.Api = new PartyApi("http://127.0.0.1:8090"); Field("currentCharacterId").SetValue(window, "c1");
        // A membership answer the readings contradict is pointed out under the clock; readings that fit it are not.
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 700, true, true, Stamp(DateTimeOffset.UtcNow.AddMinutes(-5)), NaturalMax: 700, AnsweredCap: 560) }); Call(window, "RenderHero");
        Check(Control<TextBlock>(window, "CapMismatchLabel") is { Visibility: Visibility.Visible, Text: "Natural pool held 700, more than 560: has the membership started? Answer on the dashboard." }, "the card points out readings above the answered cap");
        Field("characters").SetValue(window, new[] { new Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 700, true, true, Stamp(DateTimeOffset.UtcNow.AddMinutes(-5)), NaturalMax: 700, AnsweredCap: 840) }); Call(window, "RenderHero");
        Check(Control<TextBlock>(window, "CapMismatchLabel").Visibility == Visibility.Collapsed, "readings that fit the answer need no hint");
    }
    static void LanguageScenario(MainWindow window)
    {
        var settings = Settings(window);
        // Language: automatic by default; a choice is remembered and applies after a restart.
        var language = (ComboBox)settings.FindName("LanguageInput");
        Check((string)language.SelectedValue == Localizer.Automatic && language.Items.Count == 3, "the language follows Windows until one is chosen");
        language.SelectedValue = "cs";
        Check(PreferencesStore.Load().Language == "cs" && ((FrameworkElement)settings.FindName("LanguageRestartPanel")).Visibility == Visibility.Visible && Localizer.Language == "en", "choosing Czech is remembered and offers a restart");
        language.SelectedValue = Localizer.Automatic; Check(PreferencesStore.Load().Language == Localizer.Automatic, "automatic can be chosen again");
    }
    static void AutostartScenario(MainWindow window)
    {
        var settings = Settings(window);
        // The offer comes after the first live reading.
        Call(window, "OfferAutostart");
        // The offer after the first live reading waits for one answer; declining is remembered on this PC.
        Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Visible && !PreferencesStore.Load().AutostartOffered, "the offer waits for an answer");
        Call(window, "DeclineAutostart_Click", window, new RoutedEventArgs());
        Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Collapsed && PreferencesStore.Load().AutostartOffered && runKey.Values.Count == 0, "declining the offer is remembered and writes nothing");
        Call(window, "OfferAutostart");
        Check(Control<Border>(window, "AutostartOffer").Visibility == Visibility.Collapsed, "an answered offer does not come back");
        // Start with Windows: "<Odyle.exe>" --tray in the Run key, removed again when switched off.
        Call(window, "SetAutostart", true);
        Check(runKey.Values.Single() is { Key: "Odyle", Value: "\"C:\\Odyle\\Odyle.exe\" --tray" } && ((CheckBox)settings.FindName("AutostartToggle")).IsChecked == true && Autostart.Enabled, "Start with Windows writes the Run entry for a start in the tray");
        Call(window, "SetAutostart", false);
        Check(runKey.Values.Count == 0 && ((CheckBox)settings.FindName("AutostartToggle")).IsChecked == false, "switching it off removes the entry");
        runKey.Values["Odyle"] = "\"C:\\Moved away\\Odyle.exe\" --tray"; Autostart.Repair();
        Check(runKey.Values["Odyle"] == "\"C:\\Odyle\\Odyle.exe\" --tray", "an entry for an executable that is gone starts this one");
        runKey.Values["Odyle"] = StartupCommand.For(Environment.ProcessPath!); Autostart.Repair();
        Check(runKey.Values["Odyle"] == StartupCommand.For(Environment.ProcessPath!), "an entry for another existing Odyle is left alone"); runKey.Values.Clear();
        Field("preferences").SetValue(window, (Preferences)Field("preferences").GetValue(window)! with { AutostartOffered = false }); Call(window, "OfferAutostart");
        Call(window, "AcceptAutostart_Click", window, new RoutedEventArgs());
        Check(runKey.Values.Single() is { Key: "Odyle", Value: "\"C:\\Odyle\\Odyle.exe\" --tray" } && PreferencesStore.Load().AutostartOffered && Control<Border>(window, "AutostartOffer").Visibility == Visibility.Collapsed, "accepting the offer starts Odyle with Windows");
        runKey.Values.Clear();
    }
    static async Task NpcapScenario(MainWindow window)
    {
        var settings = Settings(window); var npcapTimer = (DispatcherTimer)Field("npcapTimer").GetValue(window)!;
        window.Link.Api = new PartyApi("http://127.0.0.1:8090");
        // Npcap is checked whenever settings open.
        Npcap.Probe = () => false; Call(window, "RenderNpcap");
        Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap not found · install it to read the game" && ((Button)settings.FindName("NpcapButton")).Visibility == Visibility.Visible, "a missing Npcap is pointed out with its download");
        Npcap.Probe = () => true; Call(window, "RenderNpcap"); Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap installed", "an installed Npcap is confirmed");
        // Npcap restricted to administrators is explained in settings and on the reader line.
        Npcap.Restricted = () => true; Call(window, "RenderNpcap");
        Check(((TextBlock)settings.FindName("NpcapLabel")).Text == "Npcap installed · only administrators may capture", "settings say Npcap only lets administrators capture");
        Call(window, "ShowNpcapProblem", new InvalidOperationException("No Npcap capture interfaces available."));
        Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap lets only administrators capture · reinstall it without “Restrict Npcap driver's access to Administrators only”." && Control<Button>(window, "NpcapLink") is { Visibility: Visibility.Visible, Content: "Npcap setup ↗" }, "a reader that finds no interface explains the administrators-only option");
        Npcap.Restricted = () => false;
        // Without Npcap the reader line links to its download and Npcap is looked for every 10 seconds;
        // once it is there an unpaired app only clears the line (a paired one starts the reader).
        var paired = window.Link.Api; window.Link.Api = null;
        Npcap.Probe = () => false; Call(window, "ShowNpcapProblem", new object[] { null! });
        Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap is not installed · Odyle needs it to read the game." && Control<Button>(window, "NpcapLink") is { Visibility: Visibility.Visible, Content: "Install Npcap ↗" } && npcapTimer.IsEnabled, "a missing Npcap shows in the reader row with its download");
        await (Task)Call(window, "NpcapTickAsync")!;
        Check(npcapTimer.IsEnabled && Control<Button>(window, "NpcapLink").Visibility == Visibility.Visible, "a missing Npcap is looked for again");
        Npcap.Probe = () => true; await (Task)Call(window, "NpcapTickAsync")!;
        Check(!npcapTimer.IsEnabled && Control<Button>(window, "NpcapLink").Visibility == Visibility.Collapsed && Control<TextBlock>(window, "PacketStateLabel").Text == "Npcap is installed · pair Odyle in settings to read the game.", "an installed Npcap clears the line");
        // A saved pairing that is still being retried does not ask to pair again.
        window.Link.RetryAt = DateTimeOffset.UtcNow.AddSeconds(15); Call(window, "RenderReaderButton"); Npcap.Probe = () => false; Call(window, "ShowNpcapProblem", new object[] { null! });
        Npcap.Probe = () => true; await (Task)Call(window, "NpcapTickAsync")!;
        Check(Control<TextBlock>(window, "PacketStateLabel").Text == "Stopped · start the reader before logging in." && (string)Control<Button>(window, "ReportButton").Content == "Start", "a paired app still retrying the server can start its reader: it reads locally until the server answers");
        window.Link.RetryAt = null;
    }
    static void DiagnosticsScenario(MainWindow window)
    {
        // The reader's last states hold a missing Npcap, which is installed by now.
        Npcap.Probe = () => false; Call(window, "ShowNpcapProblem", new object[] { null! }); Npcap.Probe = () => true;
        ((DispatcherTimer)Field("npcapTimer").GetValue(window)!).Stop(); var paired = window.Link.Api;
        // Copy diagnostics: version, Npcap, interfaces, the reader's last states and the end of the log,
        // with the account and this PC's name masked (built here without touching the clipboard).
        Log.Info("Connected to http://127.0.0.1:8090 as account abcdefghij12345 on device \"DESKTOP-CHECK\".");
        var diagnostics = (string)Call(window, "DiagnosticsReport")!;
        Check(diagnostics.StartsWith("Odyle diagnostics · ", StringComparison.Ordinal) && diagnostics.Contains("Npcap: installed") && diagnostics.Contains("Capture interfaces: ") && diagnostics.Contains("Account: not paired") && diagnostics.Contains("Problem · Npcap is not installed"), "diagnostics list the version, Npcap and the reader's last states");
        Check(diagnostics.Contains("account [account] on device \"[this PC]\"") && !diagnostics.Contains("abcdefghij12345") && !diagnostics.Contains("DESKTOP-CHECK") && !diagnostics.Contains("device-key"), "diagnostics mask the account and this PC's name");
        window.Link.Api = paired;
    }
    static void UnpairScenario()
    {
        // --unpair (the uninstaller): a running Odyle is asked to quit and waited for, then the pairing is
        // forgotten on this PC also when the server cannot be reached. A made-up user keeps the real Odyle out of it.
        var someone = "odyle-check-" + Guid.NewGuid().ToString("N");
        using (var asked = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Quit-" + someone))
        using (var ready = new ManualResetEventSlim())
        {
            var running = new Thread(() => { using var owned = new Mutex(true, "Local\\Odyle-" + someone); ready.Set(); asked.WaitOne(TimeSpan.FromSeconds(30)); owned.ReleaseMutex(); });
            running.Start(); ready.Wait();
            SessionStore.Save(new SavedSession("http://127.0.0.1:1", "token", "device-key"));
            typeof(App).GetMethod("Unpair", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [someone]);
            Check(running.Join(TimeSpan.FromSeconds(5)) && !File.Exists(SessionStore.FilePath) && File.ReadAllText(Log.FilePath).Contains("The server could not be told to forget this PC"), "--unpair asks a running Odyle to quit, then forgets the pairing on this PC");
        }
    }
    static void PositionScenario(MainWindow window)
    {
        // The window reopens where it was, while that is on a connected screen.
        var kept = Field("preferences").GetValue(window)!;
        var area = System.Windows.SystemParameters.WorkArea;
        Field("preferences").SetValue(window, new Preferences(Left: area.Left + 40, Top: area.Top + 30)); Call(window, "RestorePosition");
        Check(window.WindowStartupLocation == WindowStartupLocation.Manual && window.Left == area.Left + 40 && window.Top == area.Top + 30, "a remembered position on a connected screen is restored");
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Field("preferences").SetValue(window, new Preferences(Left: -40000, Top: -40000)); Call(window, "RestorePosition");
        Check(window.WindowStartupLocation == WindowStartupLocation.CenterScreen && window.Left == area.Left + 40, "a position on a screen that is gone opens centred instead");
        Field("preferences").SetValue(window, kept);
    }
    static void CzechScenario()
    {
        // The XAML is written in English and translated once per window.
        Localizer.Use("cs");
        try
        {
            var czech = new SettingsWindow();
            Check(czech.Title == "Nastavení Odyle" && ((Button)czech.FindName("PasteButton")).Content as string == "Vložit odkaz a připojit" && ((Expander)czech.FindName("ServerOptions")).Header as string == "Adresa serveru"
                && ((Button)czech.FindName("NpcapButton")).ToolTip as string == "Pro čtení paketů musí být nainstalovaný Npcap", "settings speak Czech when Czech is chosen");
            Check(Localizer.Tn(3, "{0} cube", "{0} cubes") == "3 kostky" && Localizer.T("Publisher session expired. Reconnect.") == "Relace čtečky vypršela. Připoj se znovu.", "Czech plurals and server messages");
        }
        finally { Localizer.Use("en"); }
    }
    static void FollowAndFadeScenario(MainWindow window)
    {
        var readerStates = typeof(MainWindow).GetNestedType("ReaderState", BindingFlags.NonPublic)!;
        // While you play (W#18): following the game brings the card up when the reader finds the
        // game's connection after waiting and sends it away when it waits again; fading makes the
        // window layered at 60 % while the game is in front, opaque when it is not, plain when off.
        var follow = typeof(MainWindow).GetMethod("FollowStep", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Step(bool present, string name) => (string)follow.Invoke(null, [present, Enum.Parse(readerStates, name)])!;
        Check(Step(false, "Live") == "show" && Step(false, "Listening") == "show" && Step(true, "Live") == "" && Step(true, "Waiting") == "hide" && Step(false, "Waiting") == "" && Step(true, "Problem") == "" && Step(false, "Stopped") == "", "following the game: up when its connection appears, away when it is gone");
        Check(Settings(window).FindName("FadeInGameToggle") is CheckBox && Settings(window).FindName("FollowGameToggle") is CheckBox, "the settings offer fading and following the game");
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Field("gameInFront").SetValue(window, (Func<bool>)(() => true)); Call(window, "SetFadeInGame", true, false);
        Check(Layered(handle) && Alpha(handle) == 153, "with the game in front the card fades");
        Field("gameInFront").SetValue(window, (Func<bool>)(() => false)); Call(window, "RenderFade");
        Check(Layered(handle) && Alpha(handle) == 255, "and is opaque again when the game is not in front");
        Field("gameInFront").SetValue(window, (Func<bool>)(() => true)); Call(window, "RenderFade"); Call(window, "SetFadeInGame", false, false);
        Check(!Layered(handle), "switched off, the window is a plain one again");
    }
    static async Task OpenInOdyleScenario()
    {
        // Open in Odyle: a second start hands its link to the running Odyle through the pipe; nobody listening is a plain failure.
        var pipeUser = "check-" + Guid.NewGuid().ToString("N"); var pipeLink = new TaskCompletionSource<string>(); var pipe = new PairPipe(pipeUser, link => pipeLink.TrySetResult(link));
        var pipeSent = false; for (var i = 0; i < 20 && !(pipeSent = PairPipe.Send(pipeUser, "odyle://pair?link=x")); i++) await Task.Delay(100);
        Check(pipeSent && await Task.WhenAny(pipeLink.Task, Task.Delay(3000)) == pipeLink.Task && pipeLink.Task.Result == "odyle://pair?link=x", "a second start hands its link to the running Odyle");
        pipe.Stop(); Check(!PairPipe.Send("check-nobody-" + Guid.NewGuid().ToString("N"), "odyle://pair?link=x"), "with no Odyle running the link is not taken");
        Check(PairProtocol.CommandFor(@"C:\Apps\Odyle\Odyle.exe") == "\"C:\\Apps\\Odyle\\Odyle.exe\" --pair \"%1\"", "odyle:// links start Odyle with --pair");
    }
}
