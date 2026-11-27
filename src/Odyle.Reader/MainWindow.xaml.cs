using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Odyle.Core;
using static Odyle.Core.Localizer;
using Forms = System.Windows.Forms;

namespace Odyle.Reader;
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A WPF window is not disposed; quitting disposes what it owns (QuitAsync).")]
public partial class MainWindow : Window
{
    private enum ReaderState { Stopped, Waiting, Listening, Midstream, Live, Stale, Problem, Paused }
    // The connection to the paired server: request client, publisher session, heartbeat and retries (ConnectionLifecycle).
    private readonly ConnectionLifecycle link = new();
    internal ConnectionLifecycle Link => link;
    private PartyApi? api => link.Api;
    private PacketCapture? capture;
    private readonly SettingsWindow settings = new();
    private string activeCharacterId = "", accountName = "", accountId = "";
    // The character always comes from the game; the user cannot pick one. currentCharacterId: character shown in
    // the card (last one the game named). Which character owns the reading is the attribution's (ReaderAttribution).
    private string currentCharacterId = "";
    private readonly ReaderAttribution attribution;
    internal ReaderAttribution Attribution => attribution;
    // What the connected server answered and refused (ConnectionState): replaced as a whole when the
    // request client is set up or dropped (UseApi, DropApi), so nothing carries over to the next one (#92).
    private ConnectionState connection = new();
    // While the server refuses this build's adapter (ConnectionState.AdapterOutdated) an update check
    // runs at most once an hour.
    private DateTimeOffset outdatedCheckAt = DateTimeOffset.MinValue;
    // The server's operators paused every packet reader (Server controls): the rooms, session and
    // heartbeat answers carry the switch, and a reading sent meanwhile answers 503. The capture stops
    // and Npcap is closed until the switch is off again; then the reader starts by itself.
    // readerWanted: the reader should run whenever it can (started, and not stopped by hand).
    private bool readerPaused, readerWanted;
    // Without a working Npcap the reader row links to its download page and Npcap is looked for every
    // 10 seconds; once it is there, the reader starts by itself.
    private readonly DispatcherTimer npcapTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    // The reader line's last states, for Copy diagnostics.
    private readonly Queue<string> readerHistory = new();
    private Character[] characters = [];
    // The game's maps as the server's superusers named them, for the places of location sharing.
    private GameMap[] maps = [];
    private Room[] rooms = [];
    // Server-wide Odyle per cube: the cost shown while the account has no room yet.
    private int odylePerCube = 40;
    // Room snapshots for the party list; serverOffset corrects this PC's clock for presence.
    private RoomSnapshot[] snapshots = [];
    private TimeSpan serverOffset;
    // Fetching the rooms and their snapshots, one fetch at a time (PartyRefreshQueue).
    private readonly PartyRefreshQueue partyQueue;
    // Realtime news of the account and its rooms: the party, the rooms and the checklist
    // follow changes and heartbeats as they happen, and are fetched again when the
    // connection comes back and every five minutes in case news was missed. Without it they
    // are fetched every 15 seconds, as before.
    private RealtimeClient? realtime;
    private int partyTicks;
    // The account's own daily and weekly checklist, read while the panel beside the card is open.
    private UserTask[] tasks = [];
    private TaskResets resets = new();
    private bool tasksLoaded, refreshingTasks;
    // When the checklist was last fetched, for the reset reminders (TaskRemindersAsync).
    private DateTimeOffset tasksFetchedAt = DateTimeOffset.MinValue;
    // The reader problem last told in the tray while the window was hidden; cleared once it reads again.
    private string problemNoticed = "";
    private string? tasksProblem;
    private DateTimeOffset renderedDaily, renderedWeekly;
    // The character whose list the checklist shows with the account's (Checklist.Shown).
    private string? renderedTasksCharacter;
    // Pinned above other windows and the checklist beside the card: remembered on this PC.
    private Preferences preferences = new();
    // Compact mode (the card cut down to your Odyle and your party's, in the same window and
    // place), and the shortcut registered for it on a message-only window.
    private bool compactMode;
    private NativeWindows.Shortcut? shortcut;
    private string hotkeyProblem = "";
    // The value and cube cost the card shows, for the strip.
    private int? heroValue;
    // What's new (WhatsNewWindow), open or not.
    private WhatsNewWindow? whatsNew;
    private static string VersionText => $"{Updater.Current.Major}.{Updater.Current.Minor}.{Updater.Current.Build}";
    private int heroCost = 40;
    private long lastGameBytes;
    private DateTimeOffset publishBlockedUntil = DateTimeOffset.MinValue;
    private int publishedTotal = -1;
    // The natural pool of the reading saved last, and the last change the game made (a spend, a
    // regeneration tick or an item), for the reading's kind (OdyleChange) and the card's split line.
    private int? publishedBase;
    private (int Delta, string Kind, DateTimeOffset At, string CharacterId)? lastChange;
    private string? publishFailure;
    private ReaderState readerState = ReaderState.Stopped;
    private bool exit, quitting, busy, busyShown;
    // A detected character that could not be saved (offline, busy) is saved after the next heartbeat.
    private (PacketCapture Capture, GameIdentity Identity)? pendingIdentity;
    private string lastStatus = "", lastReaderLog = "";
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer publishDelay = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    // The Odyle clock's rules from the server; null from older backends (the defaults apply).
    private ClockRules? clockRules;
    // The account's notification settings and the tray reminders they ask for (TrayReminders).
    private readonly TrayReminders reminders;
    internal TrayReminders Reminders => reminders;
    private readonly DispatcherTimer partyTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    // When and how this build updates itself (UpdateFlow); Settings and the card show it (RenderUpdate).
    private readonly UpdateFlow updates;
    internal UpdateFlow Updates => updates;
    // The window's position is remembered a second after it last moved.
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // The tray icon, its menu and its notifications (Tray); the menu is filled as it opens (TrayMenuOpening).
    private readonly Tray tray;
    internal Tray TrayIcon => tray;
    internal (string Title, string Text)? LastNotice { get => tray.LastNotice; set => tray.LastNotice = value; }
    private int hiddenTicks;

