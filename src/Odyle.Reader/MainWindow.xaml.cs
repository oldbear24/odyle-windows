using System;
using System.ComponentModel;
using System.Diagnostics;
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
using Forms = System.Windows.Forms;

namespace Odyle.Reader;
public sealed record CharacterSharing(string Title, string Rooms);
public sealed record PartyMate(string Name, string Detail, string Value, string Cubes, Brush Dot, string Hint);
public sealed record PartyRoom(string Name, PartyMate[] Mates)
{
    public string Header => Name.ToUpperInvariant();
    public string Online => $"{Mates.Length} online";
}
public partial class MainWindow : Window
{
    private enum ReaderState { Stopped, Waiting, Listening, Midstream, Live, Stale, Problem }
    private PartyApi? api;
    private PacketCapture? capture;
    private readonly SettingsWindow settings = new();
    private string sessionId = "", activeCharacterId = "", accountName = "", accountId = "";
    // The character always comes from the game; the user cannot pick one.
    // currentCharacterId: character shown in the card (last one the game named).
    // gameCharacterKey: identity the game last named (UnknownOwner after a switch it
    // has not named yet). detectedCharacterKey: that identity once saved to the account.
    // readingOwnerKey: identity that owns `latest`, "" while undecided.
    private const string UnknownOwner = "?";
    private string currentCharacterId = "", gameCharacterKey = "", gameCharacterName = "", detectedCharacterKey = "", readingOwnerKey = "";
    private ulong? ownerEntity;
    private Character[] characters = [];
    private Room[] rooms = [];
    // Server-wide Odyle per cube: the cost shown while the account has no room yet.
    private int odylePerCube = 40;
    // Room snapshots for the party list; serverOffset corrects this PC's clock for presence.
    private RoomSnapshot[] snapshots = [];
    private TimeSpan serverOffset;
    private bool refreshingParty;
    private long sequence, lastGameBytes;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow, publishBlockedUntil = DateTimeOffset.MinValue, latestAt, pendingSince;
    private PacketCandidate? latest;
    private int publishedTotal = -1;
    private string? publishFailure;
    private ReaderState readerState = ReaderState.Stopped;
    private bool exit, busy, needsReconnect;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer publishDelay = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer partyTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Forms.NotifyIcon tray;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow() : this(true) { }
    public MainWindow(bool enableTray)
    {
        InitializeComponent();
        tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "Odyle · Not connected", Visible = enableTray };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Odyle", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(() => { ShowWindow(); OpenSettings(); }));
        menu.Items.Add("Quit", null, async (_, _) => await Dispatcher.InvokeAsync(QuitAsync).Task.Unwrap()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        settings.PasteButton.Click += PasteConnect_Click; settings.JoinButton.Click += Pair_Click; settings.RetryButton.Click += Retry_Click;
        settings.DashboardButton.Click += (_, _) => OpenDashboard(""); settings.ManageButton.Click += (_, _) => OpenDashboard("#/"); settings.LogoutButton.Click += Logout_Click;
        settings.NpcapButton.Click += Npcap_Click; settings.KeyDown += Settings_KeyDown; settings.SourceInitialized += (_, _) => DarkFrame(settings);
        // Only while the window is open; showing it refreshes at once.
        partyTimer.Tick += async (_, _) => { if (IsVisible) await RefreshPartyAsync(); };
        heartbeat.Tick += async (_, _) => await PulseAsync();
        publishDelay.Tick += async (_, _) => { publishDelay.Stop(); AttributePending(); await PublishLatestAsync(); };
        clock.Tick += (_, _) => { if (capture is not null) RenderHero(); else RenderClock(); };
        clock.Start();
        ShowConnected(false); RenderRooms(); RenderParty(); RenderHero();
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
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); _ = RefreshPartyAsync(); }
    private void OpenSettings()
    {
        if (settings.Owner is null && IsVisible) settings.Owner = this;
        settings.Show(); settings.WindowState = WindowState.Normal; settings.Activate();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Status(string message) => StatusLabel.Text = message;
    private Brush Brush(string key) => (Brush)FindResource(key);
    private Character? CurrentCharacter => Array.Find(characters, c => c.Id == currentCharacterId);
    private void ShowConnected(bool connected)
    {
        settings.JoinPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        settings.AccountPanel.Visibility = settings.RoomsPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        RenderReaderButton();
    }
    // Compact toggle beside the reader status: mint to start or connect, quiet while reading.
    private void RenderReaderButton()
    {
        ReportButton.Content = capture is not null ? "Stop" : api is not null ? "Start" : "Connect";
        if (capture is not null) ReportButton.Style = (Style)FindResource("Secondary"); else ReportButton.ClearValue(StyleProperty);
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
            _ => ("Rose", "Rose"),
        };
        ReaderDot.Fill = Brush(dot); PacketStateLabel.Foreground = Brush(text); PacketStateLabel.Text = message;
    }
    private void SetPill(string dot, string text, string background, string label)
    {
        StateDot.Fill = Brush(dot); StateText.Foreground = Brush(text); StatePill.Background = Brush(background); StateText.Text = label;
    }
    private static string Age(DateTimeOffset moment)
    {
        var seconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - moment).TotalSeconds);
        return seconds < 60 ? $"{seconds}s ago" : seconds < 3600 ? $"{seconds / 60}m ago" : seconds < 86400 ? $"{seconds / 3600}h ago" : $"{seconds / 86400}d ago";
    }
    private void RenderHero()
    {
        var connected = api is not null;
        var character = CurrentCharacter;
        var cost = HeroCost(character);
        var reading = capture is not null ? latest : null;
        // A reading whose owner is not yet the shown character is displayed, but never under its name.
        var owned = reading is not null && readingOwnerKey.Length > 0 && readingOwnerKey == detectedCharacterKey;
        int? value = reading?.Total ?? (character is { Confirmed: true } ? character.Value : null);
        AccountLabel.Text = !connected ? "NOT CONNECTED" : accountName.Length > 0 ? accountName.ToUpperInvariant() : "CONNECTING…";
        HeroTitle.Text = !connected ? "Not connected"
            : reading is not null && !owned ? (gameCharacterName.Length > 0 && gameCharacterKey != detectedCharacterKey && readingOwnerKey == gameCharacterKey ? gameCharacterName + " · not saved" : "Detecting character…")
            : character is not null ? $"{character.CharacterName} · {character.Server}" : "No character yet";
        ValueLabel.Text = value is { } v ? v.ToString("N0") : "—";
        EnergyTrack.Value = value is { } energy ? energy % cost * 100.0 / cost : 0;
        EnergyTrack.Visibility = CubeLabel.Visibility = AgeLabel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CubeLabel.Text = value is { } total ? $"{total / cost} {(total / cost == 1 ? "cube" : "cubes")} · {total % cost}/{cost}" : connected ? $"{cost} per cube" : "";
        if (!connected) SetPill("Faint", "Muted", "PillNeutral", "Offline");
        else if (reading is not null && publishFailure is not null) SetPill("Rose", "Rose", "PillRose", "Not saved");
        else if (owned && activeCharacterId.Length > 0) SetPill("Mint", "Mint", "PillLive", "Live");
        else if (reading is not null) SetPill("Amber", "Amber", "PillAmber", DateTimeOffset.UtcNow - latestAt < TimeSpan.FromSeconds(6) ? "Sending" : "Stale");
        else if (capture is not null) SetPill("MintSoft", "MintSoft", "PillNeutral", "Listening");
        else if (character is null) SetPill("Muted", "Muted", "PillNeutral", "No character");
        else if (character.Confirmed) SetPill("Muted", "Muted", "PillNeutral", "Saved");
        else SetPill("Muted", "Muted", "PillNeutral", "No reading");
        ReadingLabel.Text = !connected ? "Open settings to pair with the dashboard."
            : reading is not null && !owned ? (gameCharacterKey.Length == 0 ? "Relog or change channel once so the game names the character." : "Waiting for the game to name the character.")
            : reading is not null ? Components(reading) + (publishFailure is null ? "" : " · " + publishFailure)
            : capture is not null ? (character is null ? "Log into a character · it is detected automatically." : "Listening for the game's Odyle snapshot…")
            : character is null ? "Start the reader, then log into your character."
            : character.Confirmed ? "Last saved value · start the reader for live values."
            : "No reading yet · start the reader before logging in.";
        RenderClock();
        var name = character?.CharacterName ?? accountName;
        var tip = !connected ? "Odyle · Not connected" : value is { } shown ? $"Odyle · {name} · {shown:N0}" : $"Odyle · {name}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }
    // A single-value live update carries the total only; the login snapshot shows both components.
    private static string Components(PacketCandidate reading) => reading is { Primary: { } b, Bonus: { } x }
        ? $"{b:N0} base + {x:N0} bonus · {(reading.Source == CandidateSource.Update ? "live update" : "game snapshot")}"
        : "live update · base + bonus split after the next login";
    // Cube cost of the first room the character is shared in, else the first room.
    private int HeroCost(Character? character)
    {
        var room = (character is null ? null : Array.Find(rooms, r => r.Shares(character.Id))) ?? (rooms.Length > 0 ? rooms[0] : null);
        return room?.Cost is > 0 and var cost ? cost : odylePerCube;
    }
    // Sharing is configured on the dashboard; settings only summarizes it.
    private void RenderRooms()
    {
        settings.SharingList.ItemsSource = rooms.Length == 0 ? Array.Empty<CharacterSharing>() : characters.Select(c =>
        {
            var shared = rooms.Where(r => r.Shares(c.Id)).Select(r => r.Name).ToArray();
            return new CharacterSharing($"{c.CharacterName} · {c.Server}", shared.Length == 0 ? "Private in every room" : "Shared in " + string.Join(", ", shared));
        }).ToArray();
        settings.NoRoomsLabel.Text = characters.Length == 0 ? "No characters yet · start the reader and log into the game." : rooms.Length == 0 ? "No rooms yet · create or join one on the dashboard." : "";
        settings.NoRoomsLabel.Visibility = settings.NoRoomsLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    // Other members' characters that are live or recently stale, per room. Offline
    // characters and your own are left out; your own is the card above.
    private void RenderParty()
    {
        var now = DateTimeOffset.UtcNow + serverOffset;
        var groups = snapshots.Select(s => new PartyRoom(s.Room.Name, s.Members.Where(m => m.User != accountId)
                .SelectMany(m => m.Characters.Select(c => (Member: m, Character: c, State: Readiness.Of(c, now))))
                .Where(x => x.State is Presence.Live or Presence.Stale)
                .OrderBy(x => x.State).ThenByDescending(x => x.Character.Value)
                .Select(x => Mate(x.Member, x.Character, x.State, s.Room.Cost)).ToArray()))
            .Where(g => g.Mates.Length > 0).ToArray();
        PartyList.ItemsSource = groups;
        PartyPanel.Visibility = api is not null && rooms.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PartyEmptyLabel.Visibility = groups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private PartyMate Mate(RoomMember member, Character c, Presence state, int cost)
    {
        cost = cost > 0 ? cost : odylePerCube;
        var live = state == Presence.Live; var cubes = c.Value / cost;
        return new PartyMate(c.CharacterName, c.CharacterName == member.DisplayName ? c.Location : $"{member.DisplayName} · {c.Server}",
            c.Value.ToString("N0"), $"{cubes} {(cubes == 1 ? "cube" : "cubes")}{(live ? "" : " · stale")}", Brush(live ? "Mint" : "Amber"),
            live ? $"{member.DisplayName} · live from the game" : $"{member.DisplayName} · last value; their reader stopped reporting");
    }
    private void RenderClock()
    {
        var character = CurrentCharacter;
        AgeLabel.Text = capture is not null && latest is not null ? "updated " + Age(latestAt)
            : character is { Confirmed: true } && Readiness.Parse(character.LastReadingAt) is { } saved ? "saved " + Age(saved) : "";
        if (capture is { } running)
            CaptureStatsLabel.Text = $"{running.ReceivedPackets:N0} packets · {running.GameBytes / 1024.0:N0} KB from the game";
    }

    // ----- Operations -----
    private void SetBusy(bool value)
    {
        busy = value; ReportButton.IsEnabled = !value; settings.Body.IsEnabled = !value;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; SetBusy(true); await operations.WaitAsync();
        try { await action(); }
        catch (Exception error)
        {
            if (api is not null && (error is TaskCanceledException || error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500 || http.StatusCode == HttpStatusCode.Unauthorized))) needsReconnect = true;
            Status(error.Message);
        }
        finally { SetBusy(false); operations.Release(); RenderHero(); }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(RestoreAsync);
        if (api is null) OpenSettings();
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(RestoreAsync);
    private async Task RestoreAsync()
    {
        var saved = SessionStore.Load(); if (saved is null) return;
        settings.EndpointInput.Text = saved.Endpoint; api = new PartyApi(saved.Endpoint, saved.Token); Status("Connecting to your account…");
        try { var auth = await api.RefreshAsync(); SessionStore.Save(new SavedSession(saved.Endpoint, auth.Token)); await ConnectAsync(auth); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            api.Dispose(); api = null; SessionStore.Clear(); settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(false);
            Status("Connection expired. Copy a new connection link from the dashboard.");
        }
        catch
        {
            api.Dispose(); api = null; settings.RetryButton.Visibility = Visibility.Visible; settings.ServerOptions.IsExpanded = true; ShowConnected(false);
            Status("Server unavailable. Your connection is saved; reconnect in settings when it is back online.");
        }
        // The login snapshot names the character, so read from the moment the account is connected.
        if (api is not null && capture is null) await StartReaderAsync();
    }
    private async Task ConnectAsync(AuthResponse auth)
    {
        if (api is null) return;
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; sequence = session.Sequence; activeCharacterId = ""; accountName = auth.Record.DisplayName; accountId = auth.Record.Id;
        string host; try { host = new Uri(ConnectionDetails.NormalizeEndpoint(settings.EndpointInput.Text)).Host; } catch { host = "server"; }
        settings.AccountDetail.Text = $"Signed in as {accountName} via Discord · {host}";
        settings.RetryButton.Visibility = Visibility.Collapsed; ShowConnected(true);
        await ReloadAsync();
        Status("Connected to your account.");
        lastTick = DateTimeOffset.UtcNow; needsReconnect = false; heartbeat.Start();
        await RefreshPartyAsync(); partyTimer.Start();
    }
    private async Task ReloadAsync()
    {
        if (api is null) return;
        characters = await api.GetCharactersAsync(); var listed = await api.GetRoomsAsync(); rooms = listed.Rooms; odylePerCube = listed.OdylePerCube;
        // Until the game names a character, show the one read most recently.
        if (CurrentCharacter is null) currentCharacterId = characters.OrderByDescending(c => c.LastReadingAt, StringComparer.Ordinal).FirstOrDefault()?.Id ?? "";
        RenderRooms(); RenderHero();
    }
    // The backend also serves the dashboard; "#/" opens My rooms & characters.
    private void OpenDashboard(string page)
    {
        try { Process.Start(new ProcessStartInfo(ConnectionDetails.NormalizeEndpoint(settings.EndpointInput.Text) + "/" + page) { UseShellExecute = true }); }
        catch (Exception error) { Status(error.Message); }
    }
    private async void Pair_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PairAsync);
    private async void PasteConnect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (!System.Windows.Clipboard.ContainsText()) throw new InvalidOperationException("Copy the connection link from the dashboard first.");
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
        var next = new PartyApi(connection.Endpoint); Status("Pairing with your account…");
        try { var auth = await next.PairAsync(connection.Code); api?.Dispose(); api = next; settings.EndpointInput.Text = connection.Endpoint; SessionStore.Save(new SavedSession(connection.Endpoint, auth.Token)); settings.CodeInput.Clear(); await ConnectAsync(auth); }
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
    private async Task RefreshPartyAsync()
    {
        if (api is not { } current || refreshingParty) return;
        refreshingParty = true;
        try
        {
            var listed = await current.GetRoomsAsync();
            var fetched = await Task.WhenAll(listed.Rooms.Select(async r => { try { return await current.GetSnapshotAsync(r.Id); } catch (HttpRequestException) { return null; } }));
            if (api != current) return;
            rooms = listed.Rooms; odylePerCube = listed.OdylePerCube; snapshots = fetched.OfType<RoomSnapshot>().ToArray();
            if (snapshots.Length > 0 && Readiness.Parse(snapshots[0].ServerNow) is { } server) serverOffset = server - DateTimeOffset.UtcNow;
            RenderRooms(); RenderParty(); RenderHero();
        }
        // Connection problems are reported by the heartbeat; keep the last list.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { }
        finally { refreshingParty = false; }
    }
    private void Npcap_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });

    // ----- Packet reader -----
    private async Task InvalidateActiveAsync()
    {
        if (api is null || activeCharacterId.Length == 0) return;
        var id = activeCharacterId; activeCharacterId = ""; publishedTotal = -1;
        try { await api.InvalidateAsync(id, sessionId); } catch { }
    }
    private void ResetAttribution() { gameCharacterKey = ""; gameCharacterName = ""; detectedCharacterKey = ""; readingOwnerKey = ""; ownerEntity = null; }
    private async Task StopReaderAsync()
    {
        var old = capture; capture = null; ResetAttribution(); publishDelay.Stop();
        if (old is not null) await old.DisposeAsync();
        await InvalidateActiveAsync();
        latest = null; publishFailure = null;
        RenderReaderButton(); ToolTipService.SetIsEnabled(ReaderRow, false);
        SetReaderState(ReaderState.Stopped, old is null ? "Stopped · start the reader before logging in." : "Stopped · the last value stays saved on your account.");
        RenderHero();
    }
    private async void Reader_Click(object sender, RoutedEventArgs e)
    {
        if (api is null && capture is null) { OpenSettings(); return; }
        await ExecuteAsync(async () =>
        {
            if (capture is not null) await StopReaderAsync();
            else if (api is not null) await StartReaderAsync();
        });
    }
    private async Task StartReaderAsync()
    {
        var next = new PacketCapture();
        // Owners confirmed by earlier snapshots let a reader started after login trust live updates.
        foreach (var owner in OwnerStore.Load(accountId)) next.TrustOwner(owner);
        next.Status += (kind, message) => Dispatcher.BeginInvoke(() =>
        {
            if (capture != next) return;
            // A reading the game never attributed belongs to its connection; a new connection starts over.
            if (kind == CaptureStatus.Listening && readingOwnerKey.Length == 0 && latest is { Source: CandidateSource.Update }) { latest = null; publishDelay.Stop(); RenderHero(); }
            // A new game connection while a reading is live keeps the live line.
            if (kind == CaptureStatus.Listening && readerState == ReaderState.Live) return;
            SetReaderState(kind switch { CaptureStatus.Listening => ReaderState.Listening, CaptureStatus.Midstream => ReaderState.Midstream, CaptureStatus.Problem => ReaderState.Problem, _ => ReaderState.Waiting }, message);
        });
        next.Candidate += observation => Dispatcher.BeginInvoke(() => OnCandidate(next, observation));
        next.CharacterChanged += () => Dispatcher.BeginInvoke(async () =>
        {
            if (capture != next) return;
            await operations.WaitAsync();
            try
            {
                if (capture != next) return;
                // The game switched characters on this connection. Until it names the
                // new one, no reading may be attributed to either character.
                gameCharacterKey = UnknownOwner; gameCharacterName = ""; detectedCharacterKey = ""; ownerEntity = null;
                if (readingOwnerKey.Length > 0) { latest = null; readingOwnerKey = ""; }
                await InvalidateActiveAsync(); SetReaderState(ReaderState.Listening, "Character changed · detecting the new character."); RenderHero();
            }
            finally { operations.Release(); }
        });
        next.IdentityObserved += identity => Dispatcher.BeginInvoke(async () => await SaveDetectedCharacterAsync(next, identity));
        try
        {
            next.Start(); capture = next; lastGameBytes = 0; latest = null; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue; ResetAttribution();
            RenderReaderButton(); CaptureStatsLabel.Text = "Waiting for packets…"; ToolTipService.SetIsEnabled(ReaderRow, true);
            SetReaderState(ReaderState.Waiting, "Waiting for the AION2 game connection…");
            Status("Reader running · log into your character to read its Odyle.");
        }
        catch { await next.DisposeAsync(); throw; }
    }
    private void OnCandidate(PacketCapture source, PacketCandidate observation)
    {
        if (capture != source) return;
        // The same frame can arrive on two capture interfaces within milliseconds.
        var duplicate = latest is not null && latest.Total == observation.Total && DateTimeOffset.UtcNow - latestAt < TimeSpan.FromSeconds(2);
        if (duplicate && readingOwnerKey.Length > 0) { latestAt = DateTimeOffset.UtcNow; return; }
        latest = observation; latestAt = DateTimeOffset.UtcNow;
        // A snapshot is sent at login, and after a character switch the new
        // character's identity can follow it, often on a fresh connection. Hold
        // every snapshot until the game names its owner so it can never be saved
        // to the previous character. Updates keep the owner of their snapshot; one
        // read without any snapshot waits for an owner the same way.
        if (observation.Source == CandidateSource.Snapshot && !duplicate) { readingOwnerKey = ""; pendingSince = latestAt; publishDelay.Stop(); publishDelay.Start(); OwnerStore.Remember(accountId, observation.EntityId); }
        else if (readingOwnerKey.Length == 0 && !publishDelay.IsEnabled) { pendingSince = latestAt; publishDelay.Start(); }
        if (readerState is ReaderState.Waiting or ReaderState.Listening or ReaderState.Midstream) SetReaderState(ReaderState.Listening, $"Read {observation.Total:N0} Odyle from the game · saving…");
        RenderHero();
        if (readingOwnerKey.Length > 0) _ = PublishLatestAsync();
    }
    // Runs when no new identity followed a snapshot within the publish delay. Without
    // any named character the snapshot keeps waiting for the identity message.
    private void AttributePending()
    {
        if (capture is null || latest is not { } reading || readingOwnerKey.Length > 0 || gameCharacterKey.Length == 0 || gameCharacterKey == UnknownOwner) return;
        // A stat owner other than the named character's: give the game longer to name it.
        if (ownerEntity is { } known && known != reading.EntityId && DateTimeOffset.UtcNow - pendingSince < TimeSpan.FromSeconds(30)) { publishDelay.Start(); return; }
        readingOwnerKey = gameCharacterKey;
        if (reading.OwnerConfirmed) ownerEntity = reading.EntityId;
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
            // Without any identity this run (a start after login) only a relog or channel change names the character.
            SetReaderState(ReaderState.Listening, gameCharacterKey.Length == 0 ? $"Read {reading.Total:N0} Odyle · relog or change channel once so the game names your character." : $"Read {reading.Total:N0} Odyle · waiting for the game to name the character."); RenderHero(); return;
        }
        if (readingOwnerKey != detectedCharacterKey || CurrentCharacter is not Character character)
        {
            SetReaderState(ReaderState.Listening, $"Read {reading.Total:N0} Odyle · this character's server is not supported, so it is not saved."); RenderHero(); return;
        }
        if (DateTimeOffset.UtcNow < publishBlockedUntil) return;
        // A fresh snapshot of the value already saved (reconnect, channel change) is live again.
        if (publishedTotal == reading.Total && activeCharacterId == character.Id)
        {
            if (readerState == ReaderState.Listening) SetReaderState(ReaderState.Live, $"Live · {reading.Total:N0} Odyle saved to your account.");
            return;
        }
        try
        {
            var saved = await api.ReportAsync(new PacketReading(sessionId, ++sequence, character.CharacterName, character.Server, character.Region, reading.Total, reading.ParserVersion, character.Id));
            if (activeCharacterId.Length > 0 && activeCharacterId != saved.Id) { var previous = activeCharacterId; try { await api.InvalidateAsync(previous, sessionId); } catch { } }
            activeCharacterId = saved.Id; publishedTotal = reading.Total; lastTick = DateTimeOffset.UtcNow; publishFailure = null;
            UpdateCharacterInList(saved);
            SetReaderState(ReaderState.Live, $"Live · {reading.Total:N0} Odyle saved to your account.");
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            publishFailure = error.Message; publishBlockedUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            if (error.StatusCode == HttpStatusCode.Forbidden) needsReconnect = true;
            SetReaderState(ReaderState.Problem, "The server refused the reading: " + error.Message);
        }
        catch (Exception error) { needsReconnect = true; publishFailure = "Connection problem"; SetReaderState(ReaderState.Problem, "Could not send the reading: " + error.Message); }
        RenderHero();
    }
    private async Task SaveDetectedCharacterAsync(PacketCapture next, GameIdentity identity)
    {
        await operations.WaitAsync();
        try
        {
            if (capture != next || api is null) return;
            var key = identity.CharacterName + "|" + identity.ServerId;
            if (key != gameCharacterKey)
            {
                // The game now plays a different character. A reading the previous
                // one owns must not move over to it.
                gameCharacterKey = key; gameCharacterName = identity.CharacterName; detectedCharacterKey = ""; ownerEntity = null;
                if (readingOwnerKey.Length > 0) { latest = null; readingOwnerKey = ""; }
                await InvalidateActiveAsync();
            }
            if (detectedCharacterKey != key)
            {
                if (identity.Server is null || identity.Region is null)
                {
                    Status($"Detected {identity.CharacterName}, but its server ({identity.ServerId}) is not supported yet. Its Odyle is not saved.");
                    RenderHero(); return;
                }
                var saved = await api.SaveCharacterAsync(identity.CharacterName, identity.Server, identity.Region);
                currentCharacterId = saved.Id; detectedCharacterKey = key; await ReloadAsync();
                Status($"{identity.CharacterName} · {identity.Server} saved to your account.");
            }
            // A snapshot still waiting for its owner belongs to the character just named.
            if (latest is not null && readingOwnerKey.Length == 0)
            {
                readingOwnerKey = key; publishDelay.Stop();
                if (latest.OwnerConfirmed) ownerEntity = latest.EntityId;
            }
            if (latest is not null) await PublishCoreAsync();
            RenderHero();
        }
        catch (Exception error) { Status("Could not save the detected character: " + error.Message); }
        finally { operations.Release(); }
    }

    // ----- Session upkeep -----
    private async Task ReconnectAsync()
    {
        if (api is null) return;
        PublisherSession session;
        try { session = await api.StartSessionAsync(sessionId); }
        catch (HttpRequestException error) when (error.StatusCode is not HttpStatusCode.Unauthorized) { session = await api.StartSessionAsync(); }
        sessionId = session.SessionId; sequence = session.Sequence; needsReconnect = false; activeCharacterId = ""; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue;
        Status("Reconnected to your account.");
        if (capture is not null && latest is not null) await PublishCoreAsync();
        RenderHero();
    }
    private async Task PulseAsync()
    {
        if (api is null || !await operations.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (needsReconnect || now - lastTick > TimeSpan.FromSeconds(45)) await ReconnectAsync();
            if (api is null) return;
            if (capture is { } running && latest is not null)
            {
                var bytes = running.GameBytes; var alive = bytes > lastGameBytes; lastGameBytes = bytes;
                // Re-sends only after the reading went stale; otherwise the heartbeat below keeps it live.
                if (alive) await PublishCoreAsync();
                else if (activeCharacterId.Length > 0) { await InvalidateActiveAsync(); SetReaderState(ReaderState.Stale, "No game packets for 20 seconds · the last value is kept but marked stale."); RenderHero(); }
            }
            await api.HeartbeatAsync(sessionId, activeCharacterId.Length > 0 ? activeCharacterId : null); lastTick = DateTimeOffset.UtcNow;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { await DisconnectAsync("Connection expired. Copy a new connection link from the dashboard.", clearSaved: true); }
        catch (Exception error) { needsReconnect = true; Status(error.Message); }
        finally { operations.Release(); }
    }
    private async Task DisconnectAsync(string message, bool clearSaved)
    {
        await StopReaderAsync();
        heartbeat.Stop(); partyTimer.Stop(); api?.Dispose(); api = null; if (clearSaved) SessionStore.Clear();
        activeCharacterId = ""; currentCharacterId = ""; accountName = ""; accountId = ""; characters = []; rooms = []; snapshots = [];
        settings.RetryButton.Visibility = clearSaved ? Visibility.Collapsed : Visibility.Visible; ShowConnected(false); RenderRooms(); RenderParty();
        Status(message); RenderHero();
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => DisconnectAsync("Disconnected · your characters stay saved on your account.", clearSaved: true));
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exit) { e.Cancel = true; Hide(); settings.Hide(); } }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        heartbeat.Stop(); clock.Stop(); publishDelay.Stop(); partyTimer.Stop(); await operations.WaitAsync();
        try { await Task.WhenAny(StopReaderAsync(), Task.Delay(4000)); api?.Dispose(); }
        finally { operations.Release(); tray.Dispose(); exit = true; settings.Exiting = true; settings.Close(); System.Windows.Application.Current.Shutdown(); }
    }
}
