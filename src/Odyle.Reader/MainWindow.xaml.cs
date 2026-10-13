using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
public partial class MainWindow : Window
{
    private enum ReaderState { Stopped, Waiting, Listening, Midstream, Live, Stale, Problem }
    private PartyApi? api;
    private PacketCapture? capture;
    private string sessionId = "", activeCharacterId = "", selectedCharacterId = "", previousCharacterId = "", detectedCharacterKey = "", accountName = "";
    private long sequence, lastGameBytes;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow, lastPublishedAt = DateTimeOffset.MinValue, publishBlockedUntil = DateTimeOffset.MinValue, latestAt;
    private PacketCandidate? latest;
    private int publishedTotal = -1;
    private string? publishFailure;
    private ReaderState readerState = ReaderState.Stopped;
    private bool exit, busy, needsReconnect, loading, addingCharacter;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer publishDelay = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon tray;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow() : this(true) { }
    public MainWindow(bool enableTray)
    {
        InitializeComponent();
        tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "Odyle · Not connected", Visible = enableTray };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Odyle", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Quit", null, async (_, _) => await Dispatcher.InvokeAsync(QuitAsync).Task.Unwrap()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        heartbeat.Tick += async (_, _) => await PulseAsync();
        publishDelay.Tick += async (_, _) => { publishDelay.Stop(); await PublishLatestAsync(); };
        clock.Tick += (_, _) => { if (capture is not null) RenderHero(); else RenderClock(); };
        clock.Start();
        RenderHero();
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
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11: dark title bar and a border that matches the window.
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int dark = 1, caption = 0x0013160F, border = 0x00293024;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch { }
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Status(string message) => StatusLabel.Text = message;
    private Brush Brush(string key) => (Brush)FindResource(key);

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
    private static DateTimeOffset? Parse(string stamp) => DateTimeOffset.TryParse(stamp.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    private void RenderHero()
    {
        var connected = api is not null;
        var character = addingCharacter ? null : CharactersInput.SelectedItem as Character;
        var cost = (RoomsInput.SelectedItem as Room)?.Cost is > 0 and var roomCost ? roomCost : 40;
        var reading = capture is not null ? latest : null;
        int? value = reading?.Total ?? (character is { Confirmed: true } ? character.Value : null);
        AccountLabel.Text = !connected ? "NOT CONNECTED" : accountName.Length > 0 ? accountName.ToUpperInvariant() + " · DISCORD" : "CONNECTING…";
        HeroTitle.Text = !connected ? "Connect your Discord account" : character is not null ? character.CharacterName : addingCharacter && CharacterDetails.IsExpanded ? "New character" : "No character yet";
        ValueLabel.Text = value is { } v ? v.ToString("N0") : "—";
        EnergyTrack.Value = value is { } energy ? energy % cost * 100.0 / cost : 0;
        EnergyTrack.Visibility = CubeLabel.Visibility = AgeLabel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CubeLabel.Text = value is { } total ? $"{total / cost} {(total / cost == 1 ? "cube" : "cubes")} · {total % cost} / {cost} to the next" : connected && character is not null ? $"Reward cubes cost {cost} Odyle" : "";
        if (!connected) SetPill("Faint", "Muted", "PillNeutral", "Offline");
        else if (character is null) SetPill("Muted", "Muted", "PillNeutral", "No character");
        else if (reading is not null && publishFailure is not null) SetPill("Rose", "Rose", "PillRose", "Not saved");
        else if (reading is not null && activeCharacterId.Length > 0) SetPill("Mint", "Mint", "PillLive", "Live");
        else if (reading is not null) SetPill("Amber", "Amber", "PillAmber", DateTimeOffset.UtcNow - latestAt < TimeSpan.FromSeconds(6) ? "Sending" : "Stale");
        else if (capture is not null) SetPill("MintSoft", "MintSoft", "PillNeutral", "Listening");
        else if (character.Confirmed) SetPill("Muted", "Muted", "PillNeutral", "Saved");
        else SetPill("Muted", "Muted", "PillNeutral", "No reading");
        ReadingLabel.Text = !connected ? "Pair this app with the dashboard to see your Odyle here."
            : character is null ? (addingCharacter && CharacterDetails.IsExpanded ? "Enter the name, server and region below." : "Start the reader, then log into your character.")
            : reading is not null ? $"{reading.Primary:N0} base + {reading.Bonus:N0} bonus · {(reading.Source == CandidateSource.Update ? "live update" : "game snapshot")}{(publishFailure is null ? "" : " · " + publishFailure)}"
            : capture is not null ? "Listening for the game's Odyle snapshot…"
            : character.Confirmed ? "Last saved reading · start the reader for live values."
            : "No reading yet · start the reader before logging in.";
        RenderClock();
        var name = character?.CharacterName ?? accountName;
        var tip = !connected ? "Odyle · Not connected" : value is { } shown ? $"Odyle · {name} · {shown:N0}" : $"Odyle · {name}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }
    private void RenderClock()
    {
        var character = addingCharacter ? null : CharactersInput.SelectedItem as Character;
        AgeLabel.Text = capture is not null && latest is not null ? "updated " + Age(latestAt)
            : character is { Confirmed: true } && Parse(character.LastReadingAt) is { } saved ? "saved " + Age(saved) : "";
        if (capture is { } running)
            CaptureStatsLabel.Text = $"{running.ReceivedPackets:N0} packets · {running.GameBytes / 1024.0:N0} KB from the game";
    }

    // ----- Operations -----
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; busy = true; JoinButton.IsEnabled = false; PasteButton.IsEnabled = false; RetryButton.IsEnabled = false; ReaderPanel.IsEnabled = false; await operations.WaitAsync();
        try { await action(); }
        catch (Exception error)
        {
            if (api is not null && (error is TaskCanceledException || error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500 || http.StatusCode == HttpStatusCode.Unauthorized))) needsReconnect = true;
            Status(error.Message);
        }
        finally { busy = false; JoinButton.IsEnabled = true; PasteButton.IsEnabled = true; RetryButton.IsEnabled = true; ReaderPanel.IsEnabled = api is not null && JoinPanel.Visibility == Visibility.Collapsed; operations.Release(); RenderHero(); }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ExecuteAsync(RestoreAsync);
    private async void Retry_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(RestoreAsync);
    private async Task RestoreAsync()
    {
        var saved = SessionStore.Load(); if (saved is null) return;
        EndpointInput.Text = saved.Endpoint; api = new PartyApi(saved.Endpoint, saved.Token); Status("Connecting to your account…");
        try { var auth = await api.RefreshAsync(); SessionStore.Save(new SavedSession(saved.Endpoint, auth.Token)); await ConnectAsync(auth); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            api.Dispose(); api = null; SessionStore.Clear(); RetryButton.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible; ReaderPanel.Visibility = Visibility.Collapsed;
            Status("Connection expired. Copy a new connection link from the dashboard.");
        }
        catch
        {
            api.Dispose(); api = null; RetryButton.Visibility = Visibility.Visible; ServerOptions.IsExpanded = true;
            JoinPanel.Visibility = Visibility.Visible; ReaderPanel.Visibility = Visibility.Collapsed;
            Status("Server unavailable. Your connection is saved; use Reconnect when it is back online.");
        }
    }
    private async Task ConnectAsync(AuthResponse auth)
    {
        if (api is null) return;
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; sequence = session.Sequence; activeCharacterId = ""; addingCharacter = false; accountName = auth.Record.DisplayName;
        string host; try { host = new Uri(ConnectionDetails.NormalizeEndpoint(EndpointInput.Text)).Host; } catch { host = "server"; }
        AccountDetail.Text = $"Signed in as {accountName} via Discord · {host}";
        JoinPanel.Visibility = Visibility.Collapsed; ReaderPanel.Visibility = Visibility.Visible; RetryButton.Visibility = Visibility.Collapsed;
        await ReloadAsync();
        if (CharactersInput.SelectedItem is Character c) LoadCharacter(c);
        else BeginCharacterDraft(manual: false);
        Status("Connected · readings are saved to your account.");
        lastTick = DateTimeOffset.UtcNow; needsReconnect = false; heartbeat.Start();
    }
    private async Task ReloadAsync()
    {
        if (api is null) return;
        var characters = await api.GetCharactersAsync(); var rooms = await api.GetRoomsAsync();
        loading = true;
        try
        {
            var selectedRoomId = (RoomsInput.SelectedItem as Room)?.Id;
            if (!addingCharacter && selectedCharacterId.Length == 0 && characters.Length > 0) selectedCharacterId = characters[0].Id;
            CharactersInput.ItemsSource = characters; CharactersInput.SelectedItem = Array.Find(characters, c => c.Id == selectedCharacterId);
            CharactersInput.IsEnabled = characters.Length > 0;
            NoCharactersLabel.Text = characters.Length == 0 ? "No characters yet" : "Choose a saved character";
            NoCharactersLabel.Visibility = CharactersInput.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
            RoomsInput.ItemsSource = rooms; RoomsInput.SelectedItem = Array.Find(rooms, r => r.Id == selectedRoomId) ?? (rooms.Length > 0 ? rooms[0] : null);
        }
        finally { loading = false; }
        ApplyRoom(); RenderHero();
    }
    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ConnectionDetails.NormalizeEndpoint(EndpointInput.Text)) { UseShellExecute = true }); }
        catch (Exception error) { Status(error.Message); }
    }
    private async void Pair_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PairAsync);
    private async void PasteConnect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (!System.Windows.Clipboard.ContainsText()) throw new InvalidOperationException("Copy the connection link from the dashboard first.");
        CodeInput.Password = System.Windows.Clipboard.GetText().Trim();
        await PairAsync();
    });
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && JoinPanel.Visibility == Visibility.Visible && CodeInput.IsKeyboardFocusWithin) { e.Handled = true; await ExecuteAsync(PairAsync); }
    }
    private async Task PairAsync()
    {
        var connection = ConnectionDetails.Parse(CodeInput.Password, EndpointInput.Text);
        var next = new PartyApi(connection.Endpoint); Status("Pairing with your account…");
        try { var auth = await next.PairAsync(connection.Code); api?.Dispose(); api = next; EndpointInput.Text = connection.Endpoint; SessionStore.Save(new SavedSession(connection.Endpoint, auth.Token)); CodeInput.Clear(); await ConnectAsync(auth); }
        catch { if (api != next) next.Dispose(); throw; }
    }

    // ----- Characters -----
    private void LoadCharacter(Character c)
    {
        selectedCharacterId = c.Id; addingCharacter = false;
        CharacterInput.Text = c.CharacterName; ServerInput.Text = c.Server;
        foreach (ComboBoxItem item in RegionInput.Items) if ((string)item.Tag == c.Region) RegionInput.SelectedItem = item;
        CharacterInput.IsReadOnly = true; ServerInput.IsReadOnly = true; RegionInput.IsEnabled = false;
        SaveCharacterButton.Visibility = Visibility.Collapsed; CancelCharacterButton.Visibility = Visibility.Collapsed;
        CharacterDetails.Header = "Character details"; CharacterDetails.IsExpanded = false; ReportButton.IsEnabled = true; NoCharactersLabel.Visibility = Visibility.Collapsed;
        ReportButton.Content = capture is null ? "Start packet reader" : "Stop packet reader"; IdentityPanel.IsEnabled = capture is null;
        if (capture is null) SetReaderState(ReaderState.Stopped, "Stopped · start the reader before logging in.");
        RenderHero();
    }
    private void BeginCharacterDraft(bool manual = true)
    {
        addingCharacter = true; selectedCharacterId = "";
        loading = true; CharactersInput.SelectedItem = null; loading = false;
        CharacterInput.Clear(); CharacterInput.IsReadOnly = false; ServerInput.IsReadOnly = false; RegionInput.IsEnabled = true;
        SaveCharacterButton.Visibility = Visibility.Visible; CancelCharacterButton.Visibility = CharactersInput.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCharactersLabel.Text = CharactersInput.Items.Count == 0 ? "No characters yet" : "Choose a saved character"; NoCharactersLabel.Visibility = Visibility.Visible;
        CharacterDetails.Header = manual ? "Add a character" : "Manual character setup"; CharacterDetails.IsExpanded = manual; ReportButton.IsEnabled = !manual;
        ReportButton.Content = manual ? "Start packet reader" : "Detect character from game";
        if (capture is null) SetReaderState(ReaderState.Stopped, manual ? "Save your character, then start the reader." : "Start the reader, then log into your game character.");
        RenderHero();
        if (manual) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { CharacterInput.BringIntoView(); CharacterInput.Focus(); });
    }
    private async void SaveCharacter_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null || !addingCharacter) return;
        if (RegionInput.SelectedItem is not ComboBoxItem region) throw new InvalidOperationException("Select a region.");
        Status("Saving character…");
        var saved = await api.SaveCharacterAsync(CharacterInput.Text, ServerInput.Text, (string)region.Tag);
        LoadCharacter(saved); await ReloadAsync();
        Status("Character saved. Start the reader to read its Odyle from the game.");
        if (capture is not null && latest is not null) await PublishCoreAsync(refresh: true);
    });
    private async void CancelCharacter_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() =>
    {
        Character? previous = null;
        foreach (Character character in CharactersInput.Items) if (character.Id == previousCharacterId) previous = character;
        previous ??= CharactersInput.Items.Count > 0 ? (Character)CharactersInput.Items[0] : null;
        if (previous is not null) { loading = true; CharactersInput.SelectedItem = previous; loading = false; LoadCharacter(previous); }
        return Task.CompletedTask;
    });
    private async void Character_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || api is null || CharactersInput.SelectedItem is not Character c) return;
        await ExecuteAsync(async () => { await StopReaderAsync(); LoadCharacter(c); });
    }
    private void UpdateCharacterInList(Character saved)
    {
        if (CharactersInput.ItemsSource is not Character[] items) return;
        var index = Array.FindIndex(items, x => x.Id == saved.Id); if (index < 0) return;
        items[index] = saved; loading = true;
        try { CharactersInput.ItemsSource = null; CharactersInput.ItemsSource = items; CharactersInput.SelectedItem = saved; }
        finally { loading = false; }
    }
    private async void Switch_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        await StopReaderAsync();
        previousCharacterId = selectedCharacterId;
        BeginCharacterDraft(); Status("Add the character name and server, then click Save character.");
    });

    // ----- Rooms -----
    private void Room_Changed(object sender, SelectionChangedEventArgs e) { if (!loading) { ApplyRoom(); RenderHero(); } }
    private void ApplyRoom()
    {
        var room = RoomsInput.SelectedItem as Room; ShareToggle.IsChecked = room?.Sharing == true; ShareToggle.IsEnabled = room is not null && room.Role != "viewer";
    }
    private async void JoinRoom_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return; var joined = await api.JoinAsync(InviteInput.Password); InviteInput.Clear(); await ReloadAsync();
        foreach (Room room in RoomsInput.Items) if (room.Id == joined.Room.Id) RoomsInput.SelectedItem = room;
        Status("Joined " + joined.Room.Name + ". Enable sharing if you want your characters visible there.");
    });
    private async void Share_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null || RoomsInput.SelectedItem is not Room room) return;
        try { await api.SetSharingAsync(room.Id, ShareToggle.IsChecked == true); await ReloadAsync(); Status(ShareToggle.IsChecked == true ? "Your characters are now shared in " + room.Name + "." : "Sharing in " + room.Name + " is off. Values stay saved."); }
        catch { ShareToggle.IsChecked = room.Sharing; throw; }
    });
    private void Npcap_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });

    // ----- Packet reader -----
    private async Task InvalidateActiveAsync()
    {
        if (api is null || activeCharacterId.Length == 0) return;
        var id = activeCharacterId; activeCharacterId = ""; publishedTotal = -1;
        try { await api.InvalidateAsync(id, sessionId); } catch { }
    }
    private async Task StopReaderAsync()
    {
        var old = capture; capture = null; detectedCharacterKey = ""; publishDelay.Stop();
        if (old is not null) await old.DisposeAsync();
        await InvalidateActiveAsync();
        latest = null; publishFailure = null;
        ReportButton.Content = selectedCharacterId.Length == 0 && !CharacterDetails.IsExpanded ? "Detect character from game" : "Start packet reader";
        CaptureStatsLabel.Visibility = Visibility.Collapsed; IdentityPanel.IsEnabled = true;
        SetReaderState(ReaderState.Stopped, old is null ? "Stopped · start the reader before logging in." : "Stopped · the last value stays saved on your account.");
        RenderHero();
    }
    private async void Reader_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (capture is not null) { await StopReaderAsync(); return; }
        if (api is null) return;
        var next = new PacketCapture();
        next.Status += (kind, message) => Dispatcher.BeginInvoke(() =>
        {
            if (capture != next) return;
            SetReaderState(kind switch { CaptureStatus.Listening => ReaderState.Listening, CaptureStatus.Midstream => ReaderState.Midstream, CaptureStatus.Problem => ReaderState.Problem, _ => ReaderState.Waiting }, message);
        });
        next.Candidate += observation => Dispatcher.BeginInvoke(() => OnCandidate(next, observation));
        next.CharacterChanged += () => Dispatcher.BeginInvoke(async () =>
        {
            if (capture != next) return;
            await operations.WaitAsync();
            try { if (capture != next) return; detectedCharacterKey = ""; latest = null; await InvalidateActiveAsync(); SetReaderState(ReaderState.Listening, "Character changed · detecting the new character."); RenderHero(); }
            finally { operations.Release(); }
        });
        next.IdentityObserved += identity => Dispatcher.BeginInvoke(async () => await SaveDetectedCharacterAsync(next, identity));
        try
        {
            next.Start(); capture = next; lastGameBytes = 0; latest = null; publishedTotal = -1; publishFailure = null; publishBlockedUntil = DateTimeOffset.MinValue;
            ReportButton.Content = "Stop packet reader"; IdentityPanel.IsEnabled = false; CaptureStatsLabel.Visibility = Visibility.Visible;
            SetReaderState(ReaderState.Waiting, "Waiting for the AION2 game connection…");
            Status("Reader running · log into your character to read its Odyle.");
        }
        catch { await next.DisposeAsync(); throw; }
    });
    private void OnCandidate(PacketCapture source, PacketCandidate observation)
    {
        if (capture != source) return;
        // The same frame can arrive on two capture interfaces within milliseconds.
        var duplicate = latest is not null && latest.Total == observation.Total && DateTimeOffset.UtcNow - latestAt < TimeSpan.FromSeconds(2);
        latest = observation; latestAt = DateTimeOffset.UtcNow;
        if (readerState is ReaderState.Waiting or ReaderState.Listening or ReaderState.Midstream) SetReaderState(ReaderState.Listening, $"Read {observation.Total:N0} Odyle from the game · saving…");
        RenderHero();
        if (duplicate && activeCharacterId.Length > 0) return;
        // With several saved characters, give the identity frame a moment so the
        // reading is attached to the character the game actually reports.
        if (detectedCharacterKey.Length > 0 || CharactersInput.Items.Count <= 1) _ = PublishLatestAsync();
        else { publishDelay.Stop(); publishDelay.Start(); }
    }
    private async Task PublishLatestAsync()
    {
        if (busy || !await operations.WaitAsync(0)) { publishDelay.Stop(); publishDelay.Start(); return; }
        try { await PublishCoreAsync(refresh: false); } finally { operations.Release(); }
    }
    // Caller holds the operations lock.
    private async Task PublishCoreAsync(bool refresh)
    {
        if (api is null || capture is null || latest is not { } reading) return;
        if (addingCharacter || CharactersInput.SelectedItem is not Character character)
        {
            SetReaderState(ReaderState.Listening, $"Read {reading.Total:N0} Odyle · save a character to publish it."); RenderHero(); return;
        }
        var now = DateTimeOffset.UtcNow;
        if (now < publishBlockedUntil) return;
        if (!refresh && publishedTotal == reading.Total && activeCharacterId == character.Id && now - lastPublishedAt < TimeSpan.FromSeconds(15)) return;
        try
        {
            var saved = await api.ReportAsync(new PacketReading(sessionId, ++sequence, character.CharacterName, character.Server, character.Region, reading.Total, reading.ParserVersion, character.Id));
            if (activeCharacterId.Length > 0 && activeCharacterId != saved.Id) { var previous = activeCharacterId; try { await api.InvalidateAsync(previous, sessionId); } catch { } }
            activeCharacterId = saved.Id; publishedTotal = reading.Total; lastPublishedAt = DateTimeOffset.UtcNow; lastTick = lastPublishedAt; publishFailure = null;
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
            if (detectedCharacterKey == key) return;
            if (identity.Server is null || identity.Region is null)
            {
                addingCharacter = true; selectedCharacterId = "";
                loading = true; CharactersInput.SelectedItem = null; loading = false;
                NoCharactersLabel.Text = "Complete character setup"; NoCharactersLabel.Visibility = Visibility.Visible;
                CharacterInput.Text = identity.CharacterName;
                ServerInput.Text = identity.Server ?? ""; RegionInput.SelectedItem = null;
                foreach (ComboBoxItem item in RegionInput.Items) if ((string)item.Tag == identity.Region) RegionInput.SelectedItem = item;
                CharacterInput.IsReadOnly = false; ServerInput.IsReadOnly = false; RegionInput.IsEnabled = true;
                SaveCharacterButton.Visibility = Visibility.Visible; CharacterDetails.Header = "Manual character setup"; CharacterDetails.IsExpanded = true;
                Status("Character detected, but its server/region is not mapped. Stop the reader and complete the setup."); RenderHero();
                return;
            }
            var saved = await api.SaveCharacterAsync(identity.CharacterName, identity.Server, identity.Region);
            LoadCharacter(saved); await ReloadAsync(); detectedCharacterKey = key;
            Status($"Detected {identity.CharacterName} · {identity.Server}. Saved to your account.");
            if (latest is not null) await PublishCoreAsync(refresh: true);
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
        if (capture is not null && latest is not null) await PublishCoreAsync(refresh: true);
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
                if (alive) await PublishCoreAsync(refresh: true);
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
        heartbeat.Stop(); api?.Dispose(); api = null; if (clearSaved) SessionStore.Clear();
        activeCharacterId = ""; selectedCharacterId = ""; accountName = ""; addingCharacter = false;
        loading = true; CharactersInput.ItemsSource = null; RoomsInput.ItemsSource = null; loading = false;
        JoinPanel.Visibility = Visibility.Visible; ReaderPanel.Visibility = Visibility.Collapsed; RetryButton.Visibility = clearSaved ? Visibility.Collapsed : Visibility.Visible;
        Status(message); RenderHero();
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => DisconnectAsync("Disconnected · your characters stay saved on your account.", clearSaved: true));
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exit) { e.Cancel = true; Hide(); } }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        heartbeat.Stop(); clock.Stop(); publishDelay.Stop(); await operations.WaitAsync();
        try { await Task.WhenAny(StopReaderAsync(), Task.Delay(4000)); api?.Dispose(); }
        finally { operations.Release(); tray.Dispose(); exit = true; System.Windows.Application.Current.Shutdown(); }
    }
}