    public MainWindow() : this(true) { }
    // saved: the preferences App read at start (for the language), so they are read once; the checks read them here.
    public MainWindow(bool enableTray, Preferences? saved = null)
    {
        // Starting over also forgets where the named character was and the party it was in.
        attribution = new(publishDelay, () => { locations.Reset(); groups.Reset(); });
        reminders = new(() => preferences, saved => PreferencesStore.Save(preferences = saved), Notify);
        partyQueue = new(() => api, id => rooms.Any(r => r.Id == id), ApplyRoomsAsync, ApplySnapshots);
        updates = new(() => api, () => quitting, () => busy, card => { RenderUpdate(); if (card) RenderHero(); }, NotifyReady, Status, () => QuitAsync(restart: true));
        InitializeComponent(); Translation.Apply(this); gameInFront = GameWindowInFront;
        tray = new(enableTray, Dispatcher, Brush, new(Open: ShowWindow, Reader: () => Reader_Click(this, new RoutedEventArgs()), Tasks: shown => { SetTasksShown(shown, persist: true); if (shown) ShowWindow(); },
            Opening: TrayMenuOpening, Pin: on => SetStayOnTop(on, persist: true), Compact: on => { SetCompact(on, persist: true); ShowWindow(); }, Settings: () => { ShowWindow(); OpenSettings(); },
            Quit: () => Guarded("Quit from the tray", QuitAskedAsync)));
        settings.PasteButton.Click += PasteConnect_Click; settings.JoinButton.Click += Pair_Click; settings.RetryButton.Click += Retry_Click;
        settings.DashboardButton.Click += (_, _) => OpenDashboard(""); settings.ManageButton.Click += (_, _) => OpenDashboard("/"); settings.LogoutButton.Click += Logout_Click;
        settings.NpcapButton.Click += (_, _) => OpenLink(Npcap.DownloadPage); settings.KeyDown += Settings_KeyDown; settings.SourceInitialized += (_, _) => SettingsFrame();
        settings.DiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        settings.CheckUpdateButton.Click += async (_, _) => await Guarded("Check for updates", () => updates.CheckAsync(manual: true)); settings.InstallUpdateButton.Click += Install_Click;
        settings.ReleaseNotesButton.Click += (_, _) => OpenLink(updates.Staged?.ReleaseUrl ?? ReleaseFeed.ReleasesPage.ToString());
        settings.WhatsNewButton.Click += (_, _) => OpenWhatsNew();
        settings.StayOnTopToggle.Click += (_, _) => SetStayOnTop(settings.StayOnTopToggle.IsChecked == true, persist: true);
        settings.TasksToggle.Click += (_, _) => SetTasksShown(settings.TasksToggle.IsChecked == true, persist: true);
        settings.AutostartToggle.Click += (_, _) => SetAutostart(settings.AutostartToggle.IsChecked == true);
        settings.ConfirmQuitToggle.Click += (_, _) => PreferencesStore.Save(preferences = preferences with { ConfirmQuit = settings.ConfirmQuitToggle.IsChecked == true });
        settings.FadeInGameToggle.Click += (_, _) => SetFadeInGame(settings.FadeInGameToggle.IsChecked == true, persist: true);
        settings.FollowGameToggle.Click += (_, _) => PreferencesStore.Save(preferences = preferences with { FollowGame = settings.FollowGameToggle.IsChecked == true });
        settings.DiscordToggle.Click += (_, _) => SetDiscordStatus(settings.DiscordToggle.IsChecked == true, persist: true); discord.Changed += () => Dispatcher.BeginInvoke(RenderDiscordNote);
        gameFocusTimer.Tick += (_, _) => RenderFade(); MouseEnter += (_, _) => RenderFade(); MouseLeave += (_, _) => RenderFade();
        settings.NotificationsButton.Click += (_, _) => OpenDashboard("/account");
        // The acrylic background: the toggle at once, the opacity as the slider moves, saved once it rests.
        settings.AcrylicToggle.Click += (_, _) => SetAcrylic(settings.AcrylicToggle.IsChecked == true, preferences.AcrylicOpacity, persist: true);
        acrylicSave.Tick += (_, _) => { acrylicSave.Stop(); SetAcrylic(preferences.Acrylic, preferences.AcrylicOpacity, persist: true); };
        settings.AcrylicOpacityInput.ValueChanged += (_, e) =>
        {
            var chosen = (int)Math.Round(e.NewValue);
            if (chosen == preferences.AcrylicOpacity) return;
            SetAcrylic(preferences.Acrylic, chosen, persist: false); acrylicSave.Stop(); acrylicSave.Start();
        };
        settings.QuitButton.Click += Quit_Click;
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SavePosition(); };
        settings.LogButton.Click += (_, _) => { try { System.IO.Directory.CreateDirectory(Log.Folder); } catch (IOException) { } OpenLink(Log.Folder); };
        link.RetryTimer.Tick += async (_, _) => await Guarded("Reconnecting the saved account", async () =>
        {
            link.RetryTimer.Stop();
            if (busy) { link.RetryTimer.Interval = TimeSpan.FromSeconds(2); link.RetryTimer.Start(); return; }
            await ExecuteAsync(RestoreAsync);
        });
        updates.Timer.Tick += async (_, _) => await Guarded("The update timer", updates.TickAsync);
        // With realtime the party and the checklist follow the news, and every 20th tick (five
        // minutes) fetches them in case some was missed; run reminders are checked every tick.
        // Without it, open, they refresh every 15 seconds (showing the window refreshes at once)
        // and in the tray the rooms every minute, for the run reminders.
        partyTimer.Tick += async (_, _) => await Guarded("The party timer", async () =>
        {
            await TaskRemindersAsync();
            if (realtime?.IsConnected == true)
            {
                if (++partyTicks % 20 == 0) await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync());
                else CheckReminders();
            }
            else if (IsVisible) await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync());
            else if (reminders.Settings.RunsInTray && ++hiddenTicks % 4 == 0) await RefreshPartyAsync();
        });
        link.Heartbeat.Tick += async (_, _) => await Guarded("The heartbeat", PulseAsync);
        npcapTimer.Tick += async (_, _) => await Guarded("The Npcap check", NpcapTickAsync);
        publishDelay.Tick += async (_, _) => await Guarded("Sending the reading", async () => { publishDelay.Stop(); AttributePending(); RenderHero(); await PublishLatestAsync(); });
        // Every second: a failure is logged once per distinct message and the next tick runs as usual (#83).
        clock.Tick += (_, _) => GuardedTick("The one-second tick", Tick);
        clock.Start();
        if (!App.Headless) Microsoft.Win32.SystemEvents.PowerModeChanged += Resumed;
        ShowConnected(false); RenderRooms(); RenderParty(); RenderHero(); RenderUpdate();
        preferences = saved ?? PreferencesStore.Load(); hiddenAnnouncement = preferences.HiddenAnnouncement; settings.ConfirmQuitToggle.IsChecked = preferences.ConfirmQuit; SetStayOnTop(preferences.StayOnTop, persist: false);
        settings.FollowGameToggle.IsChecked = preferences.FollowGame; SetDiscordStatus(preferences.DiscordStatus, persist: false); SetFadeInGame(preferences.FadeInGame, persist: false); SetTasksShown(preferences.ShowTasks, persist: false); RenderBackdrop(); RestorePosition();
        if (preferences.CompactMode) SetCompact(true, persist: false);
        // After an update the card says so once. A first start begins at this version; a PC that ran
        // Odyle before (it is paired) hears of this one.
        if (preferences.SeenVersion.Length == 0) PreferencesStore.Save(preferences = preferences with { SeenVersion = File.Exists(SessionStore.FilePath) ? "earlier" : VersionText });
        RenderUpdated();
        settings.CompactHotkeyInput.ItemsSource = CompactStrip.Hotkeys.Select(h => new LanguageChoice(h, h.Length > 0 ? h : T("Off"))).ToArray();
        settings.CompactHotkeyInput.SelectedValue = CompactStrip.Hotkeys.Contains(preferences.CompactHotkey) ? preferences.CompactHotkey : CompactStrip.DefaultHotkey;
        settings.CompactHotkeyInput.SelectionChanged += (_, _) => ChooseHotkey(settings.CompactHotkeyInput.SelectedValue as string ?? "");
        UseHotkey(preferences.CompactHotkey);
        // Automatic follows the Windows display language; a change applies when the app restarts.
        settings.LanguageInput.ItemsSource = new[] { new LanguageChoice(Automatic, T("Automatic · Windows language")), new("en", "English"), new("cs", "Čeština") };
        settings.LanguageInput.SelectedValue = preferences.Language is "en" or "cs" ? preferences.Language : Automatic;
        settings.LanguageInput.SelectionChanged += (_, _) => ChooseLanguage(settings.LanguageInput.SelectedValue as string ?? Automatic);
        settings.LanguageRestartButton.Click += async (_, _) => await Guarded("Restart for the language", () => QuitAsync(restart: true));
    }
    private void ChooseLanguage(string choice)
    {
        if (choice == (preferences.Language is "en" or "cs" ? preferences.Language : Automatic)) return;
        PreferencesStore.Save(preferences = preferences with { Language = choice });
        settings.LanguageRestartPanel.Visibility = Resolve(choice, System.Globalization.CultureInfo.CurrentUICulture) != Localizer.Language ? Visibility.Visible : Visibility.Collapsed;
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e); NativeWindows.DarkFrame(this); NativeWindows.RoundCorners(this);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(layering.Hook); source?.AddHook(backdrop.Hook);
        // Windows 11 draws a border around the frameless window; Windows 10 does not, so the card draws its own.
        RenderBackdrop();
    }
    private readonly NativeWindows.Layering layering = new();
    private readonly NativeWindows.Backdrop backdrop = new();
    private readonly DispatcherTimer acrylicSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // The frameless card is moved by its header, and in compact mode by any of it.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Drag(e);
    private void Drag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    // ✕ closes to the tray like the old title bar's close button (Window_Closing hides the window).
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
    // The see-through background (Settings → Window): Windows blurs what is behind the card, and the card's
    // background is drawn over it at the chosen opacity (100 % looks as without it).
    private void SetAcrylic(bool on, int opacity, bool persist)
    {
        preferences = preferences with { Acrylic = on, AcrylicOpacity = Math.Clamp(opacity, 0, 100) };
        if (persist) { PreferencesStore.Save(preferences); Log.Info(on ? $"The acrylic background is on at {preferences.AcrylicOpacity} %." : "The acrylic background is off."); }
        RenderBackdrop();
    }
    private void RenderBackdrop()
    {
        var supported = NativeWindows.Backdrop.Supported; var on = preferences.Acrylic && supported;
        settings.AcrylicToggle.IsChecked = preferences.Acrylic; settings.AcrylicToggle.IsEnabled = supported;
        settings.AcrylicOpacityPanel.IsEnabled = on; settings.AcrylicOpacityLabel.Text = $"{preferences.AcrylicOpacity} %";
        if (Math.Abs(settings.AcrylicOpacityInput.Value - preferences.AcrylicOpacity) > 0.5) settings.AcrylicOpacityInput.Value = preferences.AcrylicOpacity;
        settings.AcrylicNoteLabel.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        // Compact mode's chips float over the game (variant C): the window is clear around them, with no blur, border,
        // corners or shadow; the card is the frosted (or solid) panel. Windows 11 draws the card's border, Windows 10 not.
        var solid = ((SolidColorBrush)Brush("Bg")).Color;
        Background = compactMode ? Brushes.Transparent : on ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(preferences.AcrylicOpacity * 2.55), solid.R, solid.G, solid.B)) : Brush("Bg");
        backdrop.Use(new WindowInteropHelper(this).Handle, on, transparent: compactMode);
        NativeWindows.Floating(this, compactMode);
        RootBorder.BorderThickness = new Thickness(!compactMode && Environment.OSVersion.Version.Build < 22000 ? 1 : 0);
        // Settings, in the card's style, wear the same background (it has no compact mode).
        settings.Background = on ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(preferences.AcrylicOpacity * 2.55), solid.R, solid.G, solid.B)) : Brush("Bg");
        settingsBackdrop.Use(new WindowInteropHelper(settings).Handle, on);
        if (whatsNew is { } news) { news.Background = settings.Background; newsBackdrop.Use(new WindowInteropHelper(news).Handle, on); }
    }
    // The settings window is frameless like the card: its rounded corners and dark border, the plain blur while it is
    // dragged, and the background again each time it shows (Windows may drop a backdrop set before a window is shown).
    private readonly NativeWindows.Backdrop settingsBackdrop = new(), newsBackdrop = new();
    private void SettingsFrame() => Frame(settings, settingsBackdrop);
    // A frameless window beside the card (Settings, What's new): rounded corners, the dark border, the plain blur while
    // it is dragged, and its background again each time it shows.
    private void Frame(Window window, NativeWindows.Backdrop backdrop)
    {
        NativeWindows.DarkFrame(window); NativeWindows.RoundCorners(window);
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(backdrop.Hook);
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) RenderBackdrop(); };
        RenderBackdrop();
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); _ = RefreshPartyAsync(); _ = RefreshTasksAsync(); }
    // A second start of Odyle signals this one (App) to show its window in front.
    // Open in Odyle on the dashboard (odyle://pair?link=…). The link names the server, and any page could
    // open such a link, so Odyle asks before pairing with it and says when that replaces this PC's pairing.
    public async void OfferPairing(string link) => await Guarded("An Open in Odyle link", () => OfferPairingAsync(link));
    private async Task OfferPairingAsync(string link)
    {
        BringToFront();
        ConnectionDetails details;
        try { details = ConnectionDetails.FromAppLink(link); }
        catch (ArgumentException error) { Status(error.Message); Log.Warn("An Open in Odyle link was not taken: " + error.Message); return; }
        var host = new Uri(details.Endpoint).Authority;
        var replaces = api is not null && accountName.Length > 0 ? "\n\n" + T("This replaces this PC's pairing with {0}.", accountName) : "";
        if (MessageBox.Show(this, T("Pair Odyle on this PC with the server {0}?", host) + replaces, "Odyle", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { Log.Info("An Open in Odyle link for " + host + " was declined."); return; }
        Log.Info("Pairing from an Open in Odyle link for " + host + ".");
        // A link that comes while the start-up connection runs waits for it instead of being dropped (#79).
        await ExecuteAsync(() => { settings.EndpointInput.Text = details.Endpoint; settings.CodeInput.Password = details.Code; return PairAsync(); }, queue: true);
    }
    public void BringToFront()
    {
        ShowWindow();
        if (!Topmost) { Topmost = true; Topmost = false; }
        Focus();
    }
    private void OpenSettings()
    {
        // Checked each time: Npcap may have been installed or removed in the meantime.
        settings.AutostartToggle.IsChecked = Autostart.Enabled; RenderNpcap();
        if (settings.Owner is null && IsVisible) settings.Owner = this;
        settings.Show(); settings.WindowState = WindowState.Normal; settings.Activate();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    // The footer says only what needs attention; "" clears it once that is over (connected again, saved).
    // An empty status leaves the footer to the notice of a server on an older dashboard (ServerNotice).
    private void Status(string message)
    {
        StatusLabel.Text = message.Length > 0 ? message : ServerNotice;
        if (message != lastStatus) { lastStatus = message; if (message.Length > 0) { Log.Info("Status: " + message); Translation.Announce(StatusLabel); } }
    }
    // What a failed request means for the person at the PC, in their language.
    internal static string Describe(Exception error) => error switch
    {
        ServerBusyException busy => T("Server busy · retrying in {0}.", Wait(busy.Until - DateTimeOffset.UtcNow)),
        HttpRequestException { StatusCode: null } => T("Cannot reach the server · check the internet connection."),
        TaskCanceledException => T("The server did not answer in time."),
        _ => T(error.Message),
    };
    private Brush Brush(string key) => (Brush)FindResource(key);
    private Character? CurrentCharacter => Array.Find(characters, c => c.Id == currentCharacterId);
    private void ShowConnected(bool connected)
    {
        settings.JoinPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        settings.AccountPanel.Visibility = settings.RoomsPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        RenderReaderButton();
    }
    // Compact toggle beside the reader status: mint to start or connect, quiet while reading.
    // While the server pauses readers there is nothing to start.
    private void RenderReaderButton()
    {
        ReportButton.Content = capture is not null ? T("Stop") : api is not null || Offline ? T("Start") : T("Connect");
        if (capture is not null) ReportButton.Style = (Style)FindResource("Secondary"); else ReportButton.ClearValue(StyleProperty);
        ReportButton.IsEnabled = !busy && !(readerPaused && api is not null && capture is null);
        RenderChip();
    }

    // ----- Presentation -----
    private void SetReaderState(ReaderState state, string message)
    {
        readerState = state;
        // Following the game (W#18): the card comes up with it and goes to the tray after it.
        var follow = FollowStep(gamePresent, state); var wasPresent = gamePresent;
        gamePresent = state is ReaderState.Listening or ReaderState.Midstream or ReaderState.Live or ReaderState.Stale || (gamePresent && state is not (ReaderState.Waiting or ReaderState.Stopped));
        if (preferences.FollowGame && follow.Length > 0 && !App.Headless) FollowGame(follow == "show");
        if (gamePresent && !wasPresent) gameSince = DateTimeOffset.UtcNow; else if (!gamePresent) gameSince = null;
        var (dot, text) = state switch
        {
            ReaderState.Stopped => ("Faint", "Muted"),
            ReaderState.Waiting => ("Amber", "Muted"),
            ReaderState.Listening => ("MintSoft", "Muted"),
            ReaderState.Midstream => ("Amber", "Amber"),
            ReaderState.Live => ("Mint", "Text"),
            ReaderState.Stale => ("Amber", "Muted"),
            ReaderState.Paused => ("Amber", "Amber"),
            _ => ("Rose", "Rose"),
        };
        ReaderDot.Fill = Brush(dot); PacketStateLabel.Foreground = Brush(text);
        // Variant C: the reader's tile says it in a word, and the line under the tiles only shows while it is not live.
        ReaderChipDot.Fill = Brush(dot);
        ReaderChipText.Text = state switch
        {
            ReaderState.Live => T("On"),
            ReaderState.Listening or ReaderState.Midstream => T("Listening"),
            ReaderState.Waiting => T("Waiting"),
            ReaderState.Stale => T("Stale"),
            ReaderState.Paused => T("Paused"),
            ReaderState.Problem => T("Problem"),
            _ => T("Off"),
        };
        ReaderChipText.Foreground = Brush(state == ReaderState.Live ? "Text" : text);
        ReaderLine.Visibility = state == ReaderState.Problem ? Visibility.Visible : Visibility.Collapsed;
        RenderChip();
        tray.ShowState(state == ReaderState.Stopped ? null : dot);
        if (PacketStateLabel.Text != message) { PacketStateLabel.Text = message; Translation.Announce(PacketStateLabel); }
        // In the tray, a reader that cannot read says so once (odyle-dashboard#48): Npcap missing or
        // restricted, a capture that failed. It reads again: the next problem is told again.
        if (state == ReaderState.Live) problemNoticed = "";
        else if (state == ReaderState.Problem && !IsVisible && reminders.Settings.Where.Tray && message != problemNoticed)
        {
            problemNoticed = message; Notify(T("Odyle cannot read the game"), message);
        }
        // The Npcap link belongs to the line that says Npcap is missing or restricted (ShowNpcapProblem).
        NpcapLink.Visibility = Visibility.Collapsed;
        if (message != lastReaderLog)
        {
            lastReaderLog = message; Log.Info($"Reader {state}: {message}");
            readerHistory.Enqueue($"{DateTime.Now:HH:mm:ss} {state} · {message}"); while (readerHistory.Count > 10) readerHistory.Dequeue();
        }
    }
    private void SetPill(string dot, string text, string label)
    {
        // The state as a word under the name, in its colour, and the dot beside the name.
        StateDot.Fill = Brush(dot); StateText.Foreground = Brush(text); StateText.Text = label;
        StateCaps.Text = label.ToLower(Culture); StateCaps.Foreground = Brush(text);
    }
    // The reader's tile: its tooltip is the reader line, and a click starts or stops the reader like the line's button.
    private void RenderChip()
    {
        var line = PacketStateLabel.Text;
        ReaderChip.ToolTip = capture is not null ? T("{0} · click to stop the reader", line) : api is not null || Offline ? T("{0} · click to start the reader", line) : line;
        System.Windows.Automation.AutomationProperties.SetName(ReaderChip, capture is not null ? T("Stop reader") : T("Start reader"));
        ReaderChip.IsEnabled = ReportButton.IsEnabled;
    }
    // "TestHero · Lumiel" with the server quieter; heroTitle keeps the whole line (Text does not, once it is in runs).
    private string heroTitle = "";
    private void ShowHeroTitle(string title)
    {
        if (heroTitle == title && HeroTitle.Inlines.Count > 0) return;
        heroTitle = title;
        var at = title.IndexOf(" · ", StringComparison.Ordinal);
        HeroTitle.Inlines.Clear();
        if (at <= 0) { HeroTitle.Inlines.Add(new System.Windows.Documents.Run(title)); return; }
        HeroTitle.Inlines.Add(new System.Windows.Documents.Run(title[..at]));
        HeroTitle.Inlines.Add(new System.Windows.Documents.Run(title[at..]) { FontWeight = FontWeights.Normal, FontSize = 11, Foreground = Brush("Muted") });
    }
    // A shorter countdown for compact mode's pill: hours and minutes, 1:12 (0:04 under an hour).
    private static string Hours(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return $"{(int)left.TotalHours}:{left.Minutes:00}";
    }
    // A countdown to the second for the card's HUD lines: 1:12:04, or 4:05 under an hour.
    private static string Countdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }
    // The tray menu as it opens: the reader's state, Start or Stop reader, whether the tasks show,
    // and the account's characters, the fullest first (equal ones: the longest unread first), each
    // with its value, cubes at the card's cost and when it was read; a row opens the card.
    private void TrayMenuOpening()
    {
        var state = PacketStateLabel.Text is { Length: > 0 } text ? text : T("Not connected");
        tray.Status.Text = state.Length > 60 ? state[..59] + "…" : state;
        tray.Reader.Text = capture is not null ? T("Stop reader") : T("Start reader");
        tray.Reader.Enabled = capture is not null || api is not null || Offline;
        tray.Tasks.Checked = TasksShown; tray.Tasks.Enabled = !compactMode;
        tray.ListCharacters(ListedCharacters().Select(c => $"{c.CharacterName} · {CharacterDetail(c)}").ToArray());
    }
    // The account's characters as the tray menu and the card list them: the fullest first (equal
    // ones: the longest unread first), at most 20; each with its Odyle, cubes at the card's cost
    // and when it was read.
    private Character[] ListedCharacters() => characters.Where(c => c.CharacterName.Length > 0)
        .OrderByDescending(c => c.Value).ThenBy(c => Readiness.Parse(c.LastReadingAt) ?? DateTimeOffset.MinValue).Take(20).ToArray();
    private string CharacterDetail(Character c)
    {
        var cost = HeroCost(c);
        var read = Readiness.Parse(c.LastReadingAt) is { } at ? " · " + Age(at) : "";
        return $"{c.Value.ToString("N0", Culture)} Odyle · {Tn(cost > 0 ? c.Value / cost : 0, "{0} cube", "{0} cubes")}{read}";
    }
    // The card's "Your characters" (W#17): with two or more characters, a toggle under the reading
    // that lists them. It opens on demand, and by itself while the game switches characters, until
    // it names the next one (then it closes again, unless it was opened by hand).
    private sealed record AltRow(string Name, string Detail, Brush Mark, string? Hint);
    private bool altsOpened;
    private void RenderAlts()
    {
        var listed = (api is not null || Offline) ? ListedCharacters() : [];
        var open = listed.Length > 1 && (altsOpened || (capture is not null && attribution.GameKey == ReaderAttribution.UnknownOwner));
        AltToggle.Visibility = listed.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        AltToggle.Content = T("Your characters ({0})", listed.Length) + (open ? " ▴" : " ▾");
        System.Windows.Automation.AutomationProperties.SetName(AltToggle, T("Your characters ({0})", listed.Length));
        ShowRows(AltList, open ? listed.Select(c =>
        {
            // A natural pool near its cap colours the row as on the card.
            var level = OdyleClock.Level(SavedClock(c), DateTimeOffset.UtcNow);
            return new AltRow(c.CharacterName, CharacterDetail(c), CapBrush(level, "Faint"), level switch
            {
                CapLevel.Soon => T("{0} · natural pool full within two days", c.CharacterName),
                CapLevel.Full => T("{0} · natural pool full, or full within the hour", c.CharacterName),
                _ => null,
            });
        }).ToArray() : null);
        AltList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AltToggle_Click(object sender, RoutedEventArgs e) { altsOpened = AltList.Visibility != Visibility.Visible; RenderAlts(); }
    private static string Age(DateTimeOffset moment)
    {
        var seconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - moment).TotalSeconds);
        return seconds < 60 ? T("{0}s ago", seconds) : seconds < 3600 ? T("{0}m ago", seconds / 60) : seconds < 86400 ? T("{0}h ago", seconds / 3600) : T("{0}d ago", seconds / 86400);
    }
    // Every second, what changes with the time alone (#84): the pill's Sending or Stale while the reader runs, the
    // ages, the Odyle clock and the taskbar, the characters list while open, compact mode's rows and the countdowns.
    // Everything else renders when what it shows changes. Hidden in the tray, only what leaves the window goes on
    // (the next run, for the Discord status); showing the window renders the card at once (Window_IsVisibleChanged).
    private void Tick()
    {
        if (IsVisible)
        {
            if (capture is not null) { RenderPill(); if (AltList.Visibility == Visibility.Visible) RenderAlts(); }
            RenderClock(); RenderCompact(); TickTasks(); RenderRetry(); RenderBusy();
        }
        RenderRunChip(); RenderDiscord();
    }
    // A list's rows are replaced only when they show something else: a render that changes nothing keeps the
    // list's items, and the tooltip open on one of them stays open (#84).
    private static void ShowRows(ItemsControl list, object[]? rows) { if (!SameRows(list.ItemsSource as object[], rows)) list.ItemsSource = rows; }
    // Rows are records, equal when they show the same; the rows inside a room or a frame compare one by one.
    private static bool SameRows(object? a, object? b) => (a, b) switch
    {
        (object[] x, object[] y) => x.Length == y.Length && x.Zip(y).All(pair => SameRows(pair.First, pair.Second)),
        (PartyRoom x, PartyRoom y) => x.Name == y.Name && x.Summary == y.Summary && Equals(x.SummaryBrush, y.SummaryBrush) && SameRows(x.Mates, y.Mates) && SameRows(x.Entries, y.Entries),
        (MateBand x, MateBand y) => x.Label == y.Label && x.Count == y.Count && x.Icon == y.Icon && Equals(x.Accent, y.Accent) && Equals(x.Edge, y.Edge) && Equals(x.Fill, y.Fill) && x.Gap == y.Gap && x.Pad == y.Pad && SameRows(x.Items, y.Items),
        _ => Equals(a, b),
    };
    private void RenderHero()
    {
        RenderAlts();
        // Offline: paired, but the server has not confirmed the pairing yet (it is tried again by itself);
        // the reader reads locally and what it read is sent once the server answers.
        var connected = api is not null; var offline = Offline;
        var character = CurrentCharacter;
        var cost = HeroCost(character);
        var reading = capture is not null ? attribution.Latest : null;
        // A reading whose owner is not yet the shown character is displayed, but never under its name.
        var owned = reading is not null && attribution.OwnerSaved;
        int? value = reading?.Total ?? (character is { Confirmed: true } ? character.Value : null);
        // The header keeps room for its buttons: it names a state, not the account (Settings does).
        AccountLabel.Text = offline ? T("OFFLINE") : !connected ? T("NOT CONNECTED") : accountName.Length > 0 ? "" : T("CONNECTING…");
        AccountLabel.Visibility = AccountLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; RenderChip();
        ShowHeroTitle(!connected && !offline ? T("Not connected")
            : offline && reading is not null ? (attribution.GameName.Length > 0 && attribution.OwnerKey == attribution.GameKey ? T("{0} · offline", attribution.GameName) : T("Detecting character…"))
            : reading is not null && !owned ? (attribution.GameName.Length > 0 && attribution.GameKey != attribution.DetectedKey && attribution.OwnerKey == attribution.GameKey ? T("{0} · not saved", attribution.GameName) : T("Detecting character…"))
            : character is not null ? $"{character.CharacterName} · {character.Server}" : T("No character yet"));
        ValueLabel.Text = value is { } v ? v.ToString("N0", Culture) : "—";
        // The inner ring fills with the natural pool towards its cap, with the clock (RenderOdyleClock); the blue one
        // around it with what items added, below.
        EnergyTrack.Visibility = BonusTrack.Visibility = connected || offline ? Visibility.Visible : Visibility.Hidden;
        // Variant C: the cubes large with their word beside the ring, and how far to the next one under them.
        var cubesLine = value is { } total ? Tn(total / cost, "{0} cube", "{0} cubes") + $" · {total % cost}/{cost}" : connected ? T("{0} per cube", cost) : "";
        CubeLabel.Text = value is { } whole ? (whole / cost).ToString("N0", Culture) : connected || offline ? "—" : "";
        CubeWord.Text = value is { } count ? Tn(count / cost, "cube", "cubes") : connected || offline ? T("cubes") : "";
        RefillCaption.Text = value is { } part ? T("{0}/{1} to the next cube", part % cost, cost) : cubesLine;
        // With several rooms the count is at the cost of the first one the character is shared in; the tooltip names it.
        CubeLabel.ToolTip = RefillCaption.ToolTip = cubesLine.Length == 0 ? null : rooms.Length > 1 && HeroRoom(character) is { } costRoom ? T("{0} · at the cost of {1}", cubesLine, costRoom.Name) : cubesLine;
        // The game's two numbers: the natural pool and what items added, and the last change it made.
        var (natural, items) = reading is not null ? (reading.Base, reading.Bonus) : (character?.Parts?.Base, character?.Parts?.Bonus);
        // The blue ring: what items added, of the most they can hold (the server's rules), for the value the card shows.
        BonusTrack.Value = value is null ? 0 : OdyleClock.BonusPart(items, clockRules) * 100;
        var split = (connected || offline) && natural is { } n && items is { } i ? T("natural {0:N0} · items {1:N0}", n, i) : "";
        if (split.Length > 0 && lastChange is { } last && last.CharacterId == character?.Id && (reading is null || owned)) split += " · " + T("{0} at {1:HH:mm}", last.Delta.ToString("+#,0;−#,0", Culture), last.At);
        SplitLabel.Text = split; ToolTipService.SetIsEnabled(RingBox, split.Length > 0);
        if (tasksLoaded && CurrentCharacter?.Id != renderedTasksCharacter) RenderTasks();
        // A character your rooms cannot see: you might think the party sees you when it does not.
        var unseen = connected && character is { Id.Length: > 0 } && rooms.Length > 0 && !rooms.Any(r => r.Shares(character.Id));
        ShareHintButton.Visibility = unseen ? Visibility.Visible : Visibility.Collapsed;
        RenderPill();
        ReadingLabel.Text = offline ? (reading is not null ? T("Offline · {0:N0} Odyle read here, sent when the server is back.", reading.Total) : T("Offline · the reader reads locally and sends what it reads when the server is back."))
            : !connected ? T("Open settings to pair with the dashboard.")
            // The operators' announcement, when there is one, says why; it moves into the card meanwhile.
            : readerPaused ? (announcement is { } reason ? T("Paused by the server · {0}", reason.Text) : T("Paused by the server · reading resumes by itself when the operators allow it again."))
            : reading is not null && !owned ? (attribution.GameKey.Length == 0 && !attribution.NameDue ? T("The game names the character within a few minutes, or at once after a relog or channel change.") : T("Waiting for the game to name the character."))
            : reading is not null ? (connection.AdapterOutdated ? OutdatedHint : publishFailure ?? "")
            // The game's snapshot of the named character has no Odyle record; it is thought to unlock at level 22.
            // Waiting for the snapshot needs no caption: the reader line says so.
            : capture is not null ? (attribution.OdyleMissingKey.Length > 0 && attribution.OdyleMissingKey == attribution.GameKey ? T("No Odyle on {0} yet · it may unlock at level 22.", attribution.GameName)
                : character is null ? T("Log into a character · it is detected automatically.") : "")
            : character is null ? T("Start the reader, then log into your character.")
            : character.Confirmed ? T("Last saved value · start the reader for live values.")
            : T("No reading yet · start the reader before logging in.");
        // A saved live reading needs no caption: the card shows the total only.
        ReadingLabel.Visibility = ReadingLabel.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderClock();
        var name = character?.CharacterName ?? accountName;
        var tip = offline ? T("Odyle · Offline") : !connected ? T("Odyle · Not connected") : value is { } shown ? $"Odyle · {name} · {shown.ToString("N0", Culture)}" : $"Odyle · {name}";
        tray.ShowTip(tip);
        heroValue = value; heroCost = cost; RenderCompact();
    }
    // The pill beside the value: what happens to the reading on the card. Sending turns Stale with the time.
    private void RenderPill()
    {
        var character = CurrentCharacter; var reading = capture is not null ? attribution.Latest : null;
        var owned = reading is not null && attribution.OwnerSaved;
        if (api is null) SetPill("Faint", "Muted", T("Offline"));
        else if (readerPaused) SetPill("Amber", "Amber", T("Paused"));
        else if (reading is not null && publishFailure is not null) SetPill("Rose", "Rose", T("Not saved"));
        else if (owned && activeCharacterId.Length > 0) SetPill("Mint", "Mint", T("Live"));
        // A snapshot waits for the loading screen to end before the game names its character.
        else if (reading is not null) SetPill("Amber", "Amber", DateTimeOffset.UtcNow - attribution.LatestAt < TimeSpan.FromSeconds(attribution.NameDue ? 30 : 6) ? T("Sending") : T("Stale"));
        else if (capture is not null) SetPill("MintSoft", "MintSoft", T("Listening"));
        else if (character is null) SetPill("Muted", "Muted", T("No character"));
        else if (character.Confirmed) SetPill("Muted", "Muted", T("Saved"));
        else SetPill("Muted", "Muted", T("No reading"));
    }
    // Paired, but the server has not confirmed the pairing yet: it is tried again by itself (retryAt).
    private bool Offline => link.Offline;
    private string OutdatedHint => updates.Staged is not null ? T("Odyle needs an update to keep reading · Restart to update installs it.") : T("Odyle needs an update to keep reading · it looks for one every hour.");
    // Cube cost of the first room the character is shared in, else the first room.
    private int HeroCost(Character? character) => HeroRoom(character)?.Cost is > 0 and var cost ? cost : odylePerCube;
    // The room whose cost the card counts cubes with: the first one the character is shared in.
    private Room? HeroRoom(Character? character) => (character is null ? null : Array.Find(rooms, r => r.Shares(character.Id))) ?? (rooms.Length > 0 ? rooms[0] : null);
    // Sharing is configured on the dashboard; settings only summarizes it.
    private void RenderRooms()
    {
        settings.SharingList.ItemsSource = rooms.Length == 0 ? Array.Empty<CharacterSharing>() : characters.Select(c =>
        {
            var shared = rooms.Where(r => r.Shares(c.Id)).Select(r => r.Name).ToArray();
            return new CharacterSharing($"{c.CharacterName} · {c.Server}", shared.Length == 0 ? T("Private in every room") : T("Shared in {0}", string.Join(", ", shared)));
        }).ToArray();
        settings.NoRoomsLabel.Text = characters.Length == 0 ? T("No characters yet · start the reader and log into the game.") : rooms.Length == 0 ? T("No rooms yet · create or join one on the dashboard.") : "";
        settings.NoRoomsLabel.Visibility = settings.NoRoomsLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    // Other members' characters that are live or recently stale, per room. Offline
    // characters and your own are left out; your own is the card above.
    private void RenderParty()
    {
        var now = DateTimeOffset.UtcNow + serverOffset;
        // Each room with characters: its party line (your character included, same rules as the
        // dashboard) and the other members' characters online, the players of one party or force
        // in the game together (yours first).
        var groups = snapshots.Select(s =>
            {
                var cost = s.Room.Cost > 0 ? s.Room.Cost : odylePerCube; var party = PartyReadiness.Party(PartyReadiness.Entries(s), now, cost);
                var listed = s.Members.Where(m => m.User != accountId)
                    .SelectMany(m => m.Characters.Where(c => c.Shared ?? true).Select(c => (Member: m, Character: c, State: Readiness.Of(c, now))))
                    .Where(x => x.State is Presence.Live or Presence.Stale)
                    .OrderBy(x => x.State).ThenByDescending(x => x.Character.Value).ToArray();
                var (_, rows) = GameBands.Arrange(listed, x => (x.Member.User, x.State == Presence.Live ? x.Character.GameGroup : null), accountId, GameBands.Own([s], accountId, now));
                var cards = rows.Select(r => (Card: Mate(r.Item.Member, r.Item.Character, r.Item.State, s.Room.Cost, now, r.Band), r.Band)).ToArray();
                return (Party: party, Room: new PartyRoom(s.Room.Name, cards.Select(c => c.Card).ToArray(), RunChip.PartyLine(party, accountId), Brush(party.Cubes > 0 ? "Mint" : "Muted"), CardEntries(GameBands.Nest(cards), top: true)));
            })
            .Where(g => g.Party.Characters.Length > 0).Select(g => g.Room).ToArray();
        ShowRows(PartyList, groups);
        PartyPanel.Visibility = api is not null && rooms.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // A room listed with "0 online" says it already; the line is for when no room has anyone to list.
        PartyEmptyLabel.Visibility = groups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Live updates are the rule and go unmentioned; only a party fetched every 15 seconds says so.
        var live = realtime?.IsConnected == true;
        PartyModeLabel.Visibility = live ? Visibility.Collapsed : Visibility.Visible; PartyModeLabel.Text = T("refreshed every 15 s");
        PartyModeLabel.ToolTip = realtime?.LastProblem is { } problem ? T("Live updates are off: {0}", T(problem)) : T("Live updates are not connected; the party is fetched every 15 seconds.");
        RenderRunChip(); RenderCompact();
    }
    // The next planned run under the card: soonest first, ones you go to before others, with a
    // countdown and Going. A run you were going to that was cancelled or moved says so for 30 minutes.
    private NextRun? chipRun; private string chipNotice = ""; private DateTimeOffset chipNoticeUntil;
    private void RenderRunChip()
    {
        var now = DateTimeOffset.UtcNow + serverOffset;
        if (RunChip.Change(chipRun, snapshots, now) is { } change) { chipNotice = change; chipNoticeUntil = now.AddMinutes(30); }
        var next = api is null ? null : RunChip.Pick(snapshots, accountId, now);
        chipRun = next;
        // Variant C: the RUN tile counts down to it to the second.
        RunCountdown.Text = next is null ? "—" : next.StartsAt > now ? Countdown(next.StartsAt - now) : T("now");
        var notice = now < chipNoticeUntil ? chipNotice : "";
        RunChipBox.Visibility = next is not null || notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunChipLabel.Text = next is null ? "" : RunChip.Line(next, now); RunChipLabel.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        RunChipNotice.Text = notice; RunChipNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var viewer = next is not null && next.Room.Role == "viewer";
        RunGoingButton.Visibility = next is null || viewer ? Visibility.Collapsed : Visibility.Visible;
        RunGoingButton.Content = next is { Going: true } ? T("Going ✓") : T("Join");
        // Join stands out in mint; once you go, the button quietens.
        if (next is { Going: true }) RunGoingButton.Style = (Style)FindResource("Secondary"); else RunGoingButton.ClearValue(StyleProperty);
        RunGoingButton.ToolTip = next is { Going: true } ? T("You are going · click to leave this run") : T("Go to this run");
    }
    private async void RunGoing_Click(object sender, RoutedEventArgs e) => await Guarded("Going to a planned run", async () =>
    {
        if (api is not { } current || chipRun is not { } run) return;
        RunGoingButton.IsEnabled = false;
        try { await current.GoingAsync(run.Room.Id, run.Run.Id, !run.Going); Log.Info($"{(run.Going ? "Left" : "Joined")} the planned run {run.Run.Id}."); await RefreshPartyAsync(); }
        // ObjectDisposedException: the account was disconnected meanwhile.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or ObjectDisposedException) { Status(T(error.Message)); }
        finally { RunGoingButton.IsEnabled = true; }
    });
    private PartyMate Mate(RoomMember member, Character c, Presence state, int cost, DateTimeOffset now, Band band)
    {
        cost = cost > 0 ? cost : odylePerCube;
        var live = state == Presence.Live; var cubes = c.Value / cost;
        var seen = !live && Readiness.Parse(c.LastSeenAt) is { } at ? " · " + T("seen {0}", Age(at)) : "";
        // The class leads the detail, so a narrow row trims the server rather than the class. A place
        // the player shares takes the server's place (the player's name stays in the tooltip).
        var place = PlaceLine(c.Location);
        var where = place.Length > 0 ? "" : c.CharacterName == member.DisplayName ? c.ServerLine : $"{member.DisplayName} · {c.Server}";
        var detail = string.Join(" · ", new[] { c.ClassLine, where }.Where(part => part.Length > 0));
        var key = c.Id.Length > 0 ? c.Id : member.User + "|" + c.CharacterName; var open = openMates.Contains(key);
        return new PartyMate(c.CharacterName, live ? detail : T("stale") + seen + (detail.Length > 0 ? " · " + detail : ""),
            c.Value.ToString("N0", Culture), Tn(cubes, "{0} cube", "{0} cubes"), Brush(live ? "Mint" : "Amber"),
            (c.ClassLine.Length > 0 ? c.ClassLine + " · " : "") + (live ? T("{0} · live from the game", member.DisplayName) : T("{0} · last value; their reader stopped reporting", member.DisplayName)) + BandHint(band), place)
        {
            // The ring: the natural pool towards its cap, from the last saved reading (an estimate, as the card's for a saved character).
            Key = key, Open = open, Progress = OdyleClock.NaturalPart(SavedClock(c)) * 100, Bar = Brush(live ? "Mint" : "StaleFill"),
            // Your party's players, or how long ago a stale one was seen, after the name. Your force's other players go
            // untagged: its violet frame says it once, where a tag on every row of a big force only repeated it.
            Tag = !live ? (Readiness.Parse(c.LastSeenAt) is { } lastSeen ? Age(lastSeen) : T("stale")) : band.YourParty ? T("party") : "",
            TagBrush = Brush(!live ? "Amber" : "PartyBand"),
            ToNext = T("{0}/{1} to the next cube", c.Value % cost, cost), Toggle = open ? T("{0} · hide the details", c.CharacterName) : T("{0} · show the details", c.CharacterName),
        };
    }
    // A party row opens its details in place on a click and closes them on the next; nothing pops up. Which
    // bars are open is kept by character while the window runs.
    private readonly HashSet<string> openMates = [];
    private void Mate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || key.Length == 0) return;
        if (!openMates.Remove(key)) openMates.Add(key);
        RenderParty();
    }
    // ----- The players the game put together -----
    // Framed as on the dashboard's room page (GameBands.Nest): a party in a blue frame, a force in a
    // violet one around its parties, yours first. The party list labels each frame with an icon and
    // how many players it holds, you included; compact mode keeps them tight and unlabelled.
    private static string BandLabel<TRow>(Nested<TRow> band) => band.Kind == "force"
        ? band.Yours ? T("In your force in game") : T("In one force in game")
        : band.Yours ? T("In your party in game") : T("In one party in game");
    private MateBand Frame<TRow>(Nested<TRow> band, string count, string icon, string fill, object[] items, Thickness gap, Thickness pad)
    {
        var force = band.Kind == "force";
        return new(BandLabel(band), count, icon, Brush(force ? "ForceBand" : "PartyBand"), Brush(force ? "ForceEdge" : "PartyEdge"), Brush(force ? "Force" + fill : "Party" + fill), items, gap, pad);
    }
    private object[] CardEntries(IEnumerable<Nested<PartyMate>> nested, bool top) => nested.Select(n => n.IsBand
        ? Frame(n, Tn(n.Rows + (n.Yours ? 1 : 0), "{0} player", "{0} players"), n.Kind == "force" ? "\uEA18" : "\uE716", "Wash", CardEntries(n.Children!, top: false), new(0, top ? 5 : 4, 0, top ? 1 : 0), new(6, 0, 6, 6))
        : (object)n.Item!).ToArray();
    // Compact mode's frames reach into the window's margin: a party's 8 px, its text in line with the
    // rows outside; a force's too, a row right inside it set in by 5 px and a party inside it lining up.
    private static (Thickness Gap, Thickness Pad) CompactFrame(string kind, bool top, double gap) => kind == "force"
        ? (new(-8, gap, -4, 0), new(2, 1, 2, 1))
        : top ? (new(-8, gap, -4, 0), new(7, 1, 3, 1)) : (new(0, gap, 0, 0), new(4, 1, 0, 1));
    // The entries inside a frame of kind parent ("" for the list itself), after a row or a frame
    // ("" when first): a frame stands 3 px apart, rows 1 px.
    private object[] CompactEntries(IEnumerable<Nested<CompactStrip.Mate>> nested, string parent, string before, Func<CompactStrip.Mate, CompactMate> row)
    {
        var entries = new List<object>();
        foreach (var n in nested)
        {
            var gap = before.Length == 0 ? (parent.Length > 0 ? 0 : n.IsBand ? 3 : 1) : n.IsBand || before == "frame" ? 3 : 1;
            if (n.IsBand)
            {
                var (margin, pad) = CompactFrame(n.Kind, top: parent.Length == 0, gap);
                entries.Add(Frame(n, "", "", "Tint", CompactEntries(n.Children!, n.Kind, "", row), margin, pad));
            }
            else entries.Add(row(n.Item!) with { Gap = new(0, gap, 0, 0), Pad = parent == "force" ? new(5, 0, 1, 0) : default });
            before = n.IsBand ? "frame" : "row";
        }
        return entries.ToArray();
    }
    // A row's tooltip says which band it is in.
    private static string BandHint(Band band) => string.Concat(
        band.Party.Length > 0 ? "\n" + (band.YourParty ? T("In your party in game") : T("In one party in game")) : "",
        band.Force.Length > 0 ? "\n" + (band.YourForce ? T("In your force in game") : T("In one force in game")) : "");
    // Where a character was last seen (location sharing): the map and how long ago, or "" without a place.
    private string PlaceLine(Place? place) => place is { Map: > 0 } p && Readiness.Parse(p.At) is { } at ? $"{Places.MapName(p.Map, maps)} · {Age(at)}" : "";
    // The card's character's place, ticking with the clock: what your rooms see of it.
    private void RenderPlace()
    {
        var place = api is not null && CurrentCharacter is { Location: { } shared } ? shared : null;
        var line = PlaceLine(place);
        PlaceText.Text = line; PlaceLabel.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var how = place is null ? "" : Places.SourceName(place.Source);
        PlaceLabel.ToolTip = line.Length == 0 ? null : how.Length > 0 ? T("Last place the game reported · {0}", how) : T("Last place the game reported");
    }
    // The Odyle clock under the split: from the reading on screen, or the saved character's last
    // one (an estimate then), with the operators' rules. Ticked every second with the age.
    private void RenderOdyleClock()
    {
        var character = CurrentCharacter; var reading = capture is not null ? attribution.Latest : null;
        var state = CardClock();
        var line = OdyleClock.Line(state, DateTimeOffset.UtcNow, reading is not null);
        // Orange two days before the natural pool is full, red once it is or within the hour.
        var flag = OdyleClock.Flag(state, DateTimeOffset.UtcNow); var level = OdyleClock.Level(state, DateTimeOffset.UtcNow);
        var full = flag.Length > 0 ? $"{flag} · {line}" : line;
        // Variant C: the first tile counts down to the next tick to the second (or says the natural pool is full), and
        // under the tiles the rest of the clock's line (when the pool is full, or that it is and loses each tick).
        var now = DateTimeOffset.UtcNow;
        NextLabel.Text = state is { Full: true } ? T("NATURAL POOL") : T("NEXT +{0}", state?.Tick ?? (clockRules ?? ClockRules.Default).Tick);
        RefillCountdown.Text = state is { Full: true } ? T("full") : state is { Next: { } next } ? Countdown(next - now) : "—";
        var rest = state is null ? "" : OdyleClock.Line(state with { Next = null }, now, reading is not null);
        OdyleClockLabel.Text = flag.Length > 0 && rest.Length > 0 ? $"{flag} · {rest}" : flag.Length > 0 ? flag : rest;
        // Shown on the card only while the pool is nearly full or full; otherwise the first tile's tooltip has the whole line.
        OdyleClockLabel.Visibility = OdyleClockLabel.Text.Length > 0 && (level != CapLevel.None || state is { Full: true }) ? Visibility.Visible : Visibility.Collapsed;
        NextTile.ToolTip = full.Length > 0 ? full : null;
        OdyleClockLabel.Foreground = CapBrush(level, "Muted"); RefillCountdown.Foreground = CapBrush(level, "Mint");
        ValueLabel.Foreground = CompactValue.Foreground = CapBrush(level, "Text");
        // The ring fills with the natural pool towards its cap, as the taskbar button does, and turns orange and red with
        // the clock; without one (paused by the operators, a reading not yet named) it stays empty.
        EnergyTrack.Value = OdyleClock.NaturalPart(state) * 100;
        EnergyTrack.Fill = CompactTrack.Fill = CapBrush(level, "Mint");
        OdyleClockLabel.FontWeight = level == CapLevel.None ? FontWeights.Normal : FontWeights.SemiBold;
        var mismatch = api is not null && character is not null ? OdyleClock.Mismatch(character.NaturalMax, character.AnsweredCap) : "";
        CapMismatchLabel.Text = mismatch; CapMismatchLabel.Visibility = mismatch.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderTaskbar(state, level, full);
    }
    // The clock of the card's character: from the reading on screen once it is that character's, else
    // from its last saved one; null without either.
    private ClockState? CardClock()
    {
        var character = CurrentCharacter; var reading = capture is not null ? attribution.Latest : null;
        var owned = reading is not null && attribution.OwnerSaved;
        if (api is not null && reading is { Base: { } natural, Bonus: { } items } && owned)
            return OdyleClock.Of(natural, items, attribution.LatestAt, DateTimeOffset.UtcNow, clockRules, character?.RegenTick, character?.NaturalMax, character?.AnsweredCap);
        return api is not null && reading is null && character is not null ? SavedClock(character) : null;
    }
    // The taskbar button fills with the natural pool towards its cap: green, yellow two days before it
    // is full, red once it is or within the hour (the card's orange and red); its tooltip is the clock.
    private void RenderTaskbar(ClockState? state, CapLevel level, string line)
    {
        // Set only when they change: the taskbar is told nothing new every second (#84).
        var bar = TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        var progress = state is not { Cap: > 0 } ? System.Windows.Shell.TaskbarItemProgressState.None : level switch
        {
            CapLevel.Soon => System.Windows.Shell.TaskbarItemProgressState.Paused,
            CapLevel.Full => System.Windows.Shell.TaskbarItemProgressState.Error,
            _ => System.Windows.Shell.TaskbarItemProgressState.Normal,
        };
        if (bar.ProgressState != progress) bar.ProgressState = progress;
        var filled = OdyleClock.NaturalPart(state);
        if (bar.ProgressValue != filled) bar.ProgressValue = filled;
        var tip = state is null ? "Odyle" : $"Odyle · {ValueLabel.Text} · {line}";
        if (bar.Description != tip) bar.Description = tip;
    }
    // The Odyle clock of a saved character from its last reading (an estimate), or null.
    private ClockState? SavedClock(Character c) => c is { Confirmed: true, Parts: { } parts } && Readiness.Parse(c.LastReadingAt) is { } saved
        ? OdyleClock.Of(parts.Base, parts.Bonus, saved, DateTimeOffset.UtcNow, clockRules, c.RegenTick, c.NaturalMax, c.AnsweredCap) : null;
    private Brush CapBrush(CapLevel level, string otherwise) => Brush(level switch { CapLevel.Soon => "Orange", CapLevel.Full => "Red", _ => otherwise });
    private void RenderClock()
    {
        var character = CurrentCharacter;
        // A live reading needs no age; a saved value says how old it is.
        AgeLabel.Text = (capture is null || attribution.Latest is null) && character is { Confirmed: true } && Readiness.Parse(character.LastReadingAt) is { } saved ? T("saved {0}", Age(saved)) : "";
        AgeLabel.Visibility = AgeLabel.Text.Length > 0 && (api is not null || Offline) ? Visibility.Visible : Visibility.Collapsed;
        // "saved 40s ago" says the state already: the word beside it would repeat it.
        StateCaps.Visibility = AgeLabel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        AgeLabel.Margin = new Thickness(StateCaps.Visibility == Visibility.Visible ? 6 : 0, 0, 0, 0);
        if (capture is { } running)
            CaptureStatsLabel.Text = T("{0:N0} packets · {1:N0} KB from the game", running.ReceivedPackets, running.GameBytes / 1024.0);
        RenderOdyleClock(); RenderPlace();
    }
    private void ShareHint_Click(object sender, RoutedEventArgs e) => OpenDashboard("/");

    // ----- Operations -----
    private void SetBusy(bool value)
    {
        busy = value; settings.Body.IsEnabled = !value; RenderReaderButton();
    }
    // The outer edge of a click, a timer tick or a dispatched callback (#83): a failure nobody expected is
    // logged, once per distinct message, instead of reaching App.OnUnhandled, which shows a message box and
    // stops Odyle after ten in a minute. Catches inside keep their own wording for the person at the PC.
    private readonly HashSet<string> failuresLogged = [];
    private void LogFailure(string what, Exception error)
    {
        if (failuresLogged.Count > 200) failuresLogged.Clear();
        if (failuresLogged.Add($"{what}|{error.GetType().FullName}|{error.Message}")) Log.Error($"{what} failed; Odyle keeps running", error);
    }
    private async Task Guarded(string what, Func<Task> action) { try { await action(); } catch (Exception error) { LogFailure(what, error); } }
    private void GuardedTick(string what, Action action) { try { action(); } catch (Exception error) { LogFailure(what, error); } }
    // One operation at a time. A click while one runs is dropped (its button is disabled meanwhile), but
    // an action that comes from outside the window (an Open in Odyle link, the tray's or the jump list's
    // Start or stop the reader) waits for it and then runs (queue, #79).
    private readonly SemaphoreSlim executions = new(1, 1);
    private async Task ExecuteAsync(Func<Task> action, bool queue = false)
    {
        if (busy && !queue) return;
        if (busy) Log.Info("An action waits for the running operation to finish.");
        await executions.WaitAsync();
        try
        {
            if (quitting) return;
            SetBusy(true); await operations.WaitAsync();
            try { await action(); }
            catch (Exception error)
            {
                link.NoteFailure(error);
                if (error is not (HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException)) Log.Error("Operation failed", error);
                Status(Describe(error));
            }
            finally { SetBusy(false); operations.Release(); RenderHero(); }
        }
        finally { executions.Release(); }
    }
    // Called by App once the window exists, shown or not. A start in the tray (with
    // Windows) stays there while the app is paired; unpaired, the window and settings open.
    public async Task StartAsync(bool tray)
    {
        // The update check does not wait for the server. (The Odyle.exe.old of the build that installed
        // this one is removed once this start proved healthy: App, Updater start health.)
        if (updates.IsRelease() && !App.Headless) updates.Timer.Start();
        // Npcap missing shows in the reader row from the start, also before the app is paired.
        if (!Npcap.Probe()) ShowNpcapProblem(null);
        await ExecuteAsync(RestoreAsync);
        if (StartupCommand.ShowsWindow(tray, paired: api is not null || link.RetryAt is not null))
        {
            if (!IsVisible) ShowWindow();
            if (api is null && !Offline) OpenSettings();
        }
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Guarded("Reconnect saved account", () => ExecuteAsync(RestoreAsync));
    private async Task RestoreAsync()
    {
        link.StopRetrying();
        if (api is not null) return;
        var saved = SessionStore.Load(out var unreadable);
        if (saved is null)
        {
            if (unreadable) { settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false); Status(T("The connection saved on this PC could not be read. Copy a new connection link from the dashboard to pair again.")); }
            return;
        }
        // A pairing from before device keys (Oct 2026) cannot sign in once its token ends, and servers on dashboard 1.0.0
        // refuse it: this PC is unpaired and pairs again, with the server's address kept in Settings.
        if (string.IsNullOrWhiteSpace(saved.DeviceKey))
        {
            Log.Warn("The saved pairing has no device key (paired before device keys); it was removed, and this PC needs to pair again.");
            SessionStore.Clear(); link.Forget(); settings.EndpointInput.Text = saved.Endpoint; settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false);
            Status(T("This PC was paired with an older Odyle and needs to pair again. Copy a new connection link from the dashboard.")); RenderHero();
            return;
        }
        settings.EndpointInput.Text = link.Endpoint = saved.Endpoint; Status(T("Connecting to your account…"));
        PartyApi next;
        try { next = UseApi(newApi(saved.Endpoint, saved.Token, saved.DeviceKey)); }
        catch (ArgumentException error) { Log.Error("The saved server address is not usable", error); SessionStore.Clear(); link.Forget(); Status(T("The connection saved on this PC could not be read. Copy a new connection link from the dashboard to pair again.")); return; }
        // The login snapshot names the character, so the first try reads before the server is asked (#86). What it
        // reads meanwhile waits for the operations lock this start holds: once the account is connected it is saved
        // and sent, and while the server cannot confirm the pairing it stays on this PC like any offline reading.
        if (capture is null && link.Attempt == 0) await StartReaderAsync();
        try { var auth = await next.SignInAsync(); SavePairing(auth); await ConnectAsync(auth); }
        // Only the server's own 401 means the pairing ended (Disconnect, Sign out everywhere, the PC removed on the
        // dashboard); a 401 from something in front of the server arrives as 502 (PartyApi.IsServersOwn401).
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            DropApi(); SessionStore.Clear(); link.Forget(); link.Attempt = 0; settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false);
            // Unpaired, the reader that started for the login snapshot stops again.
            readerWanted = false; if (capture is not null) await StopReaderAsync();
            Log.Warn("The server no longer knows this PC's pairing; the saved connection was removed."); Status(Unpaired); RenderHero();
        }
        // Anything else (offline, 403, 5xx, 429, a timeout) keeps the pairing and tries again by itself.
        catch (Exception error)
        {
            DropApi(); settings.RetryButton.Visibility = Visibility.Visible; settings.ServerOptions.IsExpanded = true; ShowConnected(false);
            ScheduleRestore(error);
        }
        // A reader that could not start before (Npcap missing, say) is tried again once the account is connected,
        // or, while the server cannot confirm the pairing, after its first failed try (a reader stopped by hand
        // meanwhile stays stopped).
        if (capture is null && (api is not null || Offline && link.Attempt <= 1)) await StartReaderAsync();
        if (capture is not null) await TakeOverAsync();
        // The character the game named while offline is saved and its reading sent now (not under this lock).
        if (api is not null && pendingIdentity is { } named) { pendingIdentity = null; _ = Dispatcher.BeginInvoke(async () => await Guarded("Saving the character named offline", () => SaveDetectedCharacterAsync(named.Capture, named.Identity))); }
    }
    private void ScheduleRestore(Exception error)
    {
        link.ScheduleRetry(error, DateTimeOffset.UtcNow);
        // The card turns offline at once: the reader may have rendered it while the server was asked, and the tick
        // no longer renders the whole card (#84).
        RenderRetry(); RenderHero();
    }
    // Counts down to the next automatic try in the footer; Reconnect saved account tries at once.
    private void RenderRetry()
    {
        if (link.RetryAt is not { } at || api is not null) return;
        var left = Wait(at - DateTimeOffset.UtcNow);
        StatusLabel.Text = link.RetryProblem == "busy" ? T("Server busy · retrying in {0}.", left) : T("Server unavailable · retrying in {0}. Your pairing is kept.", left);
    }
    // While the server asks the app to wait (429) the footer counts down; requests resume by themselves.
    private void RenderBusy()
    {
        if (api is not { } current) return;
        var left = current.BusyUntil - DateTimeOffset.UtcNow;
        if (left > TimeSpan.Zero) { busyShown = true; StatusLabel.Text = T("Server busy · retrying in {0}.", Wait(left)); }
        else if (busyShown) { busyShown = false; StatusLabel.Text = T("Server busy · retrying now."); }
    }
    // Every request client of this window reports the account names its sign-ins return.
    private PartyApi UseApi(PartyApi next)
    {
        link.Use(next); NewConnection();
        next.SignedIn += auth => Dispatcher.BeginInvoke(() => { if (api == next && auth.Record.Id == accountId) ShowAccount(auth.Record.DisplayName); });
        return next;
    }
    private void DropApi() { partyTimer.Stop(); realtime?.Dispose(); realtime = null; link.Drop(); NewConnection(); }
    // Forgets what the previous server answered and refused; the capture reads PvP only for an account that wants it.
    private void NewConnection() { connection = new(); partyQueue.NewConnection(); if (capture is { } reading) reading.ReadPvp = PvpWanted; RenderServerNotice(); }
    private void ShowAccount(string displayName)
    {
        accountName = displayName;
        string host; try { host = new Uri(ConnectionDetails.NormalizeEndpoint(ServerAddress)).Host; } catch (ArgumentException) { host = T("server"); } catch (UriFormatException) { host = T("server"); }
        settings.AccountDetail.Text = T("Signed in as {0} via Discord · {1}", accountName, host);
        RenderHero();
    }
    // The dashboard version the server names (PartyApi.ServerVersion), noted after the session starts and after each
    // heartbeat: logged once per connection and again when it changes. A server below ServerVersions.Minimum, or one
    // that names none (dashboards before 1.0.0), keeps working; the footer and Settings say it runs an older Odyle.
    private void NoteServerVersion()
    {
        if (api is not { } current) return;
        var version = current.ServerVersion;
        if (connection.ServerVersionNoted && version == connection.ServerVersion) return;
        connection.ServerVersionNoted = true; connection.ServerVersion = version;
        var parsed = ServerVersions.Parse(version);
        Log.Info(version is null ? $"The server names no version: it runs a dashboard older than {ServerVersions.Minimum}, which this app needs; it keeps working."
            : parsed is null ? "The server named a version that cannot be read; it is taken as older than " + ServerVersions.Minimum + "."
            : parsed < ServerVersions.Minimum ? $"The server runs dashboard {parsed}, older than {ServerVersions.Minimum}, which this app needs; it keeps working."
            : $"The server runs dashboard {parsed}.");
        RenderServerNotice();
    }
    private string ServerNotice => api is not null && connection.ServerVersionNoted && ServerVersions.Outdated(connection.ServerVersion) ? T("This server runs an older Odyle; ask its operator to update.") : "";
    private string shownServerNotice = "";
    private void RenderServerNotice()
    {
        var notice = ServerNotice;
        settings.ServerNoticeLabel.Text = notice; settings.ServerNoticeLabel.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The footer shows it while it says nothing else, and drops it once it no longer applies.
        if (StatusLabel.Text.Length == 0 || StatusLabel.Text == shownServerNotice) StatusLabel.Text = notice;
        shownServerNotice = notice;
    }
    private static string Unpaired => T("This PC is no longer paired with your account. Copy a new connection link from the dashboard.");
    // Saves the pairing on this PC: its device key keeps it paired however long the app is closed.
    private void SavePairing(AuthResponse auth)
    {
        if (api is not { } current) return;
        try { SessionStore.Save(new SavedSession(current.Endpoint, auth.Token, current.DeviceKey)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { Log.Error("The connection could not be saved", error); Status(T("Connected, but this PC could not save the connection: {0}", error.Message)); }
    }
    private async Task ConnectAsync(AuthResponse auth)
    {
        if (api is null) return;
        var session = await api.StartSessionAsync();
        // Another server or account than the one the reader saved its character to (pairing again while it reads): the
        // character the game named is saved again there before anything is sent, never as the new account's last one.
        if (accountId.Length > 0 && (auth.Record.Id != accountId || connectedEndpoint != api.Endpoint)) ForgetSavedCharacter();
        connectedEndpoint = api.Endpoint;
        link.Connected(session); activeCharacterId = ""; accountId = auth.Record.Id; ShowAccount(auth.Record.DisplayName);
        // A fresh connection: a backend without the switch never pauses readers.
        NoteReaderPause(session.ReaderPaused ?? false); NoteShareLocation(session.ShareLocation); NotePvpStats(session.PvpStats); NoteShareTeammates(session.ShareTeammates);
        Log.Info($"Connected to {api.Endpoint} as account {accountId} on device \"{PartyApi.DeviceName}\".");
        NoteServerVersion();
        settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(true); UsesPaths();
        var listed = await ReloadAsync();
        Status("");
        link.StartHeartbeat(DateTimeOffset.UtcNow);
        partyTimer.Start(); StartRealtime();
        // The rooms' snapshots and the checklist load beside the reader, not under the operations lock, and the
        // rooms just fetched are not asked for again (#86).
        if (listed is not null) _ = Guarded("Loading the party", () => RefreshPartyAsync(listed));
        _ = Guarded("Loading the checklist", RefreshTasksAsync);
        await FollowReaderPauseAsync();
    }
    // The server the account was last connected on, and the identity the game last named on this capture.
    private string connectedEndpoint = "";
    // The reading's character was saved to another server or account: nothing is sent until the game's character
    // is saved to this one (SaveDetectedCharacterAsync, after the operations lock this connection holds).
    private void ForgetSavedCharacter()
    {
        attribution.DetectedKey = ""; currentCharacterId = ""; activeCharacterId = ""; publishedTotal = -1;
        Log.Info("Connected to another server or account; the character the game named is saved there again.");
        if (attribution.Named is { } named && named.Capture == capture) _ = Dispatcher.BeginInvoke(async () => await Guarded("Saving the named character on the new server", () => SaveDetectedCharacterAsync(named.Capture, named.Identity)));
    }
    // After sleep the realtime connection may be dead without the server having closed it: it connects
    // again at once instead of after the six-minute silence (#82). Called on a system thread.
    private void Resumed(object sender, Microsoft.Win32.PowerModeChangedEventArgs e) { if (e.Mode == Microsoft.Win32.PowerModes.Resume) realtime?.Reconnect("The PC woke from sleep."); }
    // Listens to the account's topic and its rooms'; rooms joined or left later change the
    // topics (RefreshPartyAsync).
    private void StartRealtime()
    {
        realtime?.Dispose(); realtime = null;
        if (api is not { } current || accountId.Length == 0) return;
        var next = new RealtimeClient(current); realtime = next;
        next.Connected += again => Dispatcher.BeginInvoke(async () => await Guarded("Realtime connected", async () =>
        {
            if (realtime != next) return;
            RenderParty();
            if (!again) Log.Info("Realtime connected; the party follows changes as they happen.");
            else await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync());
        }));
        next.Message += (topic, data) => Dispatcher.BeginInvoke(async () => await Guarded("Realtime news", async () => { if (realtime == next) await OnNewsAsync(topic, RealtimeNews.Parse(data)); }));
        next.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            if (realtime != next) return;
            Log.Warn($"Realtime dropped ({next.LastProblem ?? "no reason given"}); the party is fetched every 15 seconds until it is back.");
            RenderParty();
        });
        next.Start(RealtimeTopics.For(accountId, rooms));
    }
    // A room's changed characters and heartbeats are applied to the party in place, anything
    // else fetches that room's snapshot again, and leaving the room the whole party; the
    // account's own news fetches its characters, rooms and checklist again, or applies its
    // changed characters.
    private async Task OnNewsAsync(string topic, RealtimeNews news)
    {
        if (topic.StartsWith(RealtimeTopics.Presence, StringComparison.Ordinal))
        {
            snapshots = PartyNews.WithSeen(snapshots, topic[RealtimeTopics.Presence.Length..], news.Seen); RenderParty();
            return;
        }
        if (topic.StartsWith(RealtimeTopics.Room, StringComparison.Ordinal))
        {
            var roomId = topic[RealtimeTopics.Room.Length..];
            if (!news.Removed && news.Characters.Length > 0)
            {
                var applied = PartyNews.WithCharacters(snapshots, roomId, news.Characters, out var complete);
                if (complete) { snapshots = applied; RenderParty(); return; }
            }
            // Changes to the account's rooms themselves (joining, sharing, renaming) also come as
            // news on its own topic, which fetches everything (ReloadOwnAsync).
            await (news.Removed ? RefreshPartyAsync() : partyQueue.RoomAsync(roomId));
            return;
        }
        // News for everyone: a changed announcement shows at once (a new warning also as a
        // notification), and a newer release is looked for at once, verified like any update.
        if (news.AnnouncementChanged)
        {
            if (news.Announcement is { IsWarning: true } warning && warning.Text != announcement?.Text) Notify(T("Message from the server's operators"), warning.Text);
            ShowAnnouncement(news.Announcement); RenderHero();
        }
        // Notification settings saved on the dashboard apply at once.
        if (news.Notifications is { } saved) { reminders.Settings = saved; CheckReminders(); }
        // Cap alerts of the account's characters (the server checked the quiet hours).
        if (reminders.Settings.RunsInTray) foreach (var invite in news.Invites) { var (title, text) = invite.Notice(); Notify(title, text); Log.Info(invite.Now ? $"Called to run {invite.RunId}." : $"Invited to run {invite.RunId}."); }
        if (reminders.Settings.ReadyInTray) foreach (var party in news.PartyReady) { var (title, text) = party.Notice(); Notify(title, text); Log.Info($"Told that the party of run {party.RunId} is ready."); }
        if (reminders.Settings.CapInTray) foreach (var alert in news.CapAlerts) { var (title, text) = alert.Notice(); Notify(title, text); Log.Info($"Told of the natural pool of {alert.CharacterId}: {alert.State}."); }
        if (news.Release is { } release && release > Updater.Current) { Log.Info($"The server says Odyle {release} is out; looking for it now."); await updates.CheckAsync(manual: false); }
        // Versions the operators hold back (#19): a downloaded one is dropped, and this build moves off one it runs.
        if (news.HeldBack is { } held) await updates.HeldBackAsync(held);
        if (news.UpdateNow is { } asked) await updates.UpdateNowAsync(asked);
        if (!news.Refresh && news.Characters.Length > 0)
        {
            var mine = PartyNews.WithCharacters(characters, news.Characters, out var complete);
            if (complete) { characters = mine; RenderRooms(); RenderHero(); return; }
        }
        if (news.Refresh || news.Characters.Length > 0) await ReloadOwnAsync();
    }
    private async Task ReloadOwnAsync()
    {
        if (api is not { } current) return;
        try { var mine = await current.GetCharactersAsync(); if (api != current) return; characters = mine; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { return; }
        await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync()); RenderHero();
    }
    // The account's characters and rooms; answers the rooms, which the party's next refresh then uses.
    private async Task<RoomsResponse?> ReloadAsync()
    {
        if (api is null) return null;
        characters = await api.GetCharactersAsync(); var listed = await api.GetRoomsAsync(); rooms = listed.Rooms; odylePerCube = listed.OdylePerCube; clockRules = listed.Clock; maps = listed.Maps ?? []; await FollowNotificationsAsync(listed.Notifications); NoteReaderPause(listed.ReaderPaused); ShowAnnouncement(listed.Announcement);
        // Until the game names a character, show the one read most recently.
        if (CurrentCharacter is null) currentCharacterId = characters.OrderByDescending(c => c.LastReadingAt, StringComparer.Ordinal).FirstOrDefault()?.Id ?? "";
        RenderRooms(); RenderHero();
        return listed;
    }
    // The server of the pairing (also while it is tried again), else what the settings' address box says before
    // any pairing: the box is editable while a saved pairing is retried, so what is typed there is never used.
    private string ServerAddress => link.Endpoint.Length > 0 ? link.Endpoint : settings.EndpointInput.Text;
    // The backend also serves the dashboard; "/" opens My rooms & characters. A page opens at its own
    // path once the server said it serves them (DashboardPages), else in the #/ form every server opens.
    private void OpenDashboard(string page)
    {
        try { Process.Start(new ProcessStartInfo(DashboardPages.Url(ConnectionDetails.NormalizeEndpoint(ServerAddress), page, UsesPaths())) { UseShellExecute = true }); }
        catch (Exception error) { Status(T(error.Message)); }
    }
    // Whether the server serves the dashboard's pages at their own paths: asked once per connection
    // (again after ten minutes when that failed); until it answers, links keep the #/ form.
    private bool UsesPaths()
    {
        if (connection.DashboardPaths is null && api is { } current && DateTimeOffset.UtcNow - connection.PathsAskedAt > TimeSpan.FromMinutes(10)) { connection.PathsAskedAt = DateTimeOffset.UtcNow; _ = AskPathsAsync(current); }
        return connection.DashboardPaths == true;
    }
    private async Task AskPathsAsync(PartyApi current)
    {
        try
        {
            var paths = await current.ServesPathsAsync();
            if (api != current) return;
            connection.DashboardPaths = paths; Log.Info(paths ? "The server serves the dashboard's pages at their own paths." : "The server's dashboard takes #/ links only.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { Log.Warn("Whether the server serves the dashboard's paths could not be read: " + error.Message); }
    }
    private async void Pair_Click(object sender, RoutedEventArgs e) => await Guarded("Pairing", () => ExecuteAsync(PairAsync));
    private async void PasteConnect_Click(object sender, RoutedEventArgs e) => await Guarded("Pairing from the clipboard", () => ExecuteAsync(async () =>
    {
        if (!System.Windows.Clipboard.ContainsText()) throw new InvalidOperationException(T("Copy the connection link from the dashboard first."));
        settings.CodeInput.Password = System.Windows.Clipboard.GetText().Trim();
        await PairAsync();
    }));
    private async void Settings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && settings.JoinPanel.Visibility == Visibility.Visible && settings.CodeInput.IsKeyboardFocusWithin) { e.Handled = true; await Guarded("Pairing", () => ExecuteAsync(PairAsync)); }
    }
    private async Task PairAsync()
    {
        var details = ConnectionDetails.Parse(settings.CodeInput.Password, settings.EndpointInput.Text);
        var next = newApi(details.Endpoint, null, null); Status(T("Pairing with your account…"));
        try
        {
            var auth = await next.PairAsync(details.Code); var previous = api; UseApi(next);
            // Pairing again replaces this PC's previous pairing.
            if (previous is not null) { await UnpairQuietlyAsync(previous); previous.Dispose(); }
            settings.EndpointInput.Text = details.Endpoint; SavePairing(auth); settings.CodeInput.Clear(); await ConnectAsync(auth);
        }
        // Paired and saved, but the session or the first reload failed (#79): the code is used up, so the
        // saved pairing is tried again by itself, like one the server could not confirm at start.
        catch (Exception error) when (api == next && error is not HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
        {
            DropApi(); settings.RetryButton.Visibility = Visibility.Visible; settings.ServerOptions.IsExpanded = true; ShowConnected(false);
            Log.Warn($"Paired, but connecting failed: {error.Message}");
            link.Attempt = 0; ScheduleRestore(error);
        }
        // The server's own 401 right after pairing: the new pairing ended at once (removed on the dashboard).
        catch (HttpRequestException) when (api == next)
        {
            DropApi(); SessionStore.Clear(); link.Forget(); link.Attempt = 0; settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false); RenderHero();
            throw;
        }
        catch { if (api != next) next.Dispose(); throw; }
        if (capture is null) await StartReaderAsync();
    }
    // How the window makes its request clients; the checks answer like the server.
    private Func<string, string?, string?, PartyApi> newApi = (endpoint, token, deviceKey) => new PartyApi(endpoint, token, deviceKey);
    private void UpdateCharacterInList(Character saved)
    {
        var index = Array.FindIndex(characters, x => x.Id == saved.Id);
        if (index >= 0) characters[index] = saved; else characters = [.. characters, saved];
    }

    // ----- Rooms -----
    // The party is fetched beside reader operations (PartyRefreshQueue); a caller waits until it shows the party as
    // the server has it now. known: the rooms answer the caller fetched a moment ago (ConnectAsync).
    private Task RefreshPartyAsync(RoomsResponse? known = null) => partyQueue.WholeAsync(known);
    // The account's rooms and their snapshots, fetched for the connection that is still the window's.
    private async Task ApplyRoomsAsync(RoomsResponse listed, FetchedSnapshot[] fetched)
    {
        rooms = listed.Rooms; odylePerCube = listed.OdylePerCube; clockRules = listed.Clock; maps = listed.Maps ?? [];
        snapshots = PartySnapshots.Merge(rooms, snapshots, fetched); if (PartySnapshots.ClockOffset(fetched) is { } offset) serverOffset = offset;
        await FollowNotificationsAsync(listed.Notifications); NoteReaderPause(listed.ReaderPaused); ShowAnnouncement(listed.Announcement);
        RenderRooms(); RenderParty(); RenderHero(); CheckReminders();
        if (realtime is { } listening) _ = listening.ListenAsync(RealtimeTopics.For(accountId, rooms));
    }
    // Some rooms' snapshots, fetched after realtime news of them.
    private void ApplySnapshots(FetchedSnapshot[] fetched)
    {
        snapshots = PartySnapshots.Merge(rooms, snapshots, fetched); if (PartySnapshots.ClockOffset(fetched) is { } offset) serverOffset = offset;
        RenderParty(); RenderHero(); CheckReminders();
    }
    // The server's announcement above the reading card, as plain text. Hiding it lasts until the
    // text changes, across restarts too (preferences.json, odyle-dashboard#47); a new message also
    // shows up when the window is hidden.
    private Announcement? announcement;
    private string hiddenAnnouncement = "";
    private void ShowAnnouncement(Announcement? next)
    {
        if (next is { } shown && shown.Text != announcement?.Text) Log.Info("Announcement from the server: " + shown.Text);
        announcement = next is { Text.Length: > 0 } ? next : null;
        // While the server pauses readers the card itself shows the announcement (RenderHero).
        if (announcement is not { } current || current.Text == hiddenAnnouncement || readerPaused && api is not null) { AnnouncementBar.Visibility = Visibility.Collapsed; return; }
        AnnouncementText.Text = current.Text;
        AnnouncementBar.Background = Brush(current.IsWarning ? "PillAmber" : "PillLive");
        AnnouncementBar.BorderBrush = Brush(current.IsWarning ? "Amber" : "LineStrong");
        AnnouncementText.Foreground = Brush(current.IsWarning ? "Amber" : "Text");
        AnnouncementBar.Visibility = Visibility.Visible;
    }
    private void HideAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        hiddenAnnouncement = announcement?.Text ?? ""; ShowAnnouncement(announcement);
        PreferencesStore.Save(preferences = preferences with { HiddenAnnouncement = hiddenAnnouncement });
    }
    // Tray notifications (toasts on Windows 10 and 11); clicking one opens the window.
    private void Notify(string title, string text) => tray.Notify(title, text);
    // The account's notification settings from the rooms answer (TrayReminders.FollowAsync).
    private Task FollowNotificationsAsync(NotificationSettings? listed) => reminders.FollowAsync(listed, api);
    // Planned runs the account joined on the dashboard, reminded in the tray (TrayReminders.Runs).
    private void CheckReminders() => reminders.Runs(snapshots, accountId, DateTimeOffset.UtcNow + serverOffset);
    // Two hours before each daily and weekly reset, the tasks of that cadence still open, in the
    // tray (Account & data → Notifications on the dashboard), unless it is quiet hours. Near a
    // reset the checklist is fetched every 10 minutes, also while its panel is closed; reminded
    // resets are kept in preferences.json, so a restart does not repeat them.
    private async Task TaskRemindersAsync()
    {
        if (!reminders.Settings.TasksInTray || api is not { } current || accountId.Length == 0) return;
        var now = DateTimeOffset.UtcNow + serverOffset;
        if (!ResetReminders.Near(now, resets)) return;
        if (!tasksLoaded || DateTimeOffset.UtcNow - tasksFetchedAt > TimeSpan.FromMinutes(10))
        {
            if (refreshingTasks) return;
            refreshingTasks = true;
            try { var listed = await current.GetTasksAsync(); if (api != current) return; ApplyTasks(listed); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { tasksFetchedAt = DateTimeOffset.UtcNow; return; }
            finally { refreshingTasks = false; }
            now = DateTimeOffset.UtcNow + serverOffset;
        }
        reminders.Resets(tasks, resets, now);
    }
    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { Status(T(error.Message)); }
    }

    // ----- Discord status -----
    // Off by default (Settings → Window): while the reader has the game, Discord shows the card's
    // character with its Odyle, then the run you go to within the hour, or the cubes and when the
    // natural pool is full. It ends when the game closes, the reader stops or the setting is off.
    // The Discord application is the one the server signs in with (its public auth methods).
    private readonly DiscordStatus discord = new();
    private DateTimeOffset? gameSince;
    private void SetDiscordStatus(bool on, bool persist)
    {
        settings.DiscordToggle.IsChecked = on; preferences = preferences with { DiscordStatus = on };
        if (persist) { PreferencesStore.Save(preferences); Log.Info(on ? "The Discord status is on." : "The Discord status is off."); }
        RenderDiscord();
    }
    private void RenderDiscord()
    {
        if (preferences.DiscordStatus && api is { } current && connection.DiscordApp is null && DateTimeOffset.UtcNow - connection.DiscordAskedAt > TimeSpan.FromMinutes(10))
        {
            connection.DiscordAskedAt = DateTimeOffset.UtcNow; _ = AskDiscordAppAsync(current);
        }
        if (!App.Headless) discord.Show(connection.DiscordApp ?? "", connection.DiscordApp is null ? null : PresenceNow());
        RenderDiscordNote();
    }
    private async Task AskDiscordAppAsync(PartyApi current)
    {
        try
        {
            var found = await current.GetDiscordApplicationAsync();
            if (api != current) return;
            connection.DiscordApp = found; connection.DiscordMissing = found is null;
            if (found is null) Log.Info("The server has no Discord sign-in, so there is no Discord status.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException) { Log.Warn("The server's Discord application could not be read: " + error.Message); }
        RenderDiscordNote();
    }
    // What Discord should show now, or null: only while the reader has the game, for the card's
    // character, never for a reading the game has not named yet.
    private DiscordActivity? PresenceNow()
    {
        if (!preferences.DiscordStatus || api is null || capture is null || !gamePresent || CurrentCharacter is not { } character || heroValue is not { } value) return null;
        var reading = attribution.Latest;
        if (reading is not null && !attribution.OwnerSaved) return null;
        if (reading is null && !character.Confirmed) return null;
        return DiscordPresence.Dress(DiscordPresence.Activity(character.CharacterName, value, heroCost, CardClock(), chipRun, DateTimeOffset.UtcNow + serverOffset, gameSince), api.Endpoint, character, UsesPaths());
    }
    private void RenderDiscordNote()
    {
        var note = !preferences.DiscordStatus ? ""
            : connection.DiscordMissing ? T("This server has no Discord sign-in, so Odyle has no Discord app to show your status with.")
            : discord.Now == DiscordStatus.State.Refused ? T("Discord turned it down: {0}", discord.Problem)
            : discord.Now == DiscordStatus.State.NotRunning ? T("Discord is not running on this PC.")
            : discord.Now == DiscordStatus.State.AsAdministrator ? T("Discord runs as administrator, so Odyle cannot reach it. Turn off “Run this program as an administrator” in Discord.exe's Properties → Compatibility and restart Discord.")
            : discord.Now == DiscordStatus.State.Shown ? T("Shown in your Discord status now.")
            : T("Shown in your Discord status while the reader reads your character.");
        if (settings.DiscordNoteLabel.Text != note) settings.DiscordNoteLabel.Text = note;
        settings.DiscordNoteLabel.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Jump list -----
    // The taskbar button's right-click menu (App sets it up): each task starts Odyle with --do, which
    // the running Odyle gets over the Open in Odyle pipe; a first start does it once it is up.
    public void Do(string action)
    {
        Log.Info($"Jump list: {action}.");
        switch (action)
        {
            case "compact": ToggleCompact(); BringToFront(); break;
            case "tasks": if (compactMode) SetCompact(false, persist: true); SetTasksShown(!TasksShown, persist: true); BringToFront(); break;
            case "reader": Reader_Click(this, new RoutedEventArgs()); break;
            case "dashboard": if (api is null && !Offline) { BringToFront(); OpenSettings(); } else OpenDashboard(""); break;
            case "settings": BringToFront(); OpenSettings(); break;
        }
    }

    // ----- Window -----
    // Pinned above other windows, for the game in windowed or borderless mode. The
    // header pin, the tray menu and settings toggle the same remembered choice.
    private void SetStayOnTop(bool on, bool persist)
    {
        Topmost = on; tray.Pin.Checked = on; settings.StayOnTopToggle.IsChecked = on;
        PinButton.Foreground = Brush(on ? "Mint" : "Muted"); PinButton.Background = on ? Brush("MintFill") : Brushes.Transparent;
        PinButton.ToolTip = on ? T("Pinned above other windows · click to unpin") : T("Stay on top of other windows, for example the game in windowed mode");
        if (persist) PreferencesStore.Save(preferences = preferences with { StayOnTop = on });
    }
    private void Pin_Click(object sender, RoutedEventArgs e) => SetStayOnTop(!Topmost, persist: true);
    // While you play (W#18), on this PC like stay on top. Fading: while the game's window is in
    // front the card is 60 % opaque (a layered window, so the game shows through), and fully
    // opaque while the pointer is on it; the foreground window is looked at twice a second.
    private const byte FadedAlpha = 153;
    private readonly DispatcherTimer gameFocusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool faded; private uint foregroundPid; private bool foregroundIsGame;
    // Checks replace it; the window in front belongs to the game's process (ReaderSetup).
    private Func<bool> gameInFront;
    private bool GameWindowInFront()
    {
        if (!NativeWindows.ForegroundProcess(out var pid)) return false;
        if (pid == foregroundPid) return foregroundIsGame;
        foregroundPid = pid;
        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); foregroundIsGame = GameConnection.IsGameProcess(process.ProcessName); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { foregroundIsGame = false; }
        return foregroundIsGame;
    }
    private void SetFadeInGame(bool on, bool persist)
    {
        settings.FadeInGameToggle.IsChecked = on; preferences = preferences with { FadeInGame = on };
        if (persist) PreferencesStore.Save(preferences);
        if (on) gameFocusTimer.Start(); else gameFocusTimer.Stop();
        RenderFade();
    }
    private void RenderFade()
    {
        var fade = preferences.FadeInGame && !IsMouseOver && gameInFront();
        // WPF takes the layered style off when it changes the window's style itself: it is put back.
        var handle = new WindowInteropHelper(this).Handle;
        var layered = NativeWindows.Layering.IsLayered(handle);
        if (fade == faded && layered == preferences.FadeInGame) return;
        faded = fade; layering.SetAlpha(handle, fade ? FadedAlpha : (byte)255, layered: preferences.FadeInGame);
    }
    // Following the game: it came when the reader finds its connection after waiting for one, and
    // it went when the reader waits for a connection again after it had one.
    private bool gamePresent;
    private static string FollowStep(bool present, ReaderState state) =>
        !present && state is ReaderState.Listening or ReaderState.Midstream or ReaderState.Live ? "show"
        : present && state == ReaderState.Waiting ? "hide" : "";
    private void FollowGame(bool show)
    {
        if (!show) { if (IsVisible) { SavePosition(); Hide(); } return; }
        // Up without taking the game's focus, and back above it when pinned.
        if (!IsVisible || WindowState == WindowState.Minimized) { ShowActivated = false; Show(); WindowState = WindowState.Normal; ShowActivated = true; }
        if (Topmost) { Topmost = false; Topmost = true; }
        _ = RefreshPartyAsync(); _ = RefreshTasksAsync();
    }
    // Compact mode in place of the card, in the same window: it keeps its place, title bar, pin and
    // tray, and only its content shrinks to your Odyle and your party's. In from the header button,
    // the tray or the shortcut; back the same way, with a double-click or with ⤢.
    private void SetCompact(bool on, bool persist)
    {
        tray.Compact.Checked = on;
        if (persist) PreferencesStore.Save(preferences = preferences with { CompactMode = on });
        if (on == compactMode) return;
        compactMode = on;
        Card.Visibility = on ? Visibility.Collapsed : Visibility.Visible; CompactPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CardColumn.Width = new GridLength(on ? CompactStrip.Width : CardWidth);
        TasksPanel.Visibility = !on && preferences.ShowTasks ? Visibility.Visible : Visibility.Collapsed;
        RenderBackdrop(); RenderCompact(); Log.Info(on ? "Switched to compact mode." : "Switched back to the full card.");
    }
    private void Compact_Click(object sender, RoutedEventArgs e) => SetCompact(true, persist: true);
    // What's new: the line on the card after an update, until opened or hidden, and the window.
    private void RenderUpdated()
    {
        var updated = preferences.SeenVersion.Length > 0 && preferences.SeenVersion != VersionText;
        UpdatedText.Text = T("Updated to {0}.", VersionText); UpdatedBar.Visibility = updated ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SeenVersion() { if (preferences.SeenVersion != VersionText) PreferencesStore.Save(preferences = preferences with { SeenVersion = VersionText }); RenderUpdated(); }
    private void HideUpdated_Click(object sender, RoutedEventArgs e) => SeenVersion();
    private void WhatsNew_Click(object sender, RoutedEventArgs e) => OpenWhatsNew();
    private void OpenWhatsNew()
    {
        SeenVersion();
        if (whatsNew is null)
        {
            whatsNew = new WhatsNewWindow(Changelog.Parse(WhatsNewWindow.Embedded()), VersionText);
            var news = whatsNew; news.SourceInitialized += (_, _) => Frame(news, newsBackdrop); news.Closed += (_, _) => whatsNew = null;
            if (IsVisible) whatsNew.Owner = this;
        }
        if (!App.Headless) { whatsNew.Show(); whatsNew.WindowState = WindowState.Normal; whatsNew.Activate(); }
    }
    private void Expand_Click(object sender, RoutedEventArgs e) => SetCompact(false, persist: true);
    // The card's width (variant C; compact mode is CompactStrip.Width, a little narrower).
    internal const double CardWidth = 310;
    private void CompactPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) SetCompact(false, persist: true); else Drag(e); }
    private void ToggleCompact() { SetCompact(!compactMode, persist: true); if (!IsVisible) ShowWindow(); }
    // Your row (the card's character, value, cubes and state dot), then the other members'
    // characters online in your rooms (CompactStrip.Mates), the players of one party or force in
    // the game framed together, then a short note when needed.
    private void RenderCompact()
    {
        if (!compactMode) return;
        var now = DateTimeOffset.UtcNow + serverOffset; var connected = api is not null;
        CompactName.Text = connected && CurrentCharacter is { } character ? character.CharacterName : heroTitle;
        CompactValue.Text = ValueLabel.Text; CompactDot.Fill = StateDot.Fill;
        CompactCubes.Text = connected && heroValue is { } value ? Tn(value / heroCost, "{0} cube", "{0} cubes") : "";
        CompactTrack.Value = EnergyTrack.Value; CompactTrack.Visibility = EnergyTrack.Visibility;
        CompactBonusTrack.Value = BonusTrack.Value; CompactBonusTrack.Visibility = BonusTrack.Visibility;
        var (mates, more, own) = connected ? CompactStrip.Mates(snapshots, accountId, now, odylePerCube) : ([], 0, Band.None);
        CompactPanel.ToolTip = T("{0} · {1} · double-click for the full card", heroTitle, StateText.Text) + BandHint(own);
        CompactMate Row(CompactStrip.Mate m) => new(m.Name, m.Value.ToString("N0", Culture), Tn(m.Cubes, "{0} cube", "{0} cubes"), Brush(m.Live ? "Mint" : "StaleFill"),
            (m.Live ? T("{0} · live from the game", m.Player) : T("{0} · last value; their reader stopped reporting", m.Player)) + (PlaceLine(m.Location) is { Length: > 0 } place ? " · " + place : "") + BandHint(m.Band))
        { Edge = Brush(m.Band.Party.Length > 0 ? "PartyChipEdge" : m.Band.Force.Length > 0 ? "ForceChipEdge" : "ChipEdge") };
        // Variant C: "+15 1:12:04" in your pill, and "RUN 2:10:00" under the chips, ticking with the clock.
        var clock = connected ? CardClock() : null;
        static void Line(TextBlock line, string lead, Brush accent, string rest)
        {
            line.Inlines.Clear();
            if (lead.Length == 0) return;
            line.Inlines.Add(new System.Windows.Documents.Run(lead) { Foreground = accent, FontWeight = FontWeights.Bold }); line.Inlines.Add(new System.Windows.Documents.Run(" " + rest));
        }
        Line(CompactRefill, clock is { Full: false, Next: not null } ? $"+{clock.Tick}" : "", CapBrush(OdyleClock.Level(clock, DateTimeOffset.UtcNow), "Mint"), clock?.Next is { } tick ? Hours(tick - DateTimeOffset.UtcNow) : "");
        Line(CompactRun, chipRun is { } planned && planned.StartsAt > now ? T("RUN") : "", Brush("Amber"), chipRun is { } soon ? Countdown(soon.StartsAt - now) : "");
        CompactRefill.Visibility = CompactRefill.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactClock.Visibility = CompactRun.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Your force and party come first (GameBands.Arrange): your row goes into their frames, which
        // take the divider's place, and the rest follow.
        var rest = GameBands.Nest(mates.Select(m => (m, m.Band)).ToArray());
        var ownForce = rest is [{ Kind: "force", Yours: true } force, ..] ? force : null;
        if (ownForce is not null) rest = rest[1..];
        var inForce = ownForce?.Children ?? [];
        var ownParty = (ownForce is null ? rest : inForce) is [{ Kind: "party", Yours: true } party, ..] ? party : null;
        if (ownParty is not null) { if (ownForce is null) rest = rest[1..]; else inForce = inForce[1..]; }
        void Own(Border box, Nested<CompactStrip.Mate>? band, bool top)
        {
            // Variant C draws no frame around floating chips: the chips of the players the game put with you carry the
            // party's or force's colour on their edge, and the frame's name stays in the tooltip.
            _ = top; box.ToolTip = band is null ? null : BandLabel(band);
        }
        Own(CompactOwnForce, ownForce, top: true); Own(CompactOwnParty, ownParty, top: ownForce is null);
        ShowRows(CompactOwnPartyMates, ownParty is null ? Array.Empty<object>() : CompactEntries(ownParty.Children!, "party", "row", Row));
        ShowRows(CompactOwnForceMates, ownForce is null ? Array.Empty<object>() : CompactEntries(inForce, "force", ownParty is null ? "row" : "frame", Row));
        ShowRows(CompactMates, CompactEntries(rest, "", ownForce is null && ownParty is null ? "" : "frame", Row));
        CompactNote.Text = !connected ? T("Open settings to pair with the dashboard.")
            : readerPaused ? T("Paused by the server")
            : more > 0 ? T("+{0} more online", more)
            // Nobody else online needs no chip: your pill alone says it.
            : mates.Length == 0 && rooms.Length == 0 ? T("No rooms yet · create or join one on the dashboard.") : "";
        CompactNote.Visibility = CompactNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactDivider.Visibility = rest.Length > 0 && ownForce is null && ownParty is null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChooseHotkey(string hotkey)
    {
        if (hotkey == preferences.CompactHotkey) return;
        PreferencesStore.Save(preferences = preferences with { CompactHotkey = hotkey }); UseHotkey(hotkey);
    }
    // The shortcut is registered for the whole session (NativeWindows.Shortcut). One another app holds
    // already says so in Settings, where another can be chosen.
    private void UseHotkey(string hotkey)
    {
        hotkeyProblem = "";
        if (!App.Headless && !(shortcut ??= new(ToggleCompact)).Use(hotkey, out var error))
        {
            hotkeyProblem = T("{0} is taken by another app · choose another shortcut.", hotkey);
            Log.Warn($"The shortcut {hotkey} could not be registered (error {error}).");
        }
        settings.HotkeyProblemLabel.Text = hotkeyProblem; settings.HotkeyProblemLabel.Visibility = hotkeyProblem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactButton.ToolTip = CompactStrip.Parse(hotkey) is null || hotkeyProblem.Length > 0 ? T("Compact mode · your Odyle and your party's, nothing else") : T("Compact mode · your Odyle and your party's, nothing else ({0})", hotkey);
    }
    // The checklist opens beside the card; the window grows to the right. Whether it is shown is the preference
    // (compact mode hides it meanwhile); the panel, the toggles and the tray item follow it.
    private bool TasksShown => preferences.ShowTasks && !compactMode;
    private void SetTasksShown(bool on, bool persist)
    {
        preferences = preferences with { ShowTasks = on };
        TasksPanel.Visibility = on && !compactMode ? Visibility.Visible : Visibility.Collapsed; settings.TasksToggle.IsChecked = on;
        TasksButton.Foreground = Brush(on ? "Mint" : "Muted"); TasksButton.Background = on ? Brush("MintFill") : Brushes.Transparent;
        TasksButton.ToolTip = on ? T("Hide the daily & weekly tasks") : T("Show your daily & weekly tasks beside the card");
        if (persist) PreferencesStore.Save(preferences);
        RenderTasks(); if (on) _ = RefreshTasksAsync();
    }
    private void Tasks_Click(object sender, RoutedEventArgs e) => SetTasksShown(!TasksShown, persist: true);
    private void ManageTasks_Click(object sender, RoutedEventArgs e) => OpenDashboard("/tasks");
    // Growing (the checklist opened, a longer party list) must not push the window off its screen.
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || PresentationSource.FromVisual(this) is not { CompositionTarget: { } target }) return;
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var corner = target.TransformFromDevice.Transform(new Point(area.Left, area.Top)); var far = target.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
        var (left, top) = Placement.Clamp(Left, Top, ActualWidth, ActualHeight, new ScreenArea(corner.X, corner.Y, far.X, far.Y));
        if (left != Left) Left = left;
        if (top != Top) Top = top;
    }
    // The window sizes itself to its content. Now and then it came up with a dark strip on the right
    // and its bottom cut off: a frame drawn before its size settled, or a size that missed a change of
    // the content while it was hidden or minimized. Each time it shows, once drawn, its size is checked
    // against the content (and fitted again when they differ) and the whole window is drawn again.
    protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); FitWindow(); }
    // Hidden, the card ticks no more (Tick): showing it renders it at once, before it is fitted.
    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        GuardedTick("Showing the window", () => { RenderHero(); RenderParty(); TickTasks(); RenderRetry(); RenderBusy(); RenderBackdrop(); });
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, FitWindow);
    }
    private void Window_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Normal) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, FitWindow); }
    private void FitWindow()
    {
        if (!IsVisible || WindowState != WindowState.Normal || Content is not FrameworkElement content || PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } target } source || !NativeWindows.ClientSize(source.Handle, out var width, out var height)) return;
        var shown = target.TransformFromDevice.Transform(new Point(width, height)); var wanted = content.DesiredSize;
        if (Math.Abs(shown.X - wanted.Width) > 2 || Math.Abs(shown.Y - wanted.Height) > 2)
        {
            Log.Warn($"The window showed {shown.X:0}×{shown.Y:0} for content of {wanted.Width:0}×{wanted.Height:0}; fitting it again.");
            SizeToContent = SizeToContent.Manual; SizeToContent = SizeToContent.WidthAndHeight;
        }
        NativeWindows.Repaint(source.Handle);
    }
    // The window opens where it was last, while that is still on a connected screen;
    // otherwise centred as on a first start. Screens are converted with the system DPI,
    // which is what this system-aware app's coordinates use.
    private void RestorePosition()
    {
        if (preferences is not { Left: { } left, Top: { } top } || double.IsNaN(left) || double.IsNaN(top)) return;
        var scale = Forms.Screen.PrimaryScreen is { } primary && SystemParameters.PrimaryScreenWidth > 0 ? primary.Bounds.Width / SystemParameters.PrimaryScreenWidth : 1;
        var areas = Forms.Screen.AllScreens.Select(s => new ScreenArea(s.WorkingArea.Left / scale, s.WorkingArea.Top / scale, s.WorkingArea.Right / scale, s.WorkingArea.Bottom / scale));
        if (!Placement.OnScreen(left, top, areas)) return;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top;
    }
    private void Window_LocationChanged(object? sender, EventArgs e) { if (IsVisible) { positionTimer.Stop(); positionTimer.Start(); } }
    private void SavePosition()
    {
        if (!IsVisible || WindowState != WindowState.Normal || double.IsNaN(Left) || double.IsNaN(Top) || preferences is { Left: var l, Top: var t } && l == Left && t == Top) return;
        PreferencesStore.Save(preferences = preferences with { Left = Left, Top = Top });
    }
    // Settings → Start with Windows writes or removes the Run entry ("<Odyle.exe>" --tray). Either
    // choice also answers the one-time offer below.
    private void SetAutostart(bool on)
    {
        try { Autostart.Set(on); Log.Info(on ? "Start with Windows is on." : "Start with Windows is off."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            Log.Error("Start with Windows could not be changed", error); Status(T("Start with Windows could not be changed: {0}", error.Message));
        }
        settings.AutostartToggle.IsChecked = Autostart.Enabled;
        AutostartOffer.Visibility = Visibility.Collapsed;
        if (!preferences.AutostartOffered) PreferencesStore.Save(preferences = preferences with { AutostartOffered = true });
    }
    // After the first live reading, once per PC: started with Windows, Odyle waits in the tray and
    // reads the login snapshot. Answered (here or in Settings), it is not offered again.
    private void OfferAutostart()
    {
        if (preferences.AutostartOffered || AutostartOffer.Visibility == Visibility.Visible || Autostart.Enabled) return;
        AutostartOffer.Visibility = Visibility.Visible; Log.Info("Offered Start with Windows after the first live reading.");
    }
    private void AcceptAutostart_Click(object sender, RoutedEventArgs e) => SetAutostart(true);
    private void DeclineAutostart_Click(object sender, RoutedEventArgs e)
    {
        AutostartOffer.Visibility = Visibility.Collapsed; PreferencesStore.Save(preferences = preferences with { AutostartOffered = true });
        Log.Info("Start with Windows was declined; it stays in Settings.");
    }
    // Settings → Copy diagnostics: what helps with a reader that does not read, on the clipboard
    // only. Nothing is sent; the person decides where to paste it.
    private void CopyDiagnostics()
    {
        try { System.Windows.Clipboard.SetText(DiagnosticsReport()); }
        catch (ExternalException) { settings.DiagnosticsLabel.Text = T("The clipboard is busy · try again in a moment."); return; }
        settings.DiagnosticsLabel.Text = T("Diagnostics copied · paste them into your bug report or message. Nothing was sent.");
        Log.Info("Diagnostics were copied to the clipboard.");
    }
    private string DiagnosticsReport()
    {
        string adapters;
        try { var found = PacketCapture.Adapters(); adapters = found.Length + (found.Length > 0 ? " · " + string.Join("; ", found) : ""); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { adapters = "unavailable · " + error.Message; }
        var npcap = !Npcap.Probe() ? "not found" : $"installed{(Npcap.Version() is { } version ? " " + version : "")}{(Npcap.Restricted() ? " · administrators only" : "")}";
        string host; try { host = new Uri(ConnectionDetails.NormalizeEndpoint(ServerAddress)).Host; } catch (Exception error) when (error is ArgumentException or UriFormatException) { host = "unknown server"; }
        var account = api is not null ? $"paired · {host} · {(realtime?.IsConnected == true ? "realtime" : "polling")}" : link.RetryAt is not null ? $"paired · {host} · not reached yet" : "not paired";
        var reader = capture is { } running ? $"running · {readerState} · {running.ReceivedPackets:N0} packets · {running.GameBytes / 1024.0:N0} KB from the game" : $"stopped · {readerState}";
        return Diagnostics.Report(DateTimeOffset.Now, [
            ("App", $"Odyle {Updater.Current} · {Updater.Runtime} · {(Updater.SelfContained ? "self-contained" : "small")} package · {(Updater.IsReleaseBuild ? "release" : "development")} build"),
            ("Windows", $"{Environment.OSVersion} · language {Localizer.Language} ({Localizer.Culture.Name})"),
            ("Npcap", npcap), ("Capture interfaces", adapters), ("Account", account), ("Reader", reader),
            ("Adapter", OdyleDecoder.Version + (connection.AdapterOutdated ? " · refused by the server" : "")),
            ("Server", readerPaused ? "packet readers paused by the operators" : "packet readers allowed")], readerHistory, Log.Tail(80));
    }
    private void RenderNpcap()
    {
        var installed = Npcap.Probe(); var usable = installed && !Npcap.Restricted();
        settings.NpcapLabel.Text = usable ? T("Npcap installed") : installed ? T("Npcap installed · only administrators may capture") : T("Npcap not found · install it to read the game");
        settings.NpcapDot.Fill = Brush(usable ? "Mint" : "Amber"); settings.NpcapLabel.Foreground = Brush(usable ? "Text" : "Amber");
    }
    // The reader cannot start without a working Npcap (error null: found missing at start). The reader
    // line says why, with a link to the download page, and Npcap is looked for every 10 seconds.
    private void ShowNpcapProblem(Exception? error)
    {
        var installed = Npcap.Probe();
        var message = !installed ? T("Npcap is not installed · Odyle needs it to read the game.")
            : error is InvalidOperationException && Npcap.Restricted() ? T("Npcap lets only administrators capture · reinstall it without “Restrict Npcap driver's access to Administrators only”.")
            : error is InvalidOperationException ? T("Npcap shows this Windows account no network interface · if it was installed for administrators only, reinstall it without that option.")
            : error is not null ? T(error.Message) : T("Npcap is not installed · Odyle needs it to read the game.");
        if (error is not null && message != lastReaderLog) Log.Warn("The reader could not start: " + error.Message);
        SetReaderState(ReaderState.Problem, message);
        NpcapLink.Content = installed ? T("Npcap setup ↗") : T("Install Npcap ↗"); NpcapLink.Visibility = Visibility.Visible;
        npcapTimer.Stop(); npcapTimer.Start();
        if (settings.IsVisible) RenderNpcap();
    }
    private void NpcapLink_Click(object sender, RoutedEventArgs e) => OpenLink(Npcap.DownloadPage);
    // Every 10 seconds while Npcap is missing or unusable: once it is installed, the reader starts by
    // itself (an Npcap still restricted fails again and keeps the link).
    private async Task NpcapTickAsync()
    {
        if (busy || !Npcap.Probe()) return;
        npcapTimer.Stop(); if (settings.IsVisible) RenderNpcap();
        // A saved pairing still being retried reads too, and sends what it reads once the server answers.
        if (readerWanted && (api is not null || Offline) && capture is null && !readerPaused) { await ExecuteAsync(StartReaderAsync); return; }
        if (readerState == ReaderState.Problem && capture is null)
            SetReaderState(ReaderState.Stopped, api is not null || Offline ? T("Stopped · start the reader before logging in.") : T("Npcap is installed · pair Odyle in settings to read the game."));
    }

    // ----- Daily & weekly tasks -----
    // The account's private checklist from the dashboard, read while the panel is open
    // (every 15 seconds with the party). Tasks are added and removed on the dashboard;
    // ticking them off happens here. Read-only against the reader's lock, like the party.
    private async Task RefreshTasksAsync()
    {
        if (api is not { } current || !TasksShown || refreshingTasks) return;
        refreshingTasks = true;
        try { var listed = await current.GetTasksAsync(); if (api == current) ApplyTasks(listed); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { if (api == current) { tasksProblem = T(error.Message); RenderTasks(); } }
        finally { refreshingTasks = false; }
    }
    private void ApplyTasks(TasksResponse listed)
    {
        tasks = listed.Tasks; resets = listed.Resets ?? new(); tasksLoaded = true; tasksProblem = null; tasksFetchedAt = DateTimeOffset.UtcNow;
        if (Readiness.Parse(listed.ServerNow) is { } server) serverOffset = server - DateTimeOffset.UtcNow;
        RenderTasks();
    }
    // The server stores the time a task was ticked and answers with the whole list.
    private async Task ToggleTaskAsync(TaskItem item, bool done)
    {
        if (api is not { } current) return;
        try { var listed = await current.SetTaskDoneAsync(item.Id, done); if (api == current) ApplyTasks(listed); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { Status(T("Could not update the task: {0}", T(error.Message))); RenderTasks(); }
    }
    private async void Task_Click(object sender, RoutedEventArgs e) => await Guarded("Ticking a task", async () =>
    {
        if (sender is not CheckBox { Tag: TaskItem item } box) return;
        box.IsEnabled = false;
        try { await ToggleTaskAsync(item, box.IsChecked == true); } finally { box.IsEnabled = true; }
    });
    private static readonly string[] Cadences = ["daily", "weekly"];
    private void RenderTasks()
    {
        var now = DateTimeOffset.UtcNow + serverOffset; var connected = api is not null;
        renderedDaily = Checklist.PeriodStart("daily", now, resets); renderedWeekly = Checklist.PeriodStart("weekly", now, resets);
        renderedTasksCharacter = CurrentCharacter?.Id; var shown = Checklist.Shown(tasks, renderedTasksCharacter);
        TaskGroups.ItemsSource = !connected ? Array.Empty<TaskGroup>() : Cadences.Select(cadence =>
        {
            var items = shown.Where(t => t.Cadence == cadence).Select(t =>
            {
                var done = Checklist.IsDone(t, now, resets);
                var age = Age(Readiness.Parse(t.DoneAt) ?? now);
                var hint = done ? (cadence == "daily" ? T("Done {0} · unchecks itself at the daily reset", age) : T("Done {0} · unchecks itself at the weekly reset", age)) : cadence == "daily" ? T("Still to do today") : T("Still to do this week");
                // A task of the character's own list says whose it is.
                if (!string.IsNullOrEmpty(t.CharacterName)) hint = t.CharacterName + " · " + hint;
                return new TaskItem(t, done, hint);
            }).ToArray();
            return new TaskGroup(cadence, items) { Reset = ResetIn(cadence, now), ResetAt = ResetAt(cadence, now) };
        }).ToArray();
        TasksHint.Text = !connected ? T("Pair the app in settings to see your daily and weekly tasks.")
            : tasksProblem is { } problem ? T("Could not load the tasks: {0}", problem)
            : !tasksLoaded ? T("Loading your checklist…")
            : shown.Length == 0 ? T("Add tasks on the dashboard under Daily & weekly; they show up here.")
            : T("Tick a task when it is done · it unchecks itself at the game's reset.");
    }
    private string ResetIn(string cadence, DateTimeOffset now) => T("resets in {0}", Checklist.Remaining(Checklist.NextReset(cadence, now, resets) - now));
    private string ResetAt(string cadence, DateTimeOffset now)
    {
        var at = Checklist.NextReset(cadence, now, resets).ToLocalTime().ToString("ddd d MMM HH:mm", Culture);
        return cadence == "daily" ? T("Daily reset {0} local time", at) : T("Weekly reset {0} local time", at);
    }
    // Every second while the panel is open: the countdowns tick and a reset clears the ticks.
    private void TickTasks()
    {
        if (!TasksShown || api is null || TaskGroups.ItemsSource is not TaskGroup[] groups) return;
        var now = DateTimeOffset.UtcNow + serverOffset;
        if (Checklist.PeriodStart("daily", now, resets) != renderedDaily || Checklist.PeriodStart("weekly", now, resets) != renderedWeekly) { RenderTasks(); return; }
        foreach (var group in groups) group.Reset = ResetIn(group.Cadence, now);
    }

    // ----- Packet reader -----
    private async Task InvalidateActiveAsync()
    {
        if (api is null || activeCharacterId.Length == 0) return;
        var id = activeCharacterId; activeCharacterId = ""; publishedTotal = -1;
        try { await api.InvalidateAsync(id, link.SessionId); } catch { }
    }
    private Task StopReaderAsync() => StopCaptureAsync(invalidate: true);
    // invalidate is false when a restart hands the live character over (QuitAsync).
    private async Task StopCaptureAsync(bool invalidate)
    {
        var old = capture; capture = null; attribution.Reset(); publishDelay.Stop();
        if (old is not null) await old.DisposeAsync();
        if (invalidate) await InvalidateActiveAsync();
        attribution.Latest = null; publishFailure = null;
        RenderReaderButton(); ToolTipService.SetIsEnabled(ReaderRow, false);
        SetReaderState(ReaderState.Stopped, old is null ? T("Stopped · start the reader before logging in.") : T("Stopped · the last value stays saved on your account."));
        RenderHero();
    }
    // The card's button is disabled while an operation runs; the tray and the jump list wait for it (#79).
    private async void Reader_Click(object sender, RoutedEventArgs e) => await Guarded("Start or stop the reader", async () =>
    {
        if (api is null && capture is null && !Offline) { OpenSettings(); return; }
        await ExecuteAsync(async () =>
        {
            if (capture is not null) { readerWanted = false; await StopReaderAsync(); }
            else if (api is not null || Offline) await StartReaderAsync();
        }, queue: true);
    });
    // Notes what an answer of the server says about the reader pause (null: the backend does not
    // know the switch). A change is followed once the operations lock is free; callers that hold
    // it follow at once.
    private void NoteReaderPause(bool? paused)
    {
        if (paused is not { } now || now == readerPaused) return;
        readerPaused = now;
        if (now) Log.Warn("The server's operators paused packet readers; the capture stops until they resume them.");
        else Log.Info("The server's operators resumed packet readers.");
        ShowAnnouncement(announcement); RenderReaderButton(); RenderHero();
        Dispatcher.BeginInvoke(async () => await FollowReaderPauseLaterAsync());
    }
    private async Task FollowReaderPauseLaterAsync()
    {
        await operations.WaitAsync();
        try { await FollowReaderPauseAsync(); }
        catch (Exception error) { Log.Error("The reader could not follow the server's pause", error); }
        finally { operations.Release(); }
    }
    // Caller holds the operations lock. While the server pauses readers the capture stops, which
    // closes Npcap, and the reader line says so; once the pause is over the reader starts again by
    // itself, unless it was stopped by hand.
    private async Task FollowReaderPauseAsync()
    {
        if (quitting || api is null) return;
        if (readerPaused)
        {
            if (capture is not null) await StopCaptureAsync(invalidate: true);
            if (readerState != ReaderState.Paused) ShowPaused();
            return;
        }
        if (readerState != ReaderState.Paused) return;
        if (readerWanted && capture is null) await StartReaderAsync();
        else SetReaderState(ReaderState.Stopped, T("Stopped · start the reader before logging in."));
    }
    private void ShowPaused()
    {
        SetReaderState(ReaderState.Paused, T("Paused by the server's operators · the capture is off until they resume it."));
        RenderReaderButton(); RenderHero();
    }
    // How the reader starts a capture (Npcap); the checks start none.
    private Action<PacketCapture> startCapture = capture => capture.Start();
    private async Task StartReaderAsync()
    {
        readerWanted = true;
        // Paused by the server: the reader starts by itself once the pause is over (FollowReaderPauseAsync).
        if (readerPaused) { ShowPaused(); return; }
        if (!Npcap.Probe()) { ShowNpcapProblem(null); RenderHero(); return; }
        var next = new PacketCapture();
        next.Status += (kind, message) => Dispatcher.BeginInvoke(() =>
        {
            if (capture != next) return;
            if (kind == CaptureStatus.TwoClients) OnTwoClients();
            if (kind is CaptureStatus.Listening or CaptureStatus.Waiting or CaptureStatus.TwoClients) attribution.ConnectionRead = false;
            // One of two game clients closed: start over like a new connection, the next identity names the character.
            else if (attribution.OneClientLeft()) { Log.Info("One game client is left; reading again."); RenderHero(); }
            // A reading the game never attributed belongs to its connection; a new connection starts over.
            if (kind == CaptureStatus.Listening && attribution.DropUnowned()) RenderHero();
            if (kind == CaptureStatus.Resyncing) attribution.Resync();
            if (kind == CaptureStatus.Listening) _ = NoteGameVersionAsync();
            // A new game connection while a reading is live keeps the live line, and so do joining
            // one midstream while the character is known (taken over after a restart) and a resync.
            if (kind is CaptureStatus.Listening or CaptureStatus.Midstream or CaptureStatus.Resyncing or CaptureStatus.Resumed && readerState == ReaderState.Live) { Log.Info("Reader stays live: " + message); return; }
            SetReaderState(kind switch { CaptureStatus.Listening or CaptureStatus.Resumed => ReaderState.Listening, CaptureStatus.Midstream or CaptureStatus.Resyncing => ReaderState.Midstream, CaptureStatus.Problem or CaptureStatus.TwoClients => ReaderState.Problem, _ => ReaderState.Waiting }, message);
        });
        next.Candidate += observation => Dispatcher.BeginInvoke(() => OnCandidate(next, observation));
        next.SnapshotWithoutOdyle += () => Dispatcher.BeginInvoke(() => OnSnapshotWithoutOdyle(next));
        next.CharacterChanged += character => Dispatcher.BeginInvoke(async () => await Guarded("A character switch", () => CharacterChangedAsync(next, character)));
        next.IdentityObserved += identity => Dispatcher.BeginInvoke(async () => await Guarded("Saving the named character", () => SaveDetectedCharacterAsync(next, identity)));
        next.LocationObserved += location => Dispatcher.BeginInvoke(() => OnLocation(next, location));
        next.GroupObserved += change => Dispatcher.BeginInvoke(() => OnGroup(next, change));
        next.TeammatesObserved += names => Dispatcher.BeginInvoke(() => OnTeammates(next, names));
        next.ReadPvp = PvpWanted; next.PvpObserved += e => Dispatcher.BeginInvoke(() => OnPvp(next, e));
        // Decoder diagnostics for a value that is wrong or missing: every Odyle update, stat
        // snapshot and identity, and once per run each distinct stat frame layout and each other
        // stat resource that sends updates (not every frame). The patch-day trace counts them all.
        var layouts = new HashSet<OdyleFrameLayout>(); var resources = new HashSet<uint>(); var limit = trace is null ? 64 : int.MaxValue;
        if (trace is { } counting)
        {
            next.FrameObserved += counting.Frame; next.SnapshotObserved += counting.Snapshot; next.UpdateObserved += counting.Update;
            next.LayoutObserved += counting.Layout; next.IdentityObserved += counting.Identity; next.LocationObserved += counting.Location; next.PvpFrameObserved += counting.Pvp;
        }
        // The map of every world load, for naming maps on the dashboard; positions are never logged.
        next.LocationObserved += l => { if (l.Map is { } map) Log.Info($"Decoder: world load on map {map}."); };
        next.UpdateObserved += u =>
        {
            var odyle = u.Resource == OdyleDecoder.OdyleResource;
            if (!odyle) lock (resources) if (resources.Count >= limit || !resources.Add(u.Resource)) return;
            Log.Info($"Decoder: {(odyle ? "Odyle update" : "stat update, not Odyle (once per run)")}, resource {u.Resource}, tag {u.Tag:X2}, direction {u.Direction}, values {u.Base} + {u.Bonus}, {u.TrailerBytes} trailer bytes.");
        };
        next.SnapshotObserved += s => Log.Info($"Decoder: stat snapshot, {s.PayloadBytes} bytes, {(s.Records is { } count ? $"{count} records" : "layout not walked")}, {(s.Fields.Length == 0 ? "no Odyle record" : string.Join("; ", s.Fields.Select(f => $"Odyle {f.Base} + {f.Bonus} (tag {f.Tag:X2} at {f.Offset})")))}, {(s.Read ? "read" : "not read")}.");
        next.LayoutObserved += l => { lock (layouts) if (layouts.Count >= limit || !layouts.Add(l)) return; Log.Info($"Decoder: stat frame {l.Opcode:X4} {l.Header0:X2} {l.Header1:X2} {l.Header2:X2}, {l.PayloadBytes} bytes."); };
        next.IdentityObserved += identity => Log.Info($"Decoder: own character {identity.CharacterName}, server {identity.ServerId} ({identity.Server ?? "unmapped"}, {identity.Region ?? "unmapped region"}), class {identity.Class}.");
        try { startCapture(next); }
        // Npcap missing, not loading or showing no interface: the reader row says why and links to
        // its download page, and the reader starts by itself once Npcap works (NpcapTickAsync).
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { await next.DisposeAsync(); ShowNpcapProblem(error); RenderHero(); return; }
        catch { await next.DisposeAsync(); throw; }
        npcapTimer.Stop();
        capture = next; lastGameBytes = 0; attribution.Latest = null; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue; attribution.Reset();
        RenderReaderButton(); CaptureStatsLabel.Text = T("Waiting for packets…"); ToolTipService.SetIsEnabled(ReaderRow, true);
        SetReaderState(ReaderState.Waiting, T("Waiting for the AION2 game connection…"));
        RenderHero();
    }
    private void OnCandidate(PacketCapture source, PacketCandidate observation)
    {
        if (capture != source || attribution.TwoClients || !attribution.Candidate(observation, DateTimeOffset.UtcNow)) return;
        if (readerState is ReaderState.Waiting or ReaderState.Listening or ReaderState.Midstream) SetReaderState(ReaderState.Listening, T("Saving…"));
        RenderHero();
        if (attribution.OwnerKey.Length > 0) _ = PublishLatestAsync();
    }
    // A complete snapshot without an Odyle record: the character the game loads has no Odyle yet (ReaderAttribution).
    private void OnSnapshotWithoutOdyle(PacketCapture source)
    {
        if (capture != source || attribution.TwoClients) return;
        if (attribution.SnapshotWithoutOdyle(DateTimeOffset.UtcNow))
        {
            SetReaderState(ReaderState.Listening, T("Listening to AION2"));
            _ = MarkNoOdyleAsync(); RenderHero(); return;
        }
        RenderHero();
    }
    // What the heartbeat tells the backend's reader health about this PC's reader.
    private string ReaderStatus() =>
        readerPaused ? "paused" : attribution.TwoClients ? "two-clients" : capture is null ? (npcapTimer.IsEnabled ? "npcap" : "stopped")
        : readerState switch { ReaderState.Waiting => "waiting", ReaderState.Midstream => "midstream", ReaderState.Problem => "problem", ReaderState.Live => "reading", _ => attribution.ConnectionRead ? "reading" : "listening" };
    // Two game clients run on this PC: nothing is read or published until one closes, and the live reading turns
    // stale meanwhile.
    private void OnTwoClients()
    {
        if (!attribution.TwoClientsStarted()) return;
        _ = InvalidateActiveAsync(); RenderHero();
    }
    // Runs when a reading has had no owner for the publish delay.
    private void AttributePending() { if (capture is not null) attribution.AttributePending(DateTimeOffset.UtcNow); }
    private async Task PublishLatestAsync()
    {
        if (busy || !await operations.WaitAsync(0)) { publishDelay.Stop(); publishDelay.Start(); return; }
        try { await PublishCoreAsync(); } finally { operations.Release(); }
    }
    // Caller holds the operations lock. Odyle is sent the first time a character's
    // reading is known and afterwards only when it changes; the 20-second heartbeat
    // keeps an unchanged value live.
    private async Task PublishCoreAsync()
    {
        if (api is null || capture is null || attribution.Latest is not { } reading) return;
        if (attribution.OwnerKey.Length == 0)
        {
            // Without any identity this run (a start after login) the game names the character when it
            // repeats its identity every few minutes, or at once at a relog or channel change; after a
            // snapshot it names it by itself once the loading screen ends. The card's caption says which.
            SetReaderState(ReaderState.Listening, T("Listening to AION2")); RenderHero(); return;
        }
        if (attribution.OwnerKey != attribution.DetectedKey || CurrentCharacter is not Character character)
        {
            SetReaderState(ReaderState.Listening, T("This character's server is not supported, so it is not saved.")); RenderHero(); return;
        }
        if (DateTimeOffset.UtcNow < publishBlockedUntil) return;
        // A fresh snapshot of the value already saved (reconnect, channel change) is live again.
        if (publishedTotal == reading.Total && activeCharacterId == character.Id)
        {
            if (readerState == ReaderState.Listening) SetReaderState(ReaderState.Live, T("Live"));
            return;
        }
        try
        {
            // What changed since the reading saved before it on this live streak (none after a snapshot).
            var streak = publishedTotal >= 0 && activeCharacterId == character.Id;
            var (kind, delta) = OdyleChange.Of(reading.Source, reading.Total, reading.Base, streak ? publishedTotal : null, streak ? publishedBase : null);
            var saved = await api.ReportAsync(new PacketReading(link.SessionId, ++link.Sequence, character.CharacterName, character.Server, character.Region, reading.Total, reading.ParserVersion, character.Id, Base: reading.Base, Bonus: reading.Bonus, Kind: kind, Delta: delta));
            publishedBase = reading.Base;
            if (delta is { } change && kind is OdyleChange.Spend or OdyleChange.Regen or OdyleChange.Item) lastChange = (change, kind, DateTimeOffset.Now, saved.Id);
            if (activeCharacterId.Length > 0 && activeCharacterId != saved.Id) { var previous = activeCharacterId; try { await api.InvalidateAsync(previous, link.SessionId); } catch { } }
            activeCharacterId = saved.Id; publishedTotal = reading.Total; link.LastTick = DateTimeOffset.UtcNow; publishFailure = null; connection.AdapterOutdated = false;
            UpdateCharacterInList(saved);
            SetReaderState(ReaderState.Live, T("Live"));
            OfferAutostart();
        }
        // The server's operators paused packet readers: the capture stops below until they resume them.
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable && error.Message == PartyApi.ReadersPaused)
        {
            publishFailure = null; NoteReaderPause(true);
        }
        // The server asked to wait: the reading is sent again once it may.
        catch (ServerBusyException error)
        {
            publishFailure = T("Server busy"); publishBlockedUntil = error.Until;
            SetReaderState(ReaderState.Problem, T("Server busy · the reading is sent again in {0}.", Wait(error.Until - DateTimeOffset.UtcNow)));
        }
        // The server no longer lists this build's adapter: a newer Odyle reads differently, or the
        // operators switched this one off. The update is looked for at once instead of at the daily
        // check, and the reading is tried again quietly every ten minutes in case the server takes
        // the adapter back.
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.AdapterRefused && reading.ParserVersion == OdyleDecoder.Version)
        {
            var now = DateTimeOffset.UtcNow;
            if (!connection.AdapterOutdated) Log.Warn($"The server does not accept the adapter {reading.ParserVersion}; Odyle {Updater.Current} needs an update to keep reading.");
            connection.AdapterOutdated = true; publishFailure = OutdatedHint; publishBlockedUntil = now.AddMinutes(10);
            SetReaderState(ReaderState.Problem, T("The server no longer accepts readings from this Odyle version."));
            if (now - outdatedCheckAt >= TimeSpan.FromHours(1)) { outdatedCheckAt = now; _ = updates.CheckAsync(manual: false); }
        }
        // A reading the build before an update decoded with another adapter, handed over by the restart,
        // that the server no longer takes: it is dropped, and this build reads the next Odyle change.
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.AdapterRefused)
        {
            Log.Info($"The reading handed over from the adapter {reading.ParserVersion} was refused; the next Odyle change is read again.");
            if (ReferenceEquals(attribution.Latest, reading)) attribution.Latest = null;
            publishFailure = null; SetReaderState(ReaderState.Listening, T("Updated · the value is read again at the next Odyle change."));
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            publishFailure = T(error.Message); publishBlockedUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            if (error.StatusCode == HttpStatusCode.Forbidden) link.NeedsReconnect = true;
            SetReaderState(ReaderState.Problem, T("The server refused the reading: {0}", T(error.Message)));
        }
        catch (Exception error) { link.NeedsReconnect = true; publishFailure = T("Connection problem"); SetReaderState(ReaderState.Problem, T("Could not send the reading: {0}", Describe(error))); }
        RenderHero();
        if (readerPaused) await FollowReaderPauseAsync();
    }
    private async Task CharacterChangedAsync(PacketCapture source, ulong character)
    {
        if (capture != source || attribution.TwoClients) return;
        await operations.WaitAsync();
        try
        {
            // Two capture interfaces can see the same connection, and each reports the switch.
            // Once the game named the new character the second report changes nothing.
            // The game switched characters on this connection: until it names the new one, no reading may be
            // attributed to either character. Once it named the new one, a second interface's report changes nothing.
            if (capture != source || !attribution.Switched(character)) return;
            locations.Switched();
            await InvalidateActiveAsync(); SetReaderState(ReaderState.Listening, T("Character changed · detecting the new character.")); RenderHero();
        }
        finally { operations.Release(); }
    }
    private async Task SaveDetectedCharacterAsync(PacketCapture next, GameIdentity identity)
    {
        await operations.WaitAsync();
        try
        {
            if (capture != next || attribution.TwoClients || api is null && !Offline) return;
            var key = ReaderAttribution.KeyOf(identity);
            // The game now plays a different character: a reading the previous one owns does not move over to it.
            if (attribution.Name(key, identity.CharacterName)) { locations.Switched(); await InvalidateActiveAsync(); }
            groups.Named(identity.Id);
            // A snapshot without Odyle that waited for a name belongs to this character, unless its Odyle was read already.
            if (attribution.NoteIdentity(next, identity, key, DateTimeOffset.UtcNow)) SetReaderState(ReaderState.Listening, T("Listening to AION2"));
            // Offline the character is followed as online, so a reading never moves to another one; only
            // saving it and sending its reading wait for the server (after the next connection or heartbeat).
            if (api is null)
            {
                pendingIdentity = (next, identity);
                attribution.Claim(key);
                Log.Info($"Offline: {identity.CharacterName} is followed and saved once the server answers.");
                RenderHero(); return;
            }
            if (attribution.DetectedKey != key)
            {
                // The server's names of the game servers win over this build's (GameIdentity.Learn). Asked when a character
                // is first named on a connection, and again at most once an hour after an answer that could not be read or
                // for a server neither this build nor the server named yet (the server reads PLAYNC's list every night).
                var now = DateTimeOffset.UtcNow;
                if (connection.ServersAskedAt == DateTimeOffset.MinValue || (!connection.ServersKnown || identity.Server is null && identity.Region is not null) && now - connection.ServersAskedAt > TimeSpan.FromHours(1))
                {
                    connection.ServersAskedAt = now;
                    // An answer that cannot be read keeps the names learned before.
                    try { GameIdentity.Learn((await api.GetServersAsync()).Servers); connection.ServersKnown = true; }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { Log.Warn("The server's regions and game servers could not be read: " + error.Message); }
                }
                identity = identity with { Server = GameIdentity.ServerOf(identity.ServerId).Server };
                if (identity.Server is null || identity.Region is null)
                {
                    Status(T("Detected {0}, but its server ({1}) is not supported yet. Its Odyle is not saved.", identity.CharacterName, identity.ServerId));
                    RenderHero(); return;
                }
                var saved = await api.SaveCharacterAsync(identity.CharacterName, identity.Server, identity.Region);
                currentCharacterId = saved.Id; attribution.DetectedKey = key; await ReloadAsync();
                Log.Info($"{identity.CharacterName} · {identity.Server} is saved to the account."); Status("");
            }
            if (attribution.OdyleMissingKey == key) _ = MarkNoOdyleAsync();
            // A world load still waiting for its owner is where the character just named arrived.
            if (attribution.DetectedKey == key && currentCharacterId.Length > 0 && locations.Named(currentCharacterId, DateTimeOffset.UtcNow) is { } arrived) _ = SendLocationAsync(arrived);
            // Its party and force, each time the game names it (every few minutes): a reading that ended cleared them on the server.
            _ = SendGroupAsync();
            // Party and force members' names read before the character was saved.
            _ = SendTeammatesAsync();
            // PvP events read before the character was saved are its own.
            _ = SendPvpAsync();
            // A snapshot still waiting for its owner belongs to the character just named.
            attribution.Claim(key);
            if (attribution.Latest is not null) await PublishCoreAsync();
            RenderHero();
        }
        catch (Exception error)
        {
            // Offline or busy: the game does not name the character again until the next login, so try after the next heartbeat.
            if (error is HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError } or TaskCanceledException) pendingIdentity = (next, identity);
            Status(T("Could not save the detected character: {0}", Describe(error)));
            RenderHero();
        }
        finally { operations.Release(); }
    }

    // Rooms say a character has no Odyle yet once the server knows (POST /api/odyle/no-odyle). Once per
    // character and run; a server from before it, or a failed request, changes nothing here.
    private async Task MarkNoOdyleAsync()
    {
        if (api is null || currentCharacterId.Length == 0 || attribution.DetectedKey != attribution.OdyleMissingKey || connection.NoOdyleSent == currentCharacterId) return;
        var id = currentCharacterId; connection.NoOdyleSent = id;
        try { await api.NoOdyleAsync(id); Log.Info($"No Odyle yet on {attribution.GameName}: the rooms are told."); }
        catch (Exception error) { Log.Info($"No Odyle yet on {attribution.GameName}, not told to the server: {Describe(error)}"); }
    }

    // ----- Location sharing -----
    // An account that shares its location with its rooms (an opt-in on the dashboard; the session and
    // heartbeat answers say so) sends where its named character is (LocationReporter): one position at a
    // time, the newest waiting while one is on its way (ConnectionState.Location). Without the opt-in
    // nothing leaves the PC.
    private readonly LocationReporter locations = new();
    private void NoteShareLocation(bool? share)
    {
        var now = share == true;
        if (now == connection.ShareLocation) return;
        connection.ShareLocation = now; connection.LocationNext = null;
        Log.Info(now ? "This account shares its location with its rooms." : "This account does not share its location.");
        if (now && locations.Current is { } current) _ = SendLocationAsync(current);
    }
    // A place the server stored shows on the card at once, also without realtime; the server's news of
    // the character brings the same place.
    private void ShowOwnPlace(LocationReport report)
    {
        var index = Array.FindIndex(characters, c => c.Id == report.CharacterId);
        if (index < 0) return;
        var at = (DateTimeOffset.UtcNow + serverOffset).ToString("yyyy-MM-dd HH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        characters[index] = characters[index] with { Location = new Place(report.Map, report.X, report.Y, report.Z, report.Heading, report.Source, at) };
        RenderPlace();
    }
    private void OnLocation(PacketCapture source, GameLocation location)
    {
        if (capture != source || attribution.TwoClients) return;
        if (locations.Observe(location, DateTimeOffset.UtcNow) is { } report) _ = SendLocationAsync(report);
    }
    // Paused readers, an expired session, no connection: the position is dropped, and the next one is tried.
    private Task SendLocationAsync(LocationReport report)
    {
        var state = connection;
        if (!state.ShareLocation || !state.Location.Open || api is null || link.SessionId.Length == 0) return Task.CompletedTask;
        state.LocationNext = report;
        return state.Location.RunAsync(() =>
        {
            if (state != connection || !state.ShareLocation || api is not { } current || link.SessionId.Length == 0 || state.LocationNext is not { } next) return null;
            state.LocationNext = null;
            return SendPositionAsync(state, current, next);
        }, refused: () => state.LocationNext = null);
    }
    private async Task SendPositionAsync(ConnectionState state, PartyApi current, LocationReport report)
    {
        if (await current.LocationAsync(link.SessionId, report)) { ShowOwnPlace(report); return; }
        state.ShareLocation = false; state.LocationNext = null;
        Log.Info("The server says this account does not share its location; nothing is sent until it does.");
    }
    // What the location, party and force, teammates' names and PvP wait for: a connected session and
    // the character the game named, saved to the account.
    private bool NamedAndSaved => api is not null && link.SessionId.Length > 0 && currentCharacterId.Length > 0 && attribution.NamedSaved;

    // ----- Parties and forces -----
    // The party and force the named character is in (GroupTracker), sent when either changes and each
    // time the game names the character, so rooms can put players of one party or force together.
    // One request at a time; a change meanwhile sends the newest state after it (ConnectionState.Group).
    // A server from before parties (404), paused readers, an expired session, no connection: the next
    // change or naming tries again.
    private readonly GroupTracker groups = new();
    private void OnGroup(PacketCapture source, GameGroupEvent change)
    {
        if (capture != source || attribution.TwoClients || !groups.Observe(change)) return;
        Log.Info($"Decoder: {(groups.Party.Length > 0 ? "in a party" : "in no party")}, {(groups.Force.Length > 0 ? "in a force" : "in no force")} ({change.Change}).");
        _ = SendGroupAsync();
    }
    private bool GroupReady => NamedAndSaved && (groups.Owner is 0 || groups.Owner == attribution.GameId);
    private Task SendGroupAsync()
    {
        var state = connection;
        if (!GroupReady) return Task.CompletedTask;
        return state.Group.RunAsync(() => state == connection && GroupReady && api is { } current ? current.GameGroupAsync(link.SessionId, currentCharacterId, groups.Party, groups.Force) : null);
    }

    // ----- Party and force members -----
    // The names of the other members of the named character's party and force (GameGroups.Teammates),
    // for the armory's scout on the server, which looks them up on PLAYNC's public pages and keeps
    // none of them. Each name is sent once per connection, without the party or force, one request
    // at a time; names read meanwhile, or before the character is saved, wait for the next one
    // (up to GameGroups.TeammatesLimit). A 400 for the adapter (switched off under Server controls)
    // stops them until Odyle connects again; anything else (a server from before them answers 404) drops
    // that request's names. The account can turn them off on the dashboard (on by default, #81): the
    // session and heartbeat answers carry shareTeammates (left out by older backends: on), and an answer
    // { shared: false } turns them off until a session or heartbeat answer says true. While off the
    // names read are dropped and nothing is sent.
    private void NoteShareTeammates(bool? share)
    {
        var off = share == false;
        if (off == connection.TeammatesOff) return;
        connection.TeammatesOff = off;
        if (off) connection.TeammatesWaiting.Clear();
        Log.Info(off ? "This account does not share its party and force members' names; none are sent." : "This account shares its party and force members' names with the armory again.");
    }
    private void OnTeammates(PacketCapture source, IReadOnlyList<string> names)
    {
        var state = connection;
        if (capture != source || attribution.TwoClients || state.Teammates.Refused || state.TeammatesOff) return;
        foreach (var name in names)
            if (!state.TeammatesSent.Contains(name) && !state.TeammatesWaiting.Contains(name, StringComparer.OrdinalIgnoreCase) && state.TeammatesWaiting.Count < GameGroups.TeammatesLimit) state.TeammatesWaiting.Add(name);
        _ = SendTeammatesAsync();
    }
    private Task SendTeammatesAsync()
    {
        var state = connection;
        return state.Teammates.RunAsync(() =>
        {
            if (state != connection || state.TeammatesOff || state.TeammatesWaiting.Count == 0 || !NamedAndSaved || api is not { } current) return null;
            var names = state.TeammatesWaiting.ToArray(); state.TeammatesWaiting.Clear();
            return SendNamesAsync(state, current, names);
        }, refused: () => state.TeammatesWaiting.Clear());
    }
    private async Task SendNamesAsync(ConnectionState state, PartyApi current, string[] names)
    {
        if (!await current.TeammatesAsync(link.SessionId, currentCharacterId, names)) { if (state == connection) NoteShareTeammates(false); return; }
        // A long session forgets them rather than growing without end.
        if (state.TeammatesSent.Count > 5000) state.TeammatesSent.Clear();
        state.TeammatesSent.UnionWith(names);
    }

    // ----- PvP stats -----
    // An account that turned on PvP stats (an opt-in on the dashboard; the session and heartbeat
    // answers say so) has its named character's kills, assists and deaths read (GamePvp) and sent,
    // up to 20 at a time, one request at a time. Without the opt-in the decoder skips those frames:
    // nothing of them is read, kept or sent. The patch-day trace reads them to count them, and
    // sends nothing unless the account opted in. Events the server could not take wait up to an
    // hour (PvpOutbox) for the next event, heartbeat or naming; a server from before PvP stats (404),
    // paused readers, an expired session or no connection leave them waiting.
    private bool PvpWanted => connection.PvpStats && !connection.Pvp.Refused || trace is not null;
    private void NotePvpStats(bool? on)
    {
        var now = on == true;
        if (now == connection.PvpStats) return;
        connection.PvpStats = now;
        if (!now) connection.PvpOutbox.Clear();
        if (capture is { } reading) reading.ReadPvp = PvpWanted;
        Log.Info(now ? "This account has PvP stats on: kills, assists and deaths are read." : "This account has PvP stats off: no PvP frame is read.");
    }
    private void OnPvp(PacketCapture source, PvpEvent e)
    {
        if (capture != source || attribution.TwoClients) return;
        var state = connection; var on = state.PvpStats && !state.Pvp.Refused;
        // Never who: the log says only what happened.
        Log.Info($"Decoder: PvP {e.Kind.ToString().ToLowerInvariant()}{(on ? "" : " (not sent: PvP stats are off)")}.");
        if (!on) return;
        state.PvpOutbox.Add(e);
        _ = SendPvpAsync();
    }
    private Task SendPvpAsync()
    {
        var state = connection;
        return state.Pvp.RunAsync(() => state == connection && state.PvpStats && state.PvpOutbox.Count > 0 && NamedAndSaved && api is { } current ? SendPvpBatchesAsync(state, current) : null,
            refused: () => { state.PvpOutbox.Clear(); if (capture is { } reading) reading.ReadPvp = PvpWanted; });
    }
    private async Task SendPvpBatchesAsync(ConnectionState state, PartyApi current)
    {
        PvpReport[] batch;
        while (state.PvpStats && currentCharacterId.Length > 0 && attribution.GameId != 0 && (batch = state.PvpOutbox.Next(attribution.GameId, DateTimeOffset.UtcNow)).Length > 0)
        {
            try
            {
                if (!(await current.PvpAsync(link.SessionId, currentCharacterId, batch)).Enabled) { if (state == connection) NotePvpStats(false); return; }
            }
            // Events the server will not take as they are would be refused again: they are dropped.
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message != PartyApi.PvpAdapterRefused) { state.PvpOutbox.Sent(batch); Log.Warn($"The server refused {batch.Length} PvP events: {error.Message}"); return; }
            state.PvpOutbox.Sent(batch);
        }
    }

    // ----- Restart hand-over -----
    // The character the reader keeps live right now: named by the game, saved, its reading sent.
    private Handover? LiveHandover()
    {
        if (api is null || capture is null || attribution.Latest is not { } reading || activeCharacterId.Length == 0 || !attribution.OwnerSettled
            || CurrentCharacter is not { } character || character.Id != activeCharacterId) return null;
        // Its party and force go along when the rosters that said them listed the character the game names now, or the game
        // has named none yet (a start that took them over from a restart before it).
        var group = groups.Owner != 0 && (attribution.GameId == 0 || groups.Owner == attribution.GameId);
        return new Handover(api.Endpoint, accountId, character.Id, attribution.OwnerKey, character.CharacterName, reading.Total, reading.ParserVersion, reading.Source, DateTimeOffset.UtcNow, reading.Base, reading.Bonus,
            group ? groups.Party : "", group ? groups.Force : "", group ? groups.Owner : 0, group ? groups.Member : 0);
    }
    // The first start after an update or language restart takes over the character the previous
    // build was reading, for the same server and account. Sending its reading again ties it to this
    // start's session, so the heartbeat keeps it live without a relog; a game that stopped in the
    // meantime sends nothing and the reading turns stale after 20 seconds as usual (PulseAsync).
    // The reading keeps the adapter name that read it, also after an update that changed the decoder.
    // Caller holds the operations lock.
    private async Task TakeOverAsync()
    {
        if (api is null || capture is null || HandoverStore.Take(DateTimeOffset.UtcNow) is not { } handed) return;
        if (handed.Endpoint != api.Endpoint || handed.AccountId != accountId || Array.Find(characters, c => c.Id == handed.CharacterId) is not { } character) return;
        // A hand-over from a build before 1.2.1 carries the total only, and so does one from a build that took such a
        // hand-over over and read no change since. The saved reading's two numbers stand in when they add up to it, else
        // those of the latest change in the character's history (a reading sent again without them, as such a hand-over's
        // is, cleared the saved ones), so the Odyle clock, the rings and the taskbar show at once, not after the next change.
        if (handed.Base is null || handed.Bonus is null)
        {
            var parts = character.Parts is { } saved && saved.Base + saved.Bonus == handed.Total ? saved : await LatestPartsAsync(api, character.Id, handed.Total);
            if (parts is not null) handed = handed with { Base = parts.Base, Bonus = parts.Bonus };
            // The server was asked meanwhile: the account or the reader may have gone.
            if (api is null || capture is null) return;
        }
        // The reader starts before the server is asked (#86): a snapshot it read meanwhile came with a world load and
        // waits for the game to name its character like any other, so the character is not taken over.
        if (!attribution.TakeOver(handed, DateTimeOffset.UtcNow)) { Log.Info($"Did not take over {handed.CharacterName}: the game loaded a character since the restart."); return; }
        // The party and force the rosters said before the restart: the game sends no roster until the group changes.
        if (groups.Restore(handed.Party, handed.Force, handed.GroupOwner, handed.GroupMember))
            Log.Info($"Took over the group from before the restart: {(groups.Party.Length > 0 ? "in a party" : "in no party")}, {(groups.Force.Length > 0 ? "in a force" : "in no force")}.");
        currentCharacterId = character.Id;
        Log.Info($"Took over {handed.CharacterName} ({attribution.Latest!.Total} Odyle) from before the restart.");
        await PublishCoreAsync();
        RenderHero();
    }
    // The game's two numbers of the character's latest change in its history (PartyApi.GetLatestPartsAsync), or null when
    // the server cannot say: the taken-over total then shows without the clock until the next change, as before.
    private static async Task<OdyleParts?> LatestPartsAsync(PartyApi current, string characterId, int total)
    {
        try { return await current.GetLatestPartsAsync(characterId, total); }
        // ObjectDisposedException: the account was disconnected meanwhile.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            Log.Warn("The latest change's natural pool and items could not be read: " + error.Message);
            return null;
        }
    }

    // ----- Updates -----
    // A released build looks for a newer GitHub release at every start and once a day and
    // downloads and verifies it in the background. The check at start installs it right away
    // and restarts; one found later offers a restart and otherwise installs at the next start.
    private void RenderUpdate()
    {
        var build = Updater.SelfContained ? T("Odyle {0} · self-contained", Updater.Current) : T("Odyle {0}", Updater.Current);
        settings.UpdateLabel.Text = !updates.IsRelease() ? T("{0} · development build. Released builds update themselves from GitHub releases.", build)
            : updates.Staged is not null ? T("{0} · Odyle {1} is downloaded and verified. Restart to install it; otherwise it installs at the next start.", build, updates.Staged.Version)
            : updates.Note.Length > 0 ? $"{build} · {updates.Note}"
            : Updater.LastFailure is { } failure ? T("{0} · the downloaded update could not be installed: {1}", build, T(failure))
            : T("{0} · installs new releases at every start and looks for them once a day.", build);
        settings.InstallUpdateButton.Visibility = UpdateButton.Visibility = updates.Staged is not null ? Visibility.Visible : Visibility.Collapsed;
        settings.CheckUpdateButton.Visibility = updates.IsRelease() && updates.Staged is null ? Visibility.Visible : Visibility.Collapsed;
        settings.CheckUpdateButton.IsEnabled = !updates.Checking;
        UpdateButton.ToolTip = updates.Staged is null ? null : T("Install Odyle {0} and start it · the reader stops for a moment", updates.Staged.Version);
    }
    private async void Install_Click(object sender, RoutedEventArgs e) => await Guarded("Installing the update", () => updates.InstallAsync(automatic: false));
    // A package found later than the start's check: the player hears of it once per version, also in the tray.
    private void NotifyReady(StagedUpdate ready)
    {
        if (preferences.NotifiedUpdate == ready.Version.ToString()) return;
        Notify(T("Odyle {0} is ready", ready.Version), T("Restart Odyle to update, or it installs at the next start."));
        PreferencesStore.Save(preferences = preferences with { NotifiedUpdate = ready.Version.ToString() });
    }

    // ----- Session upkeep -----
    private async Task ReconnectAsync()
    {
        if (api is not { } current) return;
        var session = await link.RenewSessionAsync(current);
        activeCharacterId = ""; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue;
        Log.Info("Started a new publisher session."); NoteServerVersion(); Status(""); NoteReaderPause(session.ReaderPaused); NoteShareLocation(session.ShareLocation); NotePvpStats(session.PvpStats); NoteShareTeammates(session.ShareTeammates);
        if (readerPaused) await FollowReaderPauseAsync();
        if (capture is not null && attribution.Latest is not null) await PublishCoreAsync();
        RenderHero();
    }
    private async Task PulseAsync()
    {
        if (api is not { } current || !await operations.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            // Paused by a 429: nothing is sent, and the quiet time does not count as a lost connection.
            if (link.Waiting(current, now)) return;
            if (link.TokenDue(now)) { SavePairing(await current.SignInAsync()); link.TokenRenewedAt = now; }
            if (link.SessionDue(now)) await ReconnectAsync();
            if (api is null) return;
            if (capture is { } running && attribution.Latest is not null)
            {
                var bytes = running.GameBytes; var alive = bytes > lastGameBytes; lastGameBytes = bytes;
                // Re-sends only after the reading went stale; otherwise the heartbeat below keeps it live.
                if (alive) await PublishCoreAsync();
                else if (activeCharacterId.Length > 0) { await InvalidateActiveAsync(); SetReaderState(ReaderState.Stale, T("No readable game packets for 20 seconds · the last value is kept but marked stale.")); RenderHero(); }
            }
            var answer = await api.HeartbeatAsync(link.SessionId, activeCharacterId.Length > 0 ? activeCharacterId : null, ReaderStatus(), gameVersion); link.LastTick = DateTimeOffset.UtcNow;
            // A server updated while the app runs says so within one heartbeat.
            NoteServerVersion();
            // The pause switch reaches every paired app within one heartbeat, both ways.
            NoteReaderPause(answer.ReaderPaused); await FollowReaderPauseAsync(); NoteShareLocation(answer.ShareLocation); NotePvpStats(answer.PvpStats); NoteShareTeammates(answer.ShareTeammates);
            // PvP events that could not be sent (no connection, a busy server) go with the next heartbeat.
            _ = SendPvpAsync();
            if (busyShown || StatusLabel.Text == T("Server busy · retrying now.")) { busyShown = false; Status(""); }
            if (pendingIdentity is { } retry) { pendingIdentity = null; _ = Dispatcher.BeginInvoke(async () => await Guarded("Saving the named character", () => SaveDetectedCharacterAsync(retry.Capture, retry.Identity))); }
        }
        // With a device key the request already signed in again; still 401 means the pairing
        // ended (Disconnect elsewhere, Sign out everywhere, the PC removed on the dashboard).
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            Log.Warn("The server no longer knows this PC's pairing (401)."); await DisconnectAsync(Unpaired, clearSaved: true);
            if (!IsVisible) Notify(T("Odyle is no longer paired"), Unpaired);
        }
        catch (ServerBusyException error) { link.LastTick = error.Until; Log.Warn($"Server busy (429); requests pause until {error.Until.ToLocalTime():HH:mm:ss}."); RenderBusy(); }
        catch (Exception error) { link.NeedsReconnect = true; Log.Warn($"Heartbeat failed: {error.Message}"); Status(Describe(error)); }
        finally { operations.Release(); }
    }
    private async Task DisconnectAsync(string message, bool clearSaved)
    {
        await StopReaderAsync();
        // The pause is the server's: without a connection there is none.
        DropApi(); link.StopRetrying(); pendingIdentity = null; busyShown = false; readerWanted = false; readerPaused = false; if (clearSaved) { SessionStore.Clear(); link.Forget(); }
        Log.Info(clearSaved ? "Disconnected; the saved connection was removed." : "Disconnected.");
        activeCharacterId = ""; currentCharacterId = ""; accountName = ""; accountId = ""; characters = []; maps = []; rooms = []; snapshots = []; partyQueue.Clear(); tasks = []; tasksLoaded = false; tasksProblem = null;
        settings.RetryButton.Visibility = clearSaved ? Visibility.Collapsed : Visibility.Visible; ShowConnected(false); RenderRooms(); RenderParty(); RenderTasks(); ShowAnnouncement(null);
        Status(message); RenderHero();
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await Guarded("Disconnecting", () => ExecuteAsync(async () =>
    {
        if (api is { } current) await UnpairQuietlyAsync(current);
        await DisconnectAsync(T("Disconnected · your characters stay saved on your account."), clearSaved: true);
    }));
    // Best effort: offline, the key is still deleted from this PC with the saved session.
    private static async Task UnpairQuietlyAsync(PartyApi paired)
    {
        try { await paired.UnpairAsync(); } catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
    }
    // The patch-day trace ("Odyle.exe --decoder-trace", DecoderTrace): every opcode, stat layout and
    // stat resource is counted without the usual once-per-run limits, and the numbers are written
    // to logs\decoder-trace.json every minute and when Odyle quits. Null on a normal start.
    private DecoderTrace? trace;
    private DispatcherTimer? traceTimer;
    public static string TracePath => Path.Combine(Log.Folder, "decoder-trace.json");
    public void StartTrace()
    {
        trace = new DecoderTrace();
        if (capture is { } reading) reading.ReadPvp = PvpWanted;
        traceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        traceTimer.Tick += (_, _) => WriteTrace();
        traceTimer.Start();
        Log.Info($"Decoder trace on: every opcode, stat layout and stat resource is counted, and PvP frames by what they are read as (never who); the numbers go to {TracePath} every minute and when Odyle quits.");
    }
    private void WriteTrace()
    {
        if (trace is null) return;
        try { File.WriteAllText(TracePath, trace.ToJson(Updater.Current.ToString(), OdyleDecoder.Version)); Log.Info("Decoder trace: " + trace.Summary()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Warn("The decoder trace could not be written: " + error.Message); }
    }
    // The game build Steam installed (GameClient), logged whenever a game connection starts with
    // another build than before: a game patch then shows in the log beside what the decoder read.
    // The heartbeat sends it too.
    private string? gameVersion;
    private async Task NoteGameVersionAsync()
    {
        var version = await Task.Run(GameClient.Version);
        if (version is null || version == gameVersion) return;
        gameVersion = version;
        if (trace is not null) trace.GameVersion = version;
        Log.Info($"Game client {version}.");
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exit) { SavePosition(); e.Cancel = true; Hide(); settings.Hide(); } }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await Guarded("Quit", QuitAskedAsync);
    // Quit from the card or the tray: while a character is read and sent, quitting takes it offline
    // for the party, so Odyle asks first (Settings turns the question off).
    private async Task QuitAskedAsync()
    {
        if (preferences.ConfirmQuit && LiveHandover() is { } live && MessageBox.Show(T("Quit Odyle? It stops reading {0}, and your party sees {0} offline.\n\nSettings can turn this question off.", live.CharacterName), "Odyle", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await QuitAsync();
    }
    // "Odyle.exe --unpair" (the uninstaller) asks the running Odyle to quit before it forgets the pairing.
    public async void QuitOnRequest() { Log.Info("Another start of Odyle (--quit or --unpair) asked this one to quit."); await Guarded("Quitting on request", () => QuitAsync()); }
    // How quitting ends the app: restarted (in the tray when it was hidden) or shut down. The checks end nothing.
    internal Action<bool, bool> EndApp = (restart, tray) => { if (restart && System.Windows.Application.Current is App app) app.Restart(tray); else System.Windows.Application.Current.Shutdown(); };
    private async Task QuitAsync(bool restart = false)
    {
        // Quitting already (an update found at start while the user quits) is not repeated.
        if (quitting) return;
        quitting = true;
        if (!App.Headless) Microsoft.Win32.SystemEvents.PowerModeChanged -= Resumed;
        // A restart from the tray (an update at start with Windows) stays in the tray.
        var hidden = !IsVisible;
        SavePosition(); link.Heartbeat.Stop(); clock.Stop(); publishDelay.Stop(); partyTimer.Stop(); updates.Timer.Stop(); positionTimer.Stop(); link.RetryTimer.Stop(); npcapTimer.Stop(); traceTimer?.Stop(); WriteTrace();
        // An update being installed finishes first (its failure is the installer's to report).
        try { await updates.InstallRun; } catch (Exception) { }
        // A quit that came while an update was copied in restarts into it, handing the live character over.
        if (updates.Installed && !restart) { restart = true; Log.Info("An update was installed while quitting; Odyle restarts into it."); }
        await operations.WaitAsync();
        // A restart hands the live character to the next start (TakeOverAsync) instead of marking it stale.
        var handover = restart ? LiveHandover() : null;
        if (handover is not null) { HandoverStore.Save(handover); Log.Info($"Handing {handover.CharacterName} over to the restart."); }
        try { await Task.WhenAny(StopCaptureAsync(invalidate: handover is null), Task.Delay(4000)); realtime?.Dispose(); api?.Dispose(); discord.Dispose(); }
        finally
        {
            operations.Release(); tray.Dispose(); exit = true; settings.Exiting = true; settings.Close(); whatsNew?.Close();
            shortcut?.Dispose();
            EndApp(restart, hidden);
        }
    }
}
