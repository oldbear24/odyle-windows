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
public sealed record CharacterSharing(string Title, string Rooms);
public sealed record LanguageChoice(string Code, string Name);
public sealed record CompactMate(string Name, string Value, string Cubes, Brush Dot, string Hint);
public sealed record PartyMate(string Name, string Detail, string Value, string Cubes, Brush Dot, string Hint);
// Summary is the room's party line (RunChip.PartyLine), mint while the party can do a cube.
public sealed record PartyRoom(string Name, PartyMate[] Mates, string Summary = "", Brush? SummaryBrush = null)
{
    public string Header => Name.ToUpper(Culture);
    public string Online => T("{0} online", Mates.Length);
}
// One checklist task as the panel shows it: done within the current reset period or not.
public sealed record TaskItem(UserTask Task, bool Done, string Hint)
{
    public string Id => Task.Id;
    public string Title => Task.Title;
}
// The daily or weekly list; Reset counts down while the window is open.
public sealed class TaskGroup(string cadence, TaskItem[] tasks) : INotifyPropertyChanged
{
    private string reset = "", resetAt = "";
    public string Cadence => cadence;
    public TaskItem[] Tasks => tasks;
    public string Header => cadence == "daily" ? T("DAILY") : T("WEEKLY");
    public string Progress => $"  {tasks.Count(t => t.Done)} / {tasks.Length}";
    public double Percent => tasks.Length == 0 ? 0 : tasks.Count(t => t.Done) * 100.0 / tasks.Length;
    public string Empty => cadence == "daily" ? T("No daily tasks yet · add them on the dashboard.") : T("No weekly tasks yet · add them on the dashboard.");
    public Visibility EmptyVisibility => tasks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Reset { get => reset; set => Set(ref reset, value, nameof(Reset)); }
    public string ResetAt { get => resetAt; set => Set(ref resetAt, value, nameof(ResetAt)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref string field, string value, string name) { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
public partial class MainWindow : Window
{
    private enum ReaderState { Stopped, Waiting, Listening, Midstream, Live, Stale, Problem, Paused }
    private PartyApi? api;
    private PacketCapture? capture;
    private readonly SettingsWindow settings = new();
    private string sessionId = "", activeCharacterId = "", accountName = "", accountId = "";
    // The character always comes from the game; the user cannot pick one.
    // currentCharacterId: character shown in the card (last one the game named).
    // gameCharacterKey: identity the game last named (UnknownOwner after a switch it
    // has not named yet), gameCharacterId its id in the game, 0 when unknown.
    // detectedCharacterKey: that identity once saved to the account.
    // readingOwnerKey: identity that owns `latest`, "" while undecided.
    // nameDue: a snapshot came and the game has not named the character since. The game sends
    // one whenever it loads a character into the world (login, switch, teleport, channel change)
    // and names the character only after the loading screen, 6 to 10 seconds later.
    // namedHasReading: the named character's Odyle was read this run.
    // identityDue: a game connection resynced, and the lost bytes may have held a character
    // switch; the next reading waits, like a snapshot, for the game to name its character.
    // odyleMissingDue: a snapshot without Odyle waits, like any snapshot, for the game to name its
    // character; odyleMissingKey is then that character, until its Odyle is read.
    private const string UnknownOwner = "?";
    private string currentCharacterId = "", gameCharacterKey = "", gameCharacterName = "", detectedCharacterKey = "", readingOwnerKey = "", odyleMissingKey = "";
    private ulong gameCharacterId;
    private bool nameDue, namedHasReading, odyleMissingDue, identityDue;
    // The server refused this build's adapter name: Odyle needs an update to keep reading. While it
    // does, an update check runs at most once an hour (outdatedCheckAt).
    private bool adapterOutdated;
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
    private Room[] rooms = [];
    // Server-wide Odyle per cube: the cost shown while the account has no room yet.
    private int odylePerCube = 40;
    // Room snapshots for the party list; serverOffset corrects this PC's clock for presence.
    private RoomSnapshot[] snapshots = [];
    private TimeSpan serverOffset;
    private Task? partyRefresh;
    private bool partyAgain;
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
    private string? tasksProblem;
    private DateTimeOffset renderedDaily, renderedWeekly;
    // The character whose list the checklist shows with the account's (Checklist.Shown).
    private string? renderedTasksCharacter;
    // Pinned above other windows and the checklist beside the card: remembered on this PC.
    private Preferences preferences = new();
    private readonly Forms.ToolStripMenuItem pinItem = new(T("Stay on top")) { CheckOnClick = true };
    // Compact mode (the card cut down to your Odyle and your party's, in the same window and
    // place), its tray item, and the shortcut registered for it on a message-only window.
    private bool compactMode;
    private readonly Forms.ToolStripMenuItem compactItem = new(T("Compact mode")) { CheckOnClick = true };
    private HwndSource? hotkeyWindow;
    private string hotkeyProblem = "";
    // The value and cube cost the card shows, for the strip.
    private int? heroValue;
    // What's new (WhatsNewWindow), open or not.
    private WhatsNewWindow? whatsNew;
    private static string VersionText => $"{Updater.Current.Major}.{Updater.Current.Minor}.{Updater.Current.Build}";
    private int heroCost = 40;
    private long sequence, lastGameBytes;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow, publishBlockedUntil = DateTimeOffset.MinValue, latestAt, pendingSince, tokenRenewedAt;
    private PacketCandidate? latest;
    private int publishedTotal = -1;
    // The natural pool of the reading saved last, and the last change the game made (a spend, a
    // regeneration tick or an item), for the reading's kind (OdyleChange) and the card's split line.
    private int? publishedBase;
    private (int Delta, string Kind, DateTimeOffset At, string CharacterId)? lastChange;
    private string? publishFailure;
    private ReaderState readerState = ReaderState.Stopped;
    private bool exit, quitting, busy, needsReconnect, busyShown;
    // A saved pairing the server could not confirm at start (offline, 403, 5xx, 429) is
    // tried again by itself: 15 s, 30 s, 1 min, 2 min, then every 5 minutes (Retry.Backoff).
    private readonly DispatcherTimer restoreTimer = new();
    private DateTimeOffset? retryAt;
    private int restoreAttempt;
    private string retryProblem = "";
    // A detected character that could not be saved (offline, busy) is saved after the next heartbeat.
    private (PacketCapture Capture, GameIdentity Identity)? pendingIdentity;
    private string lastStatus = "", lastReaderLog = "";
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer publishDelay = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    // The Odyle clock's rules from the server; null from older backends (the defaults apply).
    private ClockRules? clockRules;
    // The account's notification settings (the dashboard's Account & data → Notifications).
    private NotificationSettings notifications = NotificationSettings.Default;
    private readonly DispatcherTimer partyTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    // An update check a few seconds after every start that installs what it finds, then once a
    // day while the app runs (UpdateSchedule). The timer looks every hour, so a day spent asleep
    // catches up within the hour.
    private readonly DispatcherTimer updateTimer = new() { Interval = UpdateSchedule.First };
    private readonly UpdateSchedule updateSchedule = new();
    // The window's position is remembered a second after it last moved.
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StagedUpdate? staged;
    private bool updating;
    private string updateNote = "";
    private readonly Forms.NotifyIcon tray;
    // The last tray notification, kept for the checks (the tray icon is off there).
    private (string Title, string Text)? LastNotice { get; set; }
    private int hiddenTicks;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow() : this(true) { }
    public MainWindow(bool enableTray)
    {
        InitializeComponent(); Translation.Apply(this);
        tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = T("Odyle · Not connected"), Visible = enableTray };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add(T("Open Odyle"), null, (_, _) => Dispatcher.Invoke(ShowWindow));
        pinItem.Click += (_, _) => Dispatcher.Invoke(() => SetStayOnTop(pinItem.Checked, persist: true)); menu.Items.Add(pinItem);
        compactItem.Click += (_, _) => Dispatcher.Invoke(() => { SetCompact(compactItem.Checked, persist: true); ShowWindow(); }); menu.Items.Add(compactItem);
        menu.Items.Add(T("Settings"), null, (_, _) => Dispatcher.Invoke(() => { ShowWindow(); OpenSettings(); }));
        menu.Items.Add(T("Quit"), null, async (_, _) => await Dispatcher.InvokeAsync(() => QuitAskedAsync()).Task.Unwrap()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow); tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowWindow);
        settings.PasteButton.Click += PasteConnect_Click; settings.JoinButton.Click += Pair_Click; settings.RetryButton.Click += Retry_Click;
        settings.DashboardButton.Click += (_, _) => OpenDashboard(""); settings.ManageButton.Click += (_, _) => OpenDashboard("#/"); settings.LogoutButton.Click += Logout_Click;
        settings.NpcapButton.Click += (_, _) => OpenLink(Npcap.DownloadPage); settings.KeyDown += Settings_KeyDown; settings.SourceInitialized += (_, _) => DarkFrame(settings);
        settings.DiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        settings.CheckUpdateButton.Click += async (_, _) => await CheckForUpdatesAsync(manual: true); settings.InstallUpdateButton.Click += Install_Click;
        settings.ReleaseNotesButton.Click += (_, _) => OpenLink(staged?.ReleaseUrl ?? ReleaseFeed.ReleasesPage.ToString());
        settings.WhatsNewButton.Click += (_, _) => OpenWhatsNew();
        settings.StayOnTopToggle.Click += (_, _) => SetStayOnTop(settings.StayOnTopToggle.IsChecked == true, persist: true);
        settings.TasksToggle.Click += (_, _) => SetTasksShown(settings.TasksToggle.IsChecked == true, persist: true);
        settings.AutostartToggle.Click += (_, _) => SetAutostart(settings.AutostartToggle.IsChecked == true);
        settings.ConfirmQuitToggle.Click += (_, _) => PreferencesStore.Save(preferences = preferences with { ConfirmQuit = settings.ConfirmQuitToggle.IsChecked == true });
        settings.NotificationsButton.Click += (_, _) => OpenDashboard("#/account");
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SavePosition(); };
        settings.LogButton.Click += (_, _) => { try { System.IO.Directory.CreateDirectory(Log.Folder); } catch (IOException) { } OpenLink(Log.Folder); };
        restoreTimer.Tick += async (_, _) =>
        {
            restoreTimer.Stop();
            if (busy) { restoreTimer.Interval = TimeSpan.FromSeconds(2); restoreTimer.Start(); return; }
            await ExecuteAsync(RestoreAsync);
        };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Stop();
            if (staged is null && updateSchedule.Due(DateTimeOffset.UtcNow)) { Updater.CleanUp(); await CheckForUpdatesAsync(manual: false); }
            if (quitting) return;
            updateTimer.Interval = updateSchedule.Next; updateTimer.Start();
        };
        // With realtime the party and the checklist follow the news, and every 20th tick (five
        // minutes) fetches them in case some was missed; run reminders are checked every tick.
        // Without it, open, they refresh every 15 seconds (showing the window refreshes at once)
        // and in the tray the rooms every minute, for the run reminders.
        partyTimer.Tick += async (_, _) =>
        {
            if (realtime?.IsConnected == true)
            {
                if (++partyTicks % 20 == 0) await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync());
                else CheckReminders();
            }
            else if (IsVisible) await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync());
            else if (notifications.RunsInTray && ++hiddenTicks % 4 == 0) await RefreshPartyAsync();
        };
        heartbeat.Tick += async (_, _) => await PulseAsync();
        npcapTimer.Tick += async (_, _) => await NpcapTickAsync();
        publishDelay.Tick += async (_, _) => { publishDelay.Stop(); AttributePending(); await PublishLatestAsync(); };
        clock.Tick += (_, _) => { if (capture is not null) RenderHero(); else RenderClock(); TickTasks(); RenderRetry(); RenderBusy(); RenderRunChip(); };
        clock.Start();
        ShowConnected(false); RenderRooms(); RenderParty(); RenderHero(); RenderUpdate();
        preferences = PreferencesStore.Load(); settings.ConfirmQuitToggle.IsChecked = preferences.ConfirmQuit; SetStayOnTop(preferences.StayOnTop, persist: false); SetTasksShown(preferences.ShowTasks, persist: false); RestorePosition();
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
        settings.LanguageRestartButton.Click += async (_, _) => await QuitAsync(restart: true);
    }
    private void ChooseLanguage(string choice)
    {
        if (choice == (preferences.Language is "en" or "cs" ? preferences.Language : Automatic)) return;
        PreferencesStore.Save(preferences = preferences with { Language = choice });
        settings.LanguageRestartPanel.Visibility = Resolve(choice, System.Globalization.CultureInfo.CurrentUICulture) != Localizer.Language ? Visibility.Visible : Visibility.Collapsed;
    }
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Odyle;component/Assets/odyle.ico"));
            if (resource is not null) return new System.Drawing.Icon(resource.Stream, 32, 32);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); DarkFrame(this); }
    // Windows 11: dark title bar and a border that matches the window.
    private static void DarkFrame(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            int dark = 1, caption = 0x0013160F, border = 0x00293024;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch { }
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); _ = RefreshPartyAsync(); _ = RefreshTasksAsync(); }
    // A second start of Odyle signals this one (App) to show its window in front.
    // Open in Odyle on the dashboard (odyle://pair?link=…). The link names the server, and any page could
    // open such a link, so Odyle asks before pairing with it and says when that replaces this PC's pairing.
    public async void OfferPairing(string link)
    {
        BringToFront();
        ConnectionDetails connection;
        try { connection = ConnectionDetails.FromAppLink(link); }
        catch (ArgumentException error) { Status(error.Message); Log.Warn("An Open in Odyle link was not taken: " + error.Message); return; }
        var host = new Uri(connection.Endpoint).Authority;
        var replaces = api is not null && accountName.Length > 0 ? "\n\n" + T("This replaces this PC's pairing with {0}.", accountName) : "";
        if (MessageBox.Show(this, T("Pair Odyle on this PC with the server {0}?", host) + replaces, "Odyle", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { Log.Info("An Open in Odyle link for " + host + " was declined."); return; }
        Log.Info("Pairing from an Open in Odyle link for " + host + ".");
        settings.EndpointInput.Text = connection.Endpoint; settings.CodeInput.Password = connection.Code;
        await ExecuteAsync(PairAsync);
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
    private void Status(string message)
    {
        StatusLabel.Text = message;
        if (message != lastStatus) { lastStatus = message; Log.Info("Status: " + message); }
    }
    // What a failed request means for the person at the PC, in their language.
    private static string Describe(Exception error) => error switch
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
    }

    // ----- Presentation -----
    private void SetReaderState(ReaderState state, string message)
    {
        readerState = state;
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
        ReaderDot.Fill = Brush(dot); PacketStateLabel.Foreground = Brush(text); PacketStateLabel.Text = message;
        // The Npcap link belongs to the line that says Npcap is missing or restricted (ShowNpcapProblem).
        NpcapLink.Visibility = Visibility.Collapsed;
        if (message != lastReaderLog)
        {
            lastReaderLog = message; Log.Info($"Reader {state}: {message}");
            readerHistory.Enqueue($"{DateTime.Now:HH:mm:ss} {state} · {message}"); while (readerHistory.Count > 10) readerHistory.Dequeue();
        }
    }
    private void SetPill(string dot, string text, string background, string label)
    {
        StateDot.Fill = Brush(dot); StateText.Foreground = Brush(text); StatePill.Background = Brush(background); StateText.Text = label;
    }
    private static string Age(DateTimeOffset moment)
    {
        var seconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - moment).TotalSeconds);
        return seconds < 60 ? T("{0}s ago", seconds) : seconds < 3600 ? T("{0}m ago", seconds / 60) : seconds < 86400 ? T("{0}h ago", seconds / 3600) : T("{0}d ago", seconds / 86400);
    }
    private void RenderHero()
    {
        // Offline: paired, but the server has not confirmed the pairing yet (it is tried again by itself);
        // the reader reads locally and what it read is sent once the server answers.
        var connected = api is not null; var offline = Offline;
        var character = CurrentCharacter;
        var cost = HeroCost(character);
        var reading = capture is not null ? latest : null;
        // A reading whose owner is not yet the shown character is displayed, but never under its name.
        var owned = reading is not null && readingOwnerKey.Length > 0 && readingOwnerKey == detectedCharacterKey;
        int? value = reading?.Total ?? (character is { Confirmed: true } ? character.Value : null);
        // The header keeps room for its buttons: it names a state, not the account (Settings does).
        AccountLabel.Text = offline ? T("OFFLINE") : !connected ? T("NOT CONNECTED") : accountName.Length > 0 ? "" : T("CONNECTING…");
        AccountLabel.Visibility = AccountLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroTitle.Text = !connected && !offline ? T("Not connected")
            : offline && reading is not null ? (gameCharacterName.Length > 0 && readingOwnerKey == gameCharacterKey ? T("{0} · offline", gameCharacterName) : T("Detecting character…"))
            : reading is not null && !owned ? (gameCharacterName.Length > 0 && gameCharacterKey != detectedCharacterKey && readingOwnerKey == gameCharacterKey ? T("{0} · not saved", gameCharacterName) : T("Detecting character…"))
            : character is not null ? $"{character.CharacterName} · {character.Server}" : T("No character yet");
        ValueLabel.Text = value is { } v ? v.ToString("N0", Culture) : "—";
        EnergyTrack.Value = value is { } energy ? energy % cost * 100.0 / cost : 0;
        EnergyTrack.Visibility = CubeLabel.Visibility = AgeLabel.Visibility = connected || offline ? Visibility.Visible : Visibility.Collapsed;
        CubeLabel.Text = value is { } total ? Tn(total / cost, "{0} cube", "{0} cubes") + $" · {total % cost}/{cost}" : connected ? T("{0} per cube", cost) : "";
        // With several rooms the count is at the cost of the first one the character is shared in; the tooltip names it.
        CubeLabel.ToolTip = CubeLabel.Text.Length == 0 ? null : rooms.Length > 1 && HeroRoom(character) is { } costRoom ? T("{0} · at the cost of {1}", CubeLabel.Text, costRoom.Name) : CubeLabel.Text;
        // The game's two numbers: the natural pool and what items added, and the last change it made.
        var (natural, items) = reading is not null ? (reading.Base, reading.Bonus) : (character?.Parts?.Base, character?.Parts?.Bonus);
        var split = (connected || offline) && natural is { } n && items is { } i ? T("natural {0:N0} · items {1:N0}", n, i) : "";
        if (split.Length > 0 && lastChange is { } last && last.CharacterId == character?.Id && (reading is null || owned)) split += " · " + T("{0} at {1:HH:mm}", last.Delta.ToString("+#,0;−#,0", Culture), last.At);
        SplitLabel.Text = split; SplitLabel.Visibility = split.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderOdyleClock();
        if (tasksLoaded && CurrentCharacter?.Id != renderedTasksCharacter) RenderTasks();
        // A character your rooms cannot see: you might think the party sees you when it does not.
        var unseen = connected && character is { Id.Length: > 0 } && rooms.Length > 0 && !rooms.Any(r => r.Shares(character.Id));
        ShareHintButton.Visibility = unseen ? Visibility.Visible : Visibility.Collapsed;
        if (!connected) SetPill("Faint", "Muted", "PillNeutral", T("Offline"));
        else if (readerPaused) SetPill("Amber", "Amber", "PillAmber", T("Paused"));
        else if (reading is not null && publishFailure is not null) SetPill("Rose", "Rose", "PillRose", T("Not saved"));
        else if (owned && activeCharacterId.Length > 0) SetPill("Mint", "Mint", "PillLive", T("Live"));
        // A snapshot waits for the loading screen to end before the game names its character.
        else if (reading is not null) SetPill("Amber", "Amber", "PillAmber", DateTimeOffset.UtcNow - latestAt < TimeSpan.FromSeconds(nameDue ? 30 : 6) ? T("Sending") : T("Stale"));
        else if (capture is not null) SetPill("MintSoft", "MintSoft", "PillNeutral", T("Listening"));
        else if (character is null) SetPill("Muted", "Muted", "PillNeutral", T("No character"));
        else if (character.Confirmed) SetPill("Muted", "Muted", "PillNeutral", T("Saved"));
        else SetPill("Muted", "Muted", "PillNeutral", T("No reading"));
        ReadingLabel.Text = offline ? (reading is not null ? T("Offline · {0:N0} Odyle read here, sent when the server is back.", reading.Total) : T("Offline · the reader reads locally and sends what it reads when the server is back."))
            : !connected ? T("Open settings to pair with the dashboard.")
            // The operators' announcement, when there is one, says why; it moves into the card meanwhile.
            : readerPaused ? (announcement is { } reason ? T("Paused by the server · {0}", reason.Text) : T("Paused by the server · reading resumes by itself when the operators allow it again."))
            : reading is not null && !owned ? (gameCharacterKey.Length == 0 && !nameDue ? T("The game names the character within a few minutes, or at once after a relog or channel change.") : T("Waiting for the game to name the character."))
            : reading is not null ? (adapterOutdated ? OutdatedHint : publishFailure ?? "")
            // The game's snapshot of the named character has no Odyle record; it is thought to unlock at level 22.
            : capture is not null ? (odyleMissingKey.Length > 0 && odyleMissingKey == gameCharacterKey ? T("No Odyle on {0} yet · it may unlock at level 22.", gameCharacterName)
                : character is null ? T("Log into a character · it is detected automatically.") : T("Listening for the game's Odyle snapshot…"))
            : character is null ? T("Start the reader, then log into your character.")
            : character.Confirmed ? T("Last saved value · start the reader for live values.")
            : T("No reading yet · start the reader before logging in.");
        // A saved live reading needs no caption: the card shows the total only.
        ReadingLabel.Visibility = ReadingLabel.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderClock();
        var name = character?.CharacterName ?? accountName;
        var tip = offline ? T("Odyle · Offline") : !connected ? T("Odyle · Not connected") : value is { } shown ? $"Odyle · {name} · {shown.ToString("N0", Culture)}" : $"Odyle · {name}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        heroValue = value; heroCost = cost; RenderCompact();
    }
    // Paired, but the server has not confirmed the pairing yet: it is tried again by itself (retryAt).
    private bool Offline => api is null && retryAt is not null;
    private string OutdatedHint => staged is not null ? T("Odyle needs an update to keep reading · Restart to update installs it.") : T("Odyle needs an update to keep reading · it looks for one every hour.");
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
        // dashboard) and the other members' characters online.
        var groups = snapshots.Select(s =>
            {
                var cost = s.Room.Cost > 0 ? s.Room.Cost : odylePerCube; var party = PartyReadiness.Party(PartyReadiness.Entries(s), now, cost);
                var mates = s.Members.Where(m => m.User != accountId)
                    .SelectMany(m => m.Characters.Where(c => c.Shared ?? true).Select(c => (Member: m, Character: c, State: Readiness.Of(c, now))))
                    .Where(x => x.State is Presence.Live or Presence.Stale)
                    .OrderBy(x => x.State).ThenByDescending(x => x.Character.Value)
                    .Select(x => Mate(x.Member, x.Character, x.State, s.Room.Cost, now)).ToArray();
                return (Party: party, Room: new PartyRoom(s.Room.Name, mates, RunChip.PartyLine(party, accountId), Brush(party.Cubes > 0 ? "Mint" : "Muted")));
            })
            .Where(g => g.Party.Characters.Length > 0).Select(g => g.Room).ToArray();
        PartyList.ItemsSource = groups;
        PartyPanel.Visibility = api is not null && rooms.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PartyEmptyLabel.Visibility = groups.All(g => g.Mates.Length == 0) ? Visibility.Visible : Visibility.Collapsed;
        var live = realtime?.IsConnected == true;
        PartyModeLabel.Text = live ? T("live updates") : T("refreshed every 15 s"); PartyModeLabel.Foreground = Brush(live ? "MintSoft" : "Faint");
        PartyModeLabel.ToolTip = live ? T("The party follows changes as they happen.") : realtime?.LastProblem is { } problem ? T("Live updates are off: {0}", T(problem)) : T("Live updates are not connected; the party is fetched every 15 seconds.");
        RenderRunChip();
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
        var notice = now < chipNoticeUntil ? chipNotice : "";
        RunChipBox.Visibility = next is not null || notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunChipLabel.Text = next is null ? "" : RunChip.Line(next, now); RunChipLabel.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        RunChipNotice.Text = notice; RunChipNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var viewer = next is not null && next.Room.Role == "viewer";
        RunGoingButton.Visibility = next is null || viewer ? Visibility.Collapsed : Visibility.Visible;
        RunGoingButton.Content = next is { Going: true } ? T("Going ✓") : T("Join");
        RunGoingButton.ToolTip = next is { Going: true } ? T("You are going · click to leave this run") : T("Go to this run");
        RenderCompact();
    }
    private async void RunGoing_Click(object sender, RoutedEventArgs e)
    {
        if (api is not { } current || chipRun is not { } run) return;
        RunGoingButton.IsEnabled = false;
        try { await current.GoingAsync(run.Room.Id, run.Run.Id, !run.Going); Log.Info($"{(run.Going ? "Left" : "Joined")} the planned run {run.Run.Id}."); await RefreshPartyAsync(); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException) { Status(T(error.Message)); }
        finally { RunGoingButton.IsEnabled = true; }
    }
    private PartyMate Mate(RoomMember member, Character c, Presence state, int cost, DateTimeOffset now)
    {
        cost = cost > 0 ? cost : odylePerCube;
        var live = state == Presence.Live; var cubes = c.Value / cost;
        var seen = !live && Readiness.Parse(c.LastSeenAt) is { } at ? " · " + T("seen {0}", Age(at)) : "";
        // The class leads the detail, so a narrow row trims the server rather than the class.
        var where = c.CharacterName == member.DisplayName ? c.Location : $"{member.DisplayName} · {c.Server}";
        var detail = c.ClassLine.Length > 0 ? $"{c.ClassLine} · {where}" : where;
        return new PartyMate(c.CharacterName, live ? detail : T("stale") + seen + " · " + detail,
            c.Value.ToString("N0", Culture), Tn(cubes, "{0} cube", "{0} cubes"), Brush(live ? "Mint" : "Amber"),
            (c.ClassLine.Length > 0 ? c.ClassLine + " · " : "") + (live ? T("{0} · live from the game", member.DisplayName) : T("{0} · last value; their reader stopped reporting", member.DisplayName)));
    }
    // The Odyle clock under the split: from the reading on screen, or the saved character's last
    // one (an estimate then), with the operators' rules. Ticked every second with the age.
    private void RenderOdyleClock()
    {
        var character = CurrentCharacter; var reading = capture is not null ? latest : null;
        var owned = reading is not null && readingOwnerKey.Length > 0 && readingOwnerKey == detectedCharacterKey;
        ClockState? state = null;
        if (api is not null && reading is { Base: { } natural, Bonus: { } items } && owned)
            state = OdyleClock.Of(natural, items, latestAt, DateTimeOffset.UtcNow, clockRules, character?.RegenTick, character?.NaturalMax, character?.AnsweredCap);
        else if (api is not null && reading is null && character is { Confirmed: true, Parts: { } parts } && Readiness.Parse(character.LastReadingAt) is { } saved)
            state = OdyleClock.Of(parts.Base, parts.Bonus, saved, DateTimeOffset.UtcNow, clockRules, character.RegenTick, character.NaturalMax, character.AnsweredCap);
        var line = OdyleClock.Line(state, DateTimeOffset.UtcNow, reading is not null);
        OdyleClockLabel.Text = line; OdyleClockLabel.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var mismatch = api is not null && character is not null ? OdyleClock.Mismatch(character.NaturalMax, character.AnsweredCap) : "";
        CapMismatchLabel.Text = mismatch; CapMismatchLabel.Visibility = mismatch.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RenderClock()
    {
        var character = CurrentCharacter;
        AgeLabel.Text = capture is not null && latest is not null ? T("updated {0}", Age(latestAt))
            : character is { Confirmed: true } && Readiness.Parse(character.LastReadingAt) is { } saved ? T("saved {0}", Age(saved)) : "";
        if (capture is { } running)
            CaptureStatsLabel.Text = T("{0:N0} packets · {1:N0} KB from the game", running.ReceivedPackets, running.GameBytes / 1024.0);
        RenderOdyleClock();
    }
    private void ShareHint_Click(object sender, RoutedEventArgs e) => OpenDashboard("#/");

    // ----- Operations -----
    private void SetBusy(bool value)
    {
        busy = value; settings.Body.IsEnabled = !value; RenderReaderButton();
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; SetBusy(true); await operations.WaitAsync();
        try { await action(); }
        catch (Exception error)
        {
            // A 429 only pauses requests; it never rotates the publisher session.
            if (api is not null && error is not ServerBusyException && (error is TaskCanceledException || error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500 || http.StatusCode == HttpStatusCode.Unauthorized))) needsReconnect = true;
            if (error is not (HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException)) Log.Error("Operation failed", error);
            Status(Describe(error));
        }
        finally { SetBusy(false); operations.Release(); RenderHero(); }
    }
    // Called by App once the window exists, shown or not. A start in the tray (with
    // Windows) stays there while the app is paired; unpaired, the window and settings open.
    public async Task StartAsync(bool tray)
    {
        // The update check does not wait for the server. Its first tick also removes the
        // Odyle.exe.old of the build that installed this one, which by then has exited.
        if (Updater.IsReleaseBuild && !App.Headless) updateTimer.Start();
        // Npcap missing shows in the reader row from the start, also before the app is paired.
        if (!Npcap.Probe()) ShowNpcapProblem(null);
        await ExecuteAsync(RestoreAsync);
        if (StartupCommand.ShowsWindow(tray, paired: api is not null || retryAt is not null))
        {
            if (!IsVisible) ShowWindow();
            if (api is null && !Offline) OpenSettings();
        }
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(RestoreAsync);
    private async Task RestoreAsync()
    {
        restoreTimer.Stop(); retryAt = null;
        if (api is not null) return;
        var saved = SessionStore.Load(out var unreadable);
        if (saved is null)
        {
            if (unreadable) { settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false); Status(T("The connection saved on this PC could not be read. Copy a new connection link from the dashboard to pair again.")); }
            return;
        }
        settings.EndpointInput.Text = saved.Endpoint; Status(T("Connecting to your account…"));
        PartyApi next;
        try { next = UseApi(new PartyApi(saved.Endpoint, saved.Token, saved.DeviceKey)); }
        catch (ArgumentException error) { Log.Error("The saved server address is not usable", error); SessionStore.Clear(); Status(T("The connection saved on this PC could not be read. Copy a new connection link from the dashboard to pair again.")); return; }
        try { var auth = await next.SignInAsync(); await SavePairingAsync(auth); await ConnectAsync(auth); }
        // Only 401 means the pairing ended (Disconnect, Sign out everywhere, the PC removed on the dashboard).
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            DropApi(); SessionStore.Clear(); restoreAttempt = 0; settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false);
            Log.Warn("The server no longer knows this PC's pairing; the saved connection was removed."); Status(Unpaired);
        }
        // Anything else (offline, 403, 5xx, 429, a timeout) keeps the pairing and tries again by itself.
        catch (Exception error)
        {
            DropApi(); settings.RetryButton.Visibility = Visibility.Visible; settings.ServerOptions.IsExpanded = true; ShowConnected(false);
            ScheduleRestore(error);
        }
        // The login snapshot names the character, so read from the moment the account is connected, or,
        // while the server cannot confirm the pairing, from its first failed try on (a reader stopped by
        // hand meanwhile stays stopped).
        if (capture is null && (api is not null || Offline && restoreAttempt <= 1)) await StartReaderAsync();
        if (capture is not null) await TakeOverAsync();
        // The character the game named while offline is saved and its reading sent now (not under this lock).
        if (api is not null && pendingIdentity is { } named) { pendingIdentity = null; _ = Dispatcher.BeginInvoke(async () => await SaveDetectedCharacterAsync(named.Capture, named.Identity)); }
    }
    private void ScheduleRestore(Exception error)
    {
        var now = DateTimeOffset.UtcNow; var wait = Retry.Backoff(restoreAttempt++);
        if (error is ServerBusyException paused && paused.Until - now > wait) wait = paused.Until - now;
        retryAt = now + wait; retryProblem = error is ServerBusyException ? "busy" : ""; restoreTimer.Interval = wait; restoreTimer.Start();
        Log.Warn($"The saved pairing could not be confirmed ({(error is HttpRequestException { StatusCode: { } code } ? (int)code : error.GetType().Name)}: {error.Message}); attempt {restoreAttempt}, next in {wait.TotalSeconds:0} s.");
        RenderRetry();
    }
    // Counts down to the next automatic try in the footer; Reconnect saved account tries at once.
    private void RenderRetry()
    {
        if (retryAt is not { } at || api is not null) return;
        var left = Wait(at - DateTimeOffset.UtcNow);
        StatusLabel.Text = retryProblem == "busy" ? T("Server busy · retrying in {0}.", left) : T("Server unavailable · retrying in {0}. Your pairing is kept.", left);
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
        api = next; next.AppVersion = Updater.Current.ToString();
        next.SignedIn += auth => Dispatcher.BeginInvoke(() => { if (api == next && auth.Record.Id == accountId) ShowAccount(auth.Record.DisplayName); });
        return next;
    }
    private void DropApi() { heartbeat.Stop(); partyTimer.Stop(); realtime?.Dispose(); realtime = null; api?.Dispose(); api = null; }
    private void ShowAccount(string displayName)
    {
        accountName = displayName;
        string host; try { host = new Uri(ConnectionDetails.NormalizeEndpoint(settings.EndpointInput.Text)).Host; } catch (ArgumentException) { host = T("server"); } catch (UriFormatException) { host = T("server"); }
        settings.AccountDetail.Text = T("Signed in as {0} via Discord · {1}", accountName, host);
        RenderHero();
    }
    private static string Unpaired => T("This PC is no longer paired with your account. Copy a new connection link from the dashboard.");
    // Saves the pairing on this PC. An app paired before device keys existed gets its key
    // here, so from now on it stays paired however long it is closed.
    private async Task SavePairingAsync(AuthResponse auth)
    {
        if (api is not { } current) return;
        try { await current.EnsureDeviceKeyAsync(); } catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
        try { SessionStore.Save(new SavedSession(current.Endpoint, auth.Token, current.DeviceKey)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { Log.Error("The connection could not be saved", error); Status(T("Connected, but this PC could not save the connection: {0}", error.Message)); }
    }
    private async Task ConnectAsync(AuthResponse auth)
    {
        if (api is null) return;
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; sequence = session.Sequence; activeCharacterId = ""; accountId = auth.Record.Id; ShowAccount(auth.Record.DisplayName);
        // A fresh connection: a backend without the switch never pauses readers.
        NoteReaderPause(session.ReaderPaused ?? false);
        restoreTimer.Stop(); retryAt = null; restoreAttempt = 0;
        Log.Info($"Connected to {api.Endpoint} as account {accountId} on device \"{PartyApi.DeviceName}\"{(api.DeviceKey is null ? " without a device key" : "")}.");
        settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(true);
        await ReloadAsync();
        Status(T("Connected to your account."));
        lastTick = tokenRenewedAt = DateTimeOffset.UtcNow; needsReconnect = false; heartbeat.Start();
        await RefreshPartyAsync(); await RefreshTasksAsync(); partyTimer.Start(); StartRealtime();
        await FollowReaderPauseAsync();
    }
    // Listens to the account's topic and its rooms'; rooms joined or left later change the
    // topics (RefreshPartyAsync).
    private void StartRealtime()
    {
        realtime?.Dispose(); realtime = null;
        if (api is not { } current || accountId.Length == 0) return;
        var next = new RealtimeClient(current); realtime = next;
        next.Connected += again => Dispatcher.BeginInvoke(async () =>
        {
            if (realtime != next) return;
            RenderParty();
            if (!again) Log.Info("Realtime connected; the party follows changes as they happen.");
            else await Task.WhenAll(RefreshPartyAsync(), RefreshTasksAsync());
        });
        next.Message += (topic, data) => Dispatcher.BeginInvoke(async () => { if (realtime == next) await OnNewsAsync(topic, RealtimeNews.Parse(data)); });
        next.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            if (realtime != next) return;
            Log.Warn($"Realtime dropped ({next.LastProblem ?? "no reason given"}); the party is fetched every 15 seconds until it is back.");
            RenderParty();
        });
        next.Start(RealtimeTopics.For(accountId, rooms));
    }
    // A room's changed characters and heartbeats are applied to the party in place, anything
    // else fetches the party again; the account's own news fetches its characters, rooms and
    // checklist again, or applies its changed characters.
    private async Task OnNewsAsync(string topic, RealtimeNews news)
    {
        if (topic.StartsWith(RealtimeTopics.Presence, StringComparison.Ordinal))
        {
            snapshots = PartyNews.WithSeen(snapshots, topic[RealtimeTopics.Presence.Length..], news.Seen); RenderParty();
            return;
        }
        if (topic.StartsWith(RealtimeTopics.Room, StringComparison.Ordinal))
        {
            if (!news.Removed && news.Characters.Length > 0)
            {
                var applied = PartyNews.WithCharacters(snapshots, topic[RealtimeTopics.Room.Length..], news.Characters, out var complete);
                if (complete) { snapshots = applied; RenderParty(); return; }
            }
            await RefreshPartyAsync();
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
        if (news.Notifications is { } saved) { notifications = saved; CheckReminders(); }
        // Cap alerts of the account's characters (the server checked the quiet hours).
        if (notifications.CapInTray) foreach (var alert in news.CapAlerts) { var (title, text) = alert.Notice(); Notify(title, text); Log.Info($"Told of the natural pool of {alert.CharacterId}: {alert.State}."); }
        if (news.Release is { } release && release > Updater.Current) { Log.Info($"The server says Odyle {release} is out; looking for it now."); await CheckForUpdatesAsync(manual: false); }
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
    private async Task ReloadAsync()
    {
        if (api is null) return;
        characters = await api.GetCharactersAsync(); var listed = await api.GetRoomsAsync(); rooms = listed.Rooms; odylePerCube = listed.OdylePerCube; clockRules = listed.Clock; await FollowNotificationsAsync(listed.Notifications); NoteReaderPause(listed.ReaderPaused); ShowAnnouncement(listed.Announcement);
        // Until the game names a character, show the one read most recently.
        if (CurrentCharacter is null) currentCharacterId = characters.OrderByDescending(c => c.LastReadingAt, StringComparer.Ordinal).FirstOrDefault()?.Id ?? "";
        RenderRooms(); RenderHero();
    }
    // The backend also serves the dashboard; "#/" opens My rooms & characters.
    private void OpenDashboard(string page)
    {
        try { Process.Start(new ProcessStartInfo(ConnectionDetails.NormalizeEndpoint(settings.EndpointInput.Text) + "/" + page) { UseShellExecute = true }); }
        catch (Exception error) { Status(T(error.Message)); }
    }
    private async void Pair_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PairAsync);
    private async void PasteConnect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (!System.Windows.Clipboard.ContainsText()) throw new InvalidOperationException(T("Copy the connection link from the dashboard first."));
        settings.CodeInput.Password = System.Windows.Clipboard.GetText().Trim();
        await PairAsync();
    });
    private async void Settings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && settings.JoinPanel.Visibility == Visibility.Visible && settings.CodeInput.IsKeyboardFocusWithin) { e.Handled = true; await ExecuteAsync(PairAsync); }
    }
    private async Task PairAsync()
    {
        var connection = ConnectionDetails.Parse(settings.CodeInput.Password, settings.EndpointInput.Text);
        var next = new PartyApi(connection.Endpoint); Status(T("Pairing with your account…"));
        try
        {
            var auth = await next.PairAsync(connection.Code); var previous = api; UseApi(next);
            // Pairing again replaces this PC's previous pairing.
            if (previous is not null) { await UnpairQuietlyAsync(previous); previous.Dispose(); }
            settings.EndpointInput.Text = connection.Endpoint; await SavePairingAsync(auth); settings.CodeInput.Clear(); await ConnectAsync(auth);
        }
        catch { if (api != next) next.Dispose(); throw; }
        if (capture is null) await StartReaderAsync();
    }
    private void UpdateCharacterInList(Character saved)
    {
        var index = Array.FindIndex(characters, x => x.Id == saved.Id);
        if (index >= 0) characters[index] = saved; else characters = [.. characters, saved];
    }

    // ----- Rooms -----
    // Read-only, so it runs beside reader operations without their lock. Rooms are
    // reloaded too: sharing changed on the dashboard shows up here.
    // A refresh asked for while one runs makes it run once more (partyAgain); the caller
    // waits for that, so it always sees the party as the server has it now.
    private async Task RefreshPartyAsync()
    {
        if (partyRefresh is { IsCompleted: false } running) { partyAgain = true; await running; return; }
        await (partyRefresh = RefreshPartyNowAsync());
    }
    private async Task RefreshPartyNowAsync()
    {
        if (api is not { } current) return;
        try
        {
            do
            {
                partyAgain = false;
                var listed = await current.GetRoomsAsync();
                var fetched = await Task.WhenAll(listed.Rooms.Select(async r => { try { return await current.GetSnapshotAsync(r.Id); } catch (HttpRequestException) { return null; } }));
                if (api != current) return;
                rooms = listed.Rooms; odylePerCube = listed.OdylePerCube; clockRules = listed.Clock; await FollowNotificationsAsync(listed.Notifications); snapshots = fetched.OfType<RoomSnapshot>().ToArray(); NoteReaderPause(listed.ReaderPaused); ShowAnnouncement(listed.Announcement);
                if (snapshots.Length > 0 && Readiness.Parse(snapshots[0].ServerNow) is { } server) serverOffset = server - DateTimeOffset.UtcNow;
                RenderRooms(); RenderParty(); RenderHero(); CheckReminders();
                if (realtime is { } listening) _ = listening.ListenAsync(RealtimeTopics.For(accountId, rooms));
            } while (partyAgain);
        }
        // Connection problems are reported by the heartbeat; keep the last list.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { }
    }
    // The server's announcement above the reading card, as plain text. Hiding it lasts
    // until the text changes; a new message also shows up when the window is hidden.
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
    private void HideAnnouncement_Click(object sender, RoutedEventArgs e) { hiddenAnnouncement = announcement?.Text ?? ""; ShowAnnouncement(announcement); }
    // Tray notifications (toasts on Windows 10 and 11); clicking one opens the window.
    private void Notify(string title, string text)
    {
        LastNotice = (title, text); Log.Info("Notification: " + title);
        // Windows keeps 63 characters of a notification title and 255 of its text.
        if (tray.Visible) tray.ShowBalloonTip(10000, title.Length > 63 ? title[..62] + "…" : title, text.Length > 255 ? text[..254] + "…" : text, Forms.ToolTipIcon.Info);
    }
    // The account's notification settings from the rooms answer. A backend without them keeps
    // the old local switch; with them, an old "off" moves to the account once (only while the
    // account never saved any), and Settings links to the dashboard from then on.
    private async Task FollowNotificationsAsync(NotificationSettings? listed)
    {
        if (listed is null) { notifications = NotificationSettings.Default with { Channels = new(Tray: preferences.RemindRuns) }; return; }
        notifications = listed;
        if (preferences.RemindRunsMoved || api is not { } current) return;
        if (!listed.Saved && !preferences.RemindRuns)
        {
            try { notifications = await current.SaveNotificationsAsync(listed with { Channels = listed.Where with { Tray = false } }); Log.Info("Moved the local choice to show no run reminders to the account's notification settings."); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { Log.Warn("Could not move the run reminder choice to the account yet: " + error.Message); return; }
        }
        PreferencesStore.Save(preferences = preferences with { RemindRunsMoved = true });
    }
    // Planned runs the account joined on the dashboard: a notification at each of the account's
    // reminder times before each start (Account & data → Notifications on the dashboard), with the
    // dungeon, room, local time and note, unless the tray is off for them or it is quiet hours.
    // Reminded runs are kept in preferences.json, so a restart does not repeat them; a run moved
    // to another time reminds again.
    private void CheckReminders()
    {
        if (!notifications.RunsInTray || accountId.Length == 0) return;
        var now = DateTimeOffset.UtcNow + serverOffset; var reminded = new HashSet<string>(preferences.RemindedRuns ?? []);
        var due = Reminders.Due(snapshots, accountId, now, reminded, notifications.Leads);
        if (due.Length == 0) return;
        var quiet = notifications.QuietAt(now);
        foreach (var run in due)
        {
            foreach (var key in run.Marks ?? [run.Key]) reminded.Add(key);
            if (quiet) { Log.Info($"Skipped the reminder of the planned run {run.Run.Id} in the account's quiet hours."); continue; }
            var minutes = Math.Max(1, (long)Math.Ceiling((run.StartsAt - now).TotalMinutes));
            var details = T("{0} · starts at {1:t}", run.Room.Name, run.StartsAt.ToLocalTime().DateTime) + (run.Run.Note.Length > 0 ? "\n" + run.Run.Note : "");
            Notify(Tn(minutes, "{1} starts in {0} minute", "{1} starts in {0} minutes", run.Run.Dungeon), details);
            Log.Info($"Reminded of the planned run {run.Run.Id} ({run.Run.Dungeon}) at {run.StartsAt:u}, {run.Lead} minutes ahead.");
        }
        PreferencesStore.Save(preferences = preferences with { RemindedRuns = Reminders.Prune(reminded, now) });
    }
    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { Status(T(error.Message)); }
    }

    // ----- Window -----
    // Pinned above other windows, for the game in windowed or borderless mode. The
    // header pin, the tray menu and settings toggle the same remembered choice.
    private void SetStayOnTop(bool on, bool persist)
    {
        Topmost = on; pinItem.Checked = on; settings.StayOnTopToggle.IsChecked = on;
        PinButton.Foreground = Brush(on ? "Mint" : "Muted");
        PinButton.ToolTip = on ? T("Pinned above other windows · click to unpin") : T("Stay on top of other windows, for example the game in windowed mode");
        if (persist) PreferencesStore.Save(preferences = preferences with { StayOnTop = on });
    }
    private void Pin_Click(object sender, RoutedEventArgs e) => SetStayOnTop(!Topmost, persist: true);
    // Compact mode in place of the card, in the same window: it keeps its place, title bar, pin and
    // tray, and only its content shrinks to your Odyle and your party's. In from the header button,
    // the tray or the shortcut; back the same way, with a double-click or with ⤢.
    private void SetCompact(bool on, bool persist)
    {
        compactItem.Checked = on;
        if (persist) PreferencesStore.Save(preferences = preferences with { CompactMode = on });
        if (on == compactMode) return;
        compactMode = on;
        Card.Visibility = on ? Visibility.Collapsed : Visibility.Visible; CompactPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CardColumn.Width = new GridLength(on ? CompactStrip.Width : 280);
        TasksPanel.Visibility = !on && settings.TasksToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RenderCompact(); Log.Info(on ? "Switched to compact mode." : "Switched back to the full card.");
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
            whatsNew.SourceInitialized += (_, _) => DarkFrame(whatsNew); whatsNew.Closed += (_, _) => whatsNew = null;
            if (IsVisible) whatsNew.Owner = this;
        }
        if (!App.Headless) { whatsNew.Show(); whatsNew.WindowState = WindowState.Normal; whatsNew.Activate(); }
    }
    private void Expand_Click(object sender, RoutedEventArgs e) => SetCompact(false, persist: true);
    private void CompactPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) SetCompact(false, persist: true); }
    private void ToggleCompact() { SetCompact(!compactMode, persist: true); if (!IsVisible) ShowWindow(); }
    // Your row (the card's character, value, cubes and state dot), then the other members'
    // characters online in your rooms (CompactStrip.Mates), then a short note when needed.
    private void RenderCompact()
    {
        if (!compactMode) return;
        var now = DateTimeOffset.UtcNow + serverOffset; var connected = api is not null;
        CompactName.Text = connected && CurrentCharacter is { } character ? character.CharacterName : HeroTitle.Text;
        CompactValue.Text = ValueLabel.Text; CompactDot.Fill = StateDot.Fill;
        CompactCubes.Text = connected && heroValue is { } value ? Tn(value / heroCost, "{0} cube", "{0} cubes") : "";
        CompactPanel.ToolTip = T("{0} · {1} · double-click for the full card", HeroTitle.Text, StateText.Text);
        var (mates, more) = connected ? CompactStrip.Mates(snapshots, accountId, now, odylePerCube) : ([], 0);
        CompactMates.ItemsSource = mates.Select(m => new CompactMate(m.Name, m.Value.ToString("N0", Culture), Tn(m.Cubes, "{0} cube", "{0} cubes"), Brush(m.Live ? "Mint" : "Amber"),
            m.Live ? T("{0} · live from the game", m.Player) : T("{0} · last value; their reader stopped reporting", m.Player))).ToArray();
        CompactNote.Text = !connected ? T("Open settings to pair with the dashboard.")
            : readerPaused ? T("Paused by the server")
            : more > 0 ? T("+{0} more online", more)
            : mates.Length == 0 ? (rooms.Length == 0 ? T("No rooms yet · create or join one on the dashboard.") : T("No one else is online in your rooms.")) : "";
        CompactNote.Visibility = CompactNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactDivider.Visibility = mates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChooseHotkey(string hotkey)
    {
        if (hotkey == preferences.CompactHotkey) return;
        PreferencesStore.Save(preferences = preferences with { CompactHotkey = hotkey }); UseHotkey(hotkey);
    }
    // The shortcut is registered for the whole session (RegisterHotKey). One another app holds
    // already says so in Settings, where another can be chosen.
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    private const int HotkeyId = 0x4F44;
    private void UseHotkey(string hotkey)
    {
        hotkeyProblem = "";
        if (!App.Headless)
        {
            if (hotkeyWindow is null)
            {
                hotkeyWindow = new HwndSource(new HwndSourceParameters("Odyle shortcut") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                hotkeyWindow.AddHook((IntPtr _, int message, IntPtr id, IntPtr _, ref bool handled) =>
                {
                    if (message == 0x0312 && id.ToInt32() == HotkeyId) { handled = true; ToggleCompact(); }
                    return IntPtr.Zero;
                });
            }
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
            if (CompactStrip.Parse(hotkey) is { } keys && !RegisterHotKey(hotkeyWindow.Handle, HotkeyId, keys.Modifiers, keys.Key))
            {
                hotkeyProblem = T("{0} is taken by another app · choose another shortcut.", hotkey);
                Log.Warn($"The shortcut {hotkey} could not be registered (error {Marshal.GetLastWin32Error()}).");
            }
        }
        settings.HotkeyProblemLabel.Text = hotkeyProblem; settings.HotkeyProblemLabel.Visibility = hotkeyProblem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactButton.ToolTip = CompactStrip.Parse(hotkey) is null || hotkeyProblem.Length > 0 ? T("Compact mode · your Odyle and your party's, nothing else") : T("Compact mode · your Odyle and your party's, nothing else ({0})", hotkey);
    }
    // The checklist opens beside the card; the window grows to the right.
    private void SetTasksShown(bool on, bool persist)
    {
        TasksPanel.Visibility = on && !compactMode ? Visibility.Visible : Visibility.Collapsed; settings.TasksToggle.IsChecked = on;
        TasksButton.Foreground = Brush(on ? "Mint" : "Muted");
        TasksButton.ToolTip = on ? T("Hide the daily & weekly tasks") : T("Show your daily & weekly tasks beside the card");
        if (persist) PreferencesStore.Save(preferences = preferences with { ShowTasks = on });
        RenderTasks(); if (on) _ = RefreshTasksAsync();
    }
    private void Tasks_Click(object sender, RoutedEventArgs e) => SetTasksShown(TasksPanel.Visibility != Visibility.Visible, persist: true);
    private void ManageTasks_Click(object sender, RoutedEventArgs e) => OpenDashboard("#/tasks");
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
        string host; try { host = new Uri(ConnectionDetails.NormalizeEndpoint(settings.EndpointInput.Text)).Host; } catch (Exception error) when (error is ArgumentException or UriFormatException) { host = "unknown server"; }
        var account = api is not null ? $"paired · {host} · {(realtime?.IsConnected == true ? "realtime" : "polling")}" : retryAt is not null ? $"paired · {host} · not reached yet" : "not paired";
        var reader = capture is { } running ? $"running · {readerState} · {running.ReceivedPackets:N0} packets · {running.GameBytes / 1024.0:N0} KB from the game" : $"stopped · {readerState}";
        return Diagnostics.Report(DateTimeOffset.Now, [
            ("App", $"Odyle {Updater.Current} · {Updater.Runtime} · {(Updater.SelfContained ? "self-contained" : "small")} package · {(Updater.IsReleaseBuild ? "release" : "development")} build"),
            ("Windows", $"{Environment.OSVersion} · language {Localizer.Language} ({Localizer.Culture.Name})"),
            ("Npcap", npcap), ("Capture interfaces", adapters), ("Account", account), ("Reader", reader),
            ("Adapter", ExperimentalOdyleDecoder.Version + (adapterOutdated ? " · refused by the server" : "")),
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
        if (api is not { } current || TasksPanel.Visibility != Visibility.Visible || refreshingTasks) return;
        refreshingTasks = true;
        try { var listed = await current.GetTasksAsync(); if (api == current) ApplyTasks(listed); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { if (api == current) { tasksProblem = T(error.Message); RenderTasks(); } }
        finally { refreshingTasks = false; }
    }
    private void ApplyTasks(TasksResponse listed)
    {
        tasks = listed.Tasks; resets = listed.Resets ?? new(); tasksLoaded = true; tasksProblem = null;
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
    private async void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: TaskItem item } box) return;
        box.IsEnabled = false;
        try { await ToggleTaskAsync(item, box.IsChecked == true); } finally { box.IsEnabled = true; }
    }
    private void RenderTasks()
    {
        var now = DateTimeOffset.UtcNow + serverOffset; var connected = api is not null;
        renderedDaily = Checklist.PeriodStart("daily", now, resets); renderedWeekly = Checklist.PeriodStart("weekly", now, resets);
        renderedTasksCharacter = CurrentCharacter?.Id; var shown = Checklist.Shown(tasks, renderedTasksCharacter);
        TaskGroups.ItemsSource = !connected ? Array.Empty<TaskGroup>() : new[] { "daily", "weekly" }.Select(cadence =>
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
        if (TasksPanel.Visibility != Visibility.Visible || api is null || TaskGroups.ItemsSource is not TaskGroup[] groups) return;
        var now = DateTimeOffset.UtcNow + serverOffset;
        if (Checklist.PeriodStart("daily", now, resets) != renderedDaily || Checklist.PeriodStart("weekly", now, resets) != renderedWeekly) { RenderTasks(); return; }
        foreach (var group in groups) group.Reset = ResetIn(group.Cadence, now);
    }

    // ----- Packet reader -----
    private async Task InvalidateActiveAsync()
    {
        if (api is null || activeCharacterId.Length == 0) return;
        var id = activeCharacterId; activeCharacterId = ""; publishedTotal = -1;
        try { await api.InvalidateAsync(id, sessionId); } catch { }
    }
    private void ResetAttribution() { gameCharacterKey = ""; gameCharacterName = ""; detectedCharacterKey = ""; readingOwnerKey = ""; gameCharacterId = 0; nameDue = false; namedHasReading = false; odyleMissingDue = false; odyleMissingKey = ""; identityDue = false; }
    // `latest` belongs to the character the game named: its updates follow it from now on.
    private void Attribute(string key) { readingOwnerKey = key; namedHasReading = true; if (odyleMissingKey == key) odyleMissingKey = ""; }
    private Task StopReaderAsync() => StopCaptureAsync(invalidate: true);
    // invalidate is false when a restart hands the live character over (QuitAsync).
    private async Task StopCaptureAsync(bool invalidate)
    {
        var old = capture; capture = null; ResetAttribution(); publishDelay.Stop();
        if (old is not null) await old.DisposeAsync();
        if (invalidate) await InvalidateActiveAsync();
        latest = null; publishFailure = null;
        RenderReaderButton(); ToolTipService.SetIsEnabled(ReaderRow, false);
        SetReaderState(ReaderState.Stopped, old is null ? T("Stopped · start the reader before logging in.") : T("Stopped · the last value stays saved on your account."));
        RenderHero();
    }
    private async void Reader_Click(object sender, RoutedEventArgs e)
    {
        if (api is null && capture is null && !Offline) { OpenSettings(); return; }
        await ExecuteAsync(async () =>
        {
            if (capture is not null) { readerWanted = false; await StopReaderAsync(); }
            else if (api is not null || Offline) await StartReaderAsync();
        });
    }
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
            if (kind is CaptureStatus.Listening or CaptureStatus.Waiting or CaptureStatus.TwoClients) connectionRead = false;
            // One of two game clients closed: start over like a new connection, the next identity names the character.
            else if (twoClients) { twoClients = false; latest = null; publishDelay.Stop(); ResetAttribution(); Log.Info("One game client is left; reading again."); }
            // A reading the game never attributed belongs to its connection; a new connection starts over.
            if (kind == CaptureStatus.Listening && readingOwnerKey.Length == 0 && latest is { Source: CandidateSource.Update }) { latest = null; publishDelay.Stop(); RenderHero(); }
            if (kind == CaptureStatus.Resyncing) OnResync();
            if (kind == CaptureStatus.Listening) _ = NoteGameVersionAsync();
            // A new game connection while a reading is live keeps the live line, and so do joining
            // one midstream while the character is known (taken over after a restart) and a resync.
            if (kind is CaptureStatus.Listening or CaptureStatus.Midstream or CaptureStatus.Resyncing or CaptureStatus.Resumed && readerState == ReaderState.Live) { Log.Info("Reader stays live: " + message); return; }
            SetReaderState(kind switch { CaptureStatus.Listening or CaptureStatus.Resumed => ReaderState.Listening, CaptureStatus.Midstream or CaptureStatus.Resyncing => ReaderState.Midstream, CaptureStatus.Problem or CaptureStatus.TwoClients => ReaderState.Problem, _ => ReaderState.Waiting }, message);
        });
        next.Candidate += observation => Dispatcher.BeginInvoke(() => OnCandidate(next, observation));
        next.SnapshotWithoutOdyle += () => Dispatcher.BeginInvoke(() => OnSnapshotWithoutOdyle(next));
        next.CharacterChanged += character => Dispatcher.BeginInvoke(async () => await CharacterChangedAsync(next, character));
        next.IdentityObserved += identity => Dispatcher.BeginInvoke(async () => await SaveDetectedCharacterAsync(next, identity));
        // Decoder diagnostics for a value that is wrong or missing: every Odyle update, stat
        // snapshot and identity, and once per run each distinct stat frame layout and each other
        // stat resource that sends updates (not every frame). The patch-day trace counts them all.
        var layouts = new HashSet<OdyleFrameLayout>(); var resources = new HashSet<uint>(); var limit = trace is null ? 64 : int.MaxValue;
        if (trace is { } counting)
        {
            next.FrameObserved += counting.Frame; next.SnapshotObserved += counting.Snapshot; next.UpdateObserved += counting.Update;
            next.LayoutObserved += counting.Layout; next.IdentityObserved += counting.Identity;
        }
        next.UpdateObserved += u =>
        {
            var odyle = u.Resource == ExperimentalOdyleDecoder.OdyleResource;
            if (!odyle) lock (resources) if (resources.Count >= limit || !resources.Add(u.Resource)) return;
            Log.Info($"Decoder: {(odyle ? "Odyle update" : "stat update, not Odyle (once per run)")}, resource {u.Resource}, tag {u.Tag:X2}, direction {u.Direction}, values {u.Base} + {u.Bonus}, {u.TrailerBytes} trailer bytes.");
        };
        next.SnapshotObserved += s => Log.Info($"Decoder: stat snapshot, {s.PayloadBytes} bytes, {(s.Records is { } count ? $"{count} records" : "layout not walked")}, {(s.Fields.Length == 0 ? "no Odyle record" : string.Join("; ", s.Fields.Select(f => $"Odyle {f.Base} + {f.Bonus} (tag {f.Tag:X2} at {f.Offset})")))}, {(s.Read ? "read" : "not read")}.");
        next.LayoutObserved += l => { lock (layouts) if (layouts.Count >= limit || !layouts.Add(l)) return; Log.Info($"Decoder: stat frame {l.Opcode:X4} {l.Header0:X2} {l.Header1:X2} {l.Header2:X2}, {l.PayloadBytes} bytes."); };
        next.IdentityObserved += identity => Log.Info($"Decoder: own character {identity.CharacterName}, server {identity.ServerId} ({identity.Server ?? "unmapped"}, {identity.Region ?? "unmapped region"}), class {identity.Class}.");
        try { next.Start(); }
        // Npcap missing, not loading or showing no interface: the reader row says why and links to
        // its download page, and the reader starts by itself once Npcap works (NpcapTickAsync).
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { await next.DisposeAsync(); ShowNpcapProblem(error); RenderHero(); return; }
        catch { await next.DisposeAsync(); throw; }
        npcapTimer.Stop();
        capture = next; lastGameBytes = 0; latest = null; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue; ResetAttribution();
        RenderReaderButton(); CaptureStatsLabel.Text = T("Waiting for packets…"); ToolTipService.SetIsEnabled(ReaderRow, true);
        SetReaderState(ReaderState.Waiting, T("Waiting for the AION2 game connection…"));
        Status(T("Reader running · log into your character to read its Odyle."));
    }
    private void OnCandidate(PacketCapture source, PacketCandidate observation)
    {
        if (capture != source || twoClients) return;
        connectionRead = true;
        // The same frame can arrive on two capture interfaces within milliseconds.
        var duplicate = latest is not null && latest.Total == observation.Total && DateTimeOffset.UtcNow - latestAt < TimeSpan.FromSeconds(2);
        if (duplicate && readingOwnerKey.Length > 0) { latestAt = DateTimeOffset.UtcNow; return; }
        latest = observation; latestAt = DateTimeOffset.UtcNow;
        // A snapshot is sent at login, at a character switch and at every teleport or
        // channel change, and the game names its character only after the loading
        // screen. Hold every snapshot until the game names its owner so it can never be
        // saved to the previous character. Updates keep the owner of their snapshot; one
        // read without any snapshot waits for an owner the same way.
        // After a resync the lost bytes may have held a switch, so the next reading waits the same way.
        if ((observation.Source == CandidateSource.Snapshot || identityDue) && !duplicate) { identityDue = false; readingOwnerKey = ""; nameDue = true; odyleMissingDue = false; pendingSince = latestAt; publishDelay.Stop(); publishDelay.Start(); }
        else if (readingOwnerKey.Length == 0 && !publishDelay.IsEnabled) { pendingSince = latestAt; publishDelay.Start(); }
        if (readerState is ReaderState.Waiting or ReaderState.Listening or ReaderState.Midstream) SetReaderState(ReaderState.Listening, T("Read {0:N0} Odyle from the game · saving…", observation.Total));
        RenderHero();
        if (readingOwnerKey.Length > 0) _ = PublishLatestAsync();
    }
    // A complete snapshot without an Odyle record: the character the game loads has no Odyle yet.
    // Like any snapshot it waits for the game to name its character; a reading still waiting for
    // a name belonged to the character the game left.
    private void OnSnapshotWithoutOdyle(PacketCapture source)
    {
        if (capture != source || twoClients) return;
        connectionRead = true;
        // The game sends a login's identity and snapshot either way round: one named a moment ago
        // is the snapshot's character. Any other snapshot waits for the game to name its character.
        if (latest is null && gameCharacterKey.Length > 0 && gameCharacterKey != UnknownOwner && DateTimeOffset.UtcNow - namedAt < TimeSpan.FromSeconds(10))
        {
            odyleMissingDue = false; odyleMissingKey = gameCharacterKey;
            SetReaderState(ReaderState.Listening, T("Listening to AION2 · {0} has no Odyle yet.", gameCharacterName));
            _ = MarkNoOdyleAsync(); RenderHero(); return;
        }
        odyleMissingDue = true;
        if (latest is not null && readingOwnerKey.Length == 0) { latest = null; publishDelay.Stop(); }
        RenderHero();
    }
    // A game connection resynced: the bytes it lost may have held a switch to another character
    // (its snapshot and identity), so the next reading waits for the game to name its character,
    // and an unnamed snapshot no longer goes to the named one after 30 seconds. The reading on the
    // card stays where it is.
    private void OnResync() { identityDue = true; namedHasReading = false; }
    // Two game clients run on this PC: their characters' messages would mix, so one character's
    // Odyle could be saved under the other. Nothing is read or published until one closes, and
    // the live reading turns stale meanwhile.
    private bool twoClients;
    // Whether this game connection gave an Odyle reading or a full snapshot without Odyle (a character
    // under level 22): a reader that listens for long without either is what a game patch looks like.
    private bool connectionRead;
    // What the heartbeat tells the backend's reader health about this PC's reader.
    private string ReaderStatus() =>
        readerPaused ? "paused" : twoClients ? "two-clients" : capture is null ? (npcapTimer.IsEnabled ? "npcap" : "stopped")
        : readerState switch { ReaderState.Waiting => "waiting", ReaderState.Midstream => "midstream", ReaderState.Problem => "problem", ReaderState.Live => "reading", _ => connectionRead ? "reading" : "listening" };
    private void OnTwoClients()
    {
        if (twoClients) return;
        twoClients = true; latest = null; publishDelay.Stop(); ResetAttribution();
        _ = InvalidateActiveAsync(); RenderHero();
    }
    // Runs when a reading has had no owner for the publish delay. Without any named character
    // the reading keeps waiting for the identity message.
    private void AttributePending()
    {
        if (capture is null || latest is null || readingOwnerKey.Length > 0 || gameCharacterKey.Length == 0 || gameCharacterKey == UnknownOwner) return;
        if (nameDue)
        {
            // After a snapshot the identity the game sends next names the owner, however long the
            // loading screen takes: the character named before it may be the one just left. One still
            // unnamed after 30 seconds came without a world load, and only goes to the named character
            // if that character's Odyle was already read this run.
            var waiting = DateTimeOffset.UtcNow - pendingSince < TimeSpan.FromSeconds(30);
            if (waiting) publishDelay.Start();
            if (waiting || !namedHasReading) return;
            nameDue = false;
        }
        Attribute(gameCharacterKey);
    }
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
        if (api is null || capture is null || latest is not { } reading) return;
        if (readingOwnerKey.Length == 0)
        {
            // Without any identity this run (a start after login) the game names the character when it
            // repeats its identity every few minutes, or at once at a relog or channel change; after a
            // snapshot it names it by itself once the loading screen ends.
            SetReaderState(ReaderState.Listening, gameCharacterKey.Length == 0 && !nameDue ? T("Read {0:N0} Odyle · the game names your character within a few minutes, or at once after a relog or channel change.", reading.Total) : T("Read {0:N0} Odyle · waiting for the game to name the character.", reading.Total)); RenderHero(); return;
        }
        if (readingOwnerKey != detectedCharacterKey || CurrentCharacter is not Character character)
        {
            SetReaderState(ReaderState.Listening, T("Read {0:N0} Odyle · this character's server is not supported, so it is not saved.", reading.Total)); RenderHero(); return;
        }
        if (DateTimeOffset.UtcNow < publishBlockedUntil) return;
        // A fresh snapshot of the value already saved (reconnect, channel change) is live again.
        if (publishedTotal == reading.Total && activeCharacterId == character.Id)
        {
            if (readerState == ReaderState.Listening) SetReaderState(ReaderState.Live, T("Live · {0:N0} Odyle saved to your account.", reading.Total));
            return;
        }
        try
        {
            // What changed since the reading saved before it on this live streak (none after a snapshot).
            var streak = publishedTotal >= 0 && activeCharacterId == character.Id;
            var (kind, delta) = OdyleChange.Of(reading.Source, reading.Total, reading.Base, streak ? publishedTotal : null, streak ? publishedBase : null);
            var saved = await api.ReportAsync(new PacketReading(sessionId, ++sequence, character.CharacterName, character.Server, character.Region, reading.Total, reading.ParserVersion, character.Id, Base: reading.Base, Bonus: reading.Bonus, Kind: kind, Delta: delta));
            publishedBase = reading.Base;
            if (delta is { } change && kind is OdyleChange.Spend or OdyleChange.Regen or OdyleChange.Item) lastChange = (change, kind, DateTimeOffset.Now, saved.Id);
            if (activeCharacterId.Length > 0 && activeCharacterId != saved.Id) { var previous = activeCharacterId; try { await api.InvalidateAsync(previous, sessionId); } catch { } }
            activeCharacterId = saved.Id; publishedTotal = reading.Total; lastTick = DateTimeOffset.UtcNow; publishFailure = null; adapterOutdated = false;
            UpdateCharacterInList(saved);
            SetReaderState(ReaderState.Live, T("Live · {0:N0} Odyle saved to your account.", reading.Total));
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
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.AdapterRefused && reading.ParserVersion == ExperimentalOdyleDecoder.Version)
        {
            var now = DateTimeOffset.UtcNow;
            if (!adapterOutdated) Log.Warn($"The server does not accept the adapter {reading.ParserVersion}; Odyle {Updater.Current} needs an update to keep reading.");
            adapterOutdated = true; publishFailure = OutdatedHint; publishBlockedUntil = now.AddMinutes(10);
            SetReaderState(ReaderState.Problem, T("The server no longer accepts readings from this Odyle version."));
            if (now - outdatedCheckAt >= TimeSpan.FromHours(1)) { outdatedCheckAt = now; _ = CheckForUpdatesAsync(manual: false); }
        }
        // A reading the build before an update decoded with another adapter, handed over by the restart,
        // that the server no longer takes: it is dropped, and this build reads the next Odyle change.
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == PartyApi.AdapterRefused)
        {
            Log.Info($"The reading handed over from the adapter {reading.ParserVersion} was refused; the next Odyle change is read again.");
            if (ReferenceEquals(latest, reading)) latest = null;
            publishFailure = null; SetReaderState(ReaderState.Listening, T("Updated · the value is read again at the next Odyle change."));
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            publishFailure = T(error.Message); publishBlockedUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            if (error.StatusCode == HttpStatusCode.Forbidden) needsReconnect = true;
            SetReaderState(ReaderState.Problem, T("The server refused the reading: {0}", T(error.Message)));
        }
        catch (Exception error) { needsReconnect = true; publishFailure = T("Connection problem"); SetReaderState(ReaderState.Problem, T("Could not send the reading: {0}", Describe(error))); }
        RenderHero();
        if (readerPaused) await FollowReaderPauseAsync();
    }
    private async Task CharacterChangedAsync(PacketCapture source, ulong character)
    {
        if (capture != source || twoClients) return;
        await operations.WaitAsync();
        try
        {
            // Two capture interfaces can see the same connection, and each reports the switch.
            // Once the game named the new character the second report changes nothing.
            if (capture != source || character == gameCharacterId) return;
            // The game switched characters on this connection. Until it names the
            // new one, no reading may be attributed to either character.
            gameCharacterKey = UnknownOwner; gameCharacterName = ""; detectedCharacterKey = ""; gameCharacterId = 0; namedHasReading = false; odyleMissingKey = "";
            if (readingOwnerKey.Length > 0) { latest = null; readingOwnerKey = ""; }
            await InvalidateActiveAsync(); SetReaderState(ReaderState.Listening, T("Character changed · detecting the new character.")); RenderHero();
        }
        finally { operations.Release(); }
    }
    private async Task SaveDetectedCharacterAsync(PacketCapture next, GameIdentity identity)
    {
        await operations.WaitAsync();
        try
        {
            if (capture != next || twoClients || api is null && !Offline) return;
            var key = identity.CharacterName + "|" + identity.ServerId;
            if (key != gameCharacterKey)
            {
                // The game now plays a different character. A reading the previous
                // one owns must not move over to it.
                gameCharacterKey = key; gameCharacterName = identity.CharacterName; detectedCharacterKey = ""; namedHasReading = false; odyleMissingKey = "";
                if (readingOwnerKey.Length > 0) { latest = null; readingOwnerKey = ""; }
                await InvalidateActiveAsync();
            }
            gameCharacterId = identity.Id; nameDue = false; identityDue = false; namedAt = DateTimeOffset.UtcNow;
            // A snapshot without Odyle that waited for a name belongs to this character, unless its
            // Odyle was read already (the reading is kept).
            if (odyleMissingDue)
            {
                odyleMissingDue = false;
                if (latest is null) { odyleMissingKey = key; SetReaderState(ReaderState.Listening, T("Listening to AION2 · {0} has no Odyle yet.", identity.CharacterName)); }
            }
            // Offline the character is followed as online, so a reading never moves to another one; only
            // saving it and sending its reading wait for the server (after the next connection or heartbeat).
            if (api is null)
            {
                pendingIdentity = (next, identity);
                if (latest is not null && readingOwnerKey.Length == 0) { Attribute(key); publishDelay.Stop(); }
                Log.Info($"Offline: {identity.CharacterName} is followed and saved once the server answers.");
                RenderHero(); return;
            }
            if (detectedCharacterKey != key)
            {
                if (identity.Server is null || identity.Region is null)
                {
                    Status(T("Detected {0}, but its server ({1}) is not supported yet. Its Odyle is not saved.", identity.CharacterName, identity.ServerId));
                    RenderHero(); return;
                }
                var saved = await api.SaveCharacterAsync(identity.CharacterName, identity.Server, identity.Region);
                currentCharacterId = saved.Id; detectedCharacterKey = key; await ReloadAsync();
                Status(T("{0} · {1} saved to your account.", identity.CharacterName, identity.Server));
            }
            if (odyleMissingKey == key) _ = MarkNoOdyleAsync();
            // A snapshot still waiting for its owner belongs to the character just named.
            if (latest is not null && readingOwnerKey.Length == 0) { Attribute(key); publishDelay.Stop(); }
            if (latest is not null) await PublishCoreAsync();
            RenderHero();
        }
        catch (Exception error)
        {
            // Offline or busy: the game does not name the character again until the next login, so try after the next heartbeat.
            if (error is HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError } or TaskCanceledException) pendingIdentity = (next, identity);
            Status(T("Could not save the detected character: {0}", Describe(error)));
        }
        finally { operations.Release(); }
    }

    // Rooms say a character has no Odyle yet once the server knows (POST /api/odyle/no-odyle). Once per
    // character and run; a server from before it, or a failed request, changes nothing here.
    private DateTimeOffset namedAt; private string noOdyleSent = "";
    private async Task MarkNoOdyleAsync()
    {
        if (api is null || currentCharacterId.Length == 0 || detectedCharacterKey != odyleMissingKey || noOdyleSent == currentCharacterId) return;
        var id = currentCharacterId; noOdyleSent = id;
        try { await api.NoOdyleAsync(id); Log.Info($"No Odyle yet on {gameCharacterName}: the rooms are told."); }
        catch (Exception error) { Log.Info($"No Odyle yet on {gameCharacterName}, not told to the server: {Describe(error)}"); }
    }

    // ----- Restart hand-over -----
    // The character the reader keeps live right now: named by the game, saved, its reading sent.
    private Handover? LiveHandover()
    {
        if (api is null || capture is null || latest is not { } reading || activeCharacterId.Length == 0 || readingOwnerKey.Length == 0
            || readingOwnerKey != detectedCharacterKey || readingOwnerKey != gameCharacterKey || CurrentCharacter is not { } character || character.Id != activeCharacterId) return null;
        return new Handover(api.Endpoint, accountId, character.Id, readingOwnerKey, character.CharacterName, reading.Total, reading.ParserVersion, reading.Source, DateTimeOffset.UtcNow);
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
        currentCharacterId = character.Id; gameCharacterKey = detectedCharacterKey = handed.CharacterKey; gameCharacterName = handed.CharacterName;
        Attribute(handed.CharacterKey);
        latest = new PacketCandidate(handed.Total, handed.ParserVersion, handed.Source); latestAt = DateTimeOffset.UtcNow;
        Log.Info($"Took over {handed.CharacterName} ({handed.Total} Odyle) from before the restart.");
        await PublishCoreAsync();
        if (activeCharacterId == character.Id) Status(T("Reader running · {0} stays live after the restart.", character.CharacterName));
        RenderHero();
    }

    // ----- Updates -----
    // A released build looks for a newer GitHub release at every start and once a day and
    // downloads and verifies it in the background. The check at start installs it right away
    // and restarts; one found later offers a restart and otherwise installs at the next start.
    private void RenderUpdate()
    {
        var build = Updater.SelfContained ? T("Odyle {0} · self-contained", Updater.Current) : T("Odyle {0}", Updater.Current);
        settings.UpdateLabel.Text = !Updater.IsReleaseBuild ? T("{0} · development build. Released builds update themselves from GitHub releases.", build)
            : staged is not null ? T("{0} · Odyle {1} is downloaded and verified. Restart to install it; otherwise it installs at the next start.", build, staged.Version)
            : updateNote.Length > 0 ? $"{build} · {updateNote}"
            : Updater.LastFailure is { } failure ? T("{0} · the downloaded update could not be installed: {1}", build, T(failure))
            : T("{0} · installs new releases at every start and looks for them once a day.", build);
        settings.InstallUpdateButton.Visibility = UpdateButton.Visibility = staged is not null ? Visibility.Visible : Visibility.Collapsed;
        settings.CheckUpdateButton.Visibility = Updater.IsReleaseBuild && staged is null ? Visibility.Visible : Visibility.Collapsed;
        settings.CheckUpdateButton.IsEnabled = !updating;
        UpdateButton.ToolTip = staged is null ? null : T("Install Odyle {0} and start it · the reader stops for a moment", staged.Version);
    }
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updating || staged is not null || !Updater.IsReleaseBuild) return;
        updating = true; updateNote = T("checking GitHub for a newer release…"); RenderUpdate();
        var install = false;
        try
        {
            var release = await Updater.CheckAsync(CancellationToken.None);
            // What the first automatic check of this start to reach GitHub finds is installed right away.
            install = updateSchedule.AtStart && !manual; updateSchedule.Reached(DateTimeOffset.UtcNow, upToDate: release is null);
            if (release is null) { updateNote = T("up to date · checked {0:HH:mm}", DateTime.Now); Log.Info($"Update check: Odyle {Updater.Current} is up to date."); return; }
            // A package an earlier start downloaded but could not install is not fetched again.
            if (Updater.Staged() is { } ready && ready.Version >= release.Version) staged = ready;
            else
            {
                Log.Info($"Update check: downloading Odyle {release.Version} ({Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")}).");
                updateNote = T("downloading Odyle {0}…", release.Version); RenderUpdate();
                var progress = new Progress<double>(part => { updateNote = T("downloading Odyle {0} · {1:P0}", release.Version, part); RenderUpdate(); });
                staged = await Updater.DownloadAsync(release, progress, CancellationToken.None);
            }
            updateNote = ""; Log.Info($"Odyle {staged.Version} is downloaded, verified and waiting in {staged.Directory}.");
            if (install) return;
            // Once per version, also from the tray.
            if (preferences.NotifiedUpdate != staged.Version.ToString())
            {
                Notify(T("Odyle {0} is ready", staged.Version), T("Restart Odyle to update, or it installs at the next start."));
                PreferencesStore.Save(preferences = preferences with { NotifiedUpdate = staged.Version.ToString() });
            }
            Status(T("Odyle {0} is ready · restart to update, or it installs at the next start.", staged.Version));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            updateSchedule.Failed(); Log.Warn($"Update check failed: {error.Message}");
            updateNote = manual ? T("update check failed: {0}", T(error.Message)) : T("could not check for updates: {0}", T(error.Message));
        }
        finally { updating = false; RenderUpdate(); }
        if (install && staged is not null) await InstallUpdateAsync(automatic: true);
    }
    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync(automatic: false);
    // The files are swapped while this build still runs; only then does it stop and
    // start the new one. A swap that fails leaves everything as it was. An installation
    // at start does not wait for a running operation to finish: quitting waits for it.
    private async Task InstallUpdateAsync(bool automatic)
    {
        if (staged is not { } update || quitting || (busy && !automatic)) return;
        Log.Info($"Installing Odyle {update.Version}{(automatic ? " found at start" : "")} and restarting.");
        try { Updater.Apply(update); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error($"Odyle {update.Version} could not be installed", error);
            Status(T("Could not install the update: {0} Download it from the release page instead.", T(error.Message))); return;
        }
        Status(T("Installing Odyle {0} · restarting…", update.Version));
        await QuitAsync(restart: true);
    }

    // ----- Session upkeep -----
    private async Task ReconnectAsync()
    {
        if (api is null) return;
        PublisherSession session;
        // The previous session may be gone (expired, or deleted with a PC removed on the dashboard);
        // a fresh one names the device key, and a removed PC answers 401 there.
        try { session = await api.StartSessionAsync(sessionId); }
        catch (HttpRequestException error) when (error.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)) { session = await api.StartSessionAsync(); }
        sessionId = session.SessionId; sequence = session.Sequence; needsReconnect = false; activeCharacterId = ""; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue;
        Log.Info("Started a new publisher session."); Status(T("Reconnected to your account.")); NoteReaderPause(session.ReaderPaused);
        if (readerPaused) await FollowReaderPauseAsync();
        if (capture is not null && latest is not null) await PublishCoreAsync();
        RenderHero();
    }
    private async Task PulseAsync()
    {
        if (api is not { } current || !await operations.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            // Paused by a 429: nothing is sent, and the quiet time does not count as a lost connection.
            if (current.BusyUntil > now) { lastTick = current.BusyUntil; return; }
            // Account tokens last seven days: renew daily so a long-running app never holds an expired one.
            if (now - tokenRenewedAt > TimeSpan.FromHours(24)) { await SavePairingAsync(await current.SignInAsync()); tokenRenewedAt = now; }
            if (needsReconnect || now - lastTick > TimeSpan.FromSeconds(45)) await ReconnectAsync();
            if (api is null) return;
            if (capture is { } running && latest is not null)
            {
                var bytes = running.GameBytes; var alive = bytes > lastGameBytes; lastGameBytes = bytes;
                // Re-sends only after the reading went stale; otherwise the heartbeat below keeps it live.
                if (alive) await PublishCoreAsync();
                else if (activeCharacterId.Length > 0) { await InvalidateActiveAsync(); SetReaderState(ReaderState.Stale, T("No readable game packets for 20 seconds · the last value is kept but marked stale.")); RenderHero(); }
            }
            var answer = await api.HeartbeatAsync(sessionId, activeCharacterId.Length > 0 ? activeCharacterId : null, ReaderStatus(), gameVersion); lastTick = DateTimeOffset.UtcNow;
            // The pause switch reaches every paired app within one heartbeat, both ways.
            NoteReaderPause(answer.ReaderPaused); await FollowReaderPauseAsync();
            if (busyShown || StatusLabel.Text == T("Server busy · retrying now.")) { busyShown = false; Status(T("Connected to your account.")); }
            if (pendingIdentity is { } retry) { pendingIdentity = null; _ = Dispatcher.BeginInvoke(async () => await SaveDetectedCharacterAsync(retry.Capture, retry.Identity)); }
        }
        // With a device key the request already signed in again; still 401 means the pairing
        // ended (Disconnect elsewhere, Sign out everywhere, the PC removed on the dashboard).
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            Log.Warn("The server no longer knows this PC's pairing (401)."); await DisconnectAsync(Unpaired, clearSaved: true);
            if (!IsVisible) Notify(T("Odyle is no longer paired"), Unpaired);
        }
        catch (ServerBusyException error) { lastTick = error.Until; Log.Warn($"Server busy (429); requests pause until {error.Until.ToLocalTime():HH:mm:ss}."); RenderBusy(); }
        catch (Exception error) { needsReconnect = true; Log.Warn($"Heartbeat failed: {error.Message}"); Status(Describe(error)); }
        finally { operations.Release(); }
    }
    private async Task DisconnectAsync(string message, bool clearSaved)
    {
        await StopReaderAsync();
        // The pause is the server's: without a connection there is none.
        DropApi(); restoreTimer.Stop(); retryAt = null; pendingIdentity = null; busyShown = false; readerWanted = false; readerPaused = false; if (clearSaved) SessionStore.Clear();
        Log.Info(clearSaved ? "Disconnected; the saved connection was removed." : "Disconnected.");
        activeCharacterId = ""; currentCharacterId = ""; accountName = ""; accountId = ""; characters = []; rooms = []; snapshots = []; tasks = []; tasksLoaded = false; tasksProblem = null;
        settings.RetryButton.Visibility = clearSaved ? Visibility.Collapsed : Visibility.Visible; ShowConnected(false); RenderRooms(); RenderParty(); RenderTasks(); ShowAnnouncement(null);
        Status(message); RenderHero();
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is { } current) await UnpairQuietlyAsync(current);
        await DisconnectAsync(T("Disconnected · your characters stay saved on your account."), clearSaved: true);
    });
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
        traceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        traceTimer.Tick += (_, _) => WriteTrace();
        traceTimer.Start();
        Log.Info($"Decoder trace on: every opcode, stat layout and stat resource is counted; the numbers go to {TracePath} every minute and when Odyle quits.");
    }
    private void WriteTrace()
    {
        if (trace is null) return;
        try { File.WriteAllText(TracePath, trace.ToJson(Updater.Current.ToString(), ExperimentalOdyleDecoder.Version)); Log.Info("Decoder trace: " + trace.Summary()); }
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
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAskedAsync();
    // Quit from the card or the tray: while a character is read and sent, quitting takes it offline
    // for the party, so Odyle asks first (Settings turns the question off).
    private async Task QuitAskedAsync()
    {
        if (preferences.ConfirmQuit && LiveHandover() is { } live && MessageBox.Show(T("Quit Odyle? It stops reading {0}, and your party sees {0} offline.\n\nSettings can turn this question off.", live.CharacterName), "Odyle", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await QuitAsync();
    }
    // "Odyle.exe --unpair" (the uninstaller) asks the running Odyle to quit before it forgets the pairing.
    public async void QuitOnRequest() { Log.Info("Another start of Odyle (--unpair) asked this one to quit."); await QuitAsync(); }
    private async Task QuitAsync(bool restart = false)
    {
        // Quitting already (an update found at start while the user quits) is not repeated.
        if (quitting) return;
        quitting = true;
        // A restart from the tray (an update at start with Windows) stays in the tray.
        var hidden = !IsVisible;
        SavePosition(); heartbeat.Stop(); clock.Stop(); publishDelay.Stop(); partyTimer.Stop(); updateTimer.Stop(); positionTimer.Stop(); restoreTimer.Stop(); npcapTimer.Stop(); traceTimer?.Stop(); WriteTrace(); await operations.WaitAsync();
        // A restart hands the live character to the next start (TakeOverAsync) instead of marking it stale.
        var handover = restart ? LiveHandover() : null;
        if (handover is not null) { HandoverStore.Save(handover); Log.Info($"Handing {handover.CharacterName} over to the restart."); }
        try { await Task.WhenAny(StopCaptureAsync(invalidate: handover is null), Task.Delay(4000)); realtime?.Dispose(); api?.Dispose(); }
        finally
        {
            operations.Release(); tray.Dispose(); exit = true; settings.Exiting = true; settings.Close(); whatsNew?.Close();
            if (hotkeyWindow is not null) { UnregisterHotKey(hotkeyWindow.Handle, HotkeyId); hotkeyWindow.Dispose(); }
            if (restart && System.Windows.Application.Current is App app) app.Restart(tray: hidden); else System.Windows.Application.Current.Shutdown();
        }
    }
}
