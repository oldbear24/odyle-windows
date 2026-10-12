using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Odyle.Core;
using Forms = System.Windows.Forms;

namespace Odyle.Reader;
public partial class MainWindow : Window
{
    private PartyApi? api;
    private PacketCapture? capture;
    private string sessionId = "", activeCharacterId = "", selectedCharacterId = "";
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow;
    private bool exit, busy, needsReconnect, loading, addingCharacter;
    private string previousCharacterId = "";
    private string detectedCharacterKey = "";
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly Forms.NotifyIcon tray;
    public MainWindow() : this(true) { }
    public MainWindow(bool enableTray)
    {
        InitializeComponent();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Odyle · No reading", Visible = enableTray };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Odyle", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Quit", null, async (_, _) => await Dispatcher.InvokeAsync(QuitAsync).Task.Unwrap()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        heartbeat.Tick += async (_, _) => await PulseAsync();
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Status(string message) => StatusLabel.Text = message;
    private void ClearCurrent(string message)
    {
        ReadingLabel.Text = message; IdentityPanel.IsEnabled = capture is null;
        tray.Text = "Odyle · Waiting for verified packets";
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; busy = true; JoinButton.IsEnabled = false; PasteButton.IsEnabled = false; RetryButton.IsEnabled = false; ReaderPanel.IsEnabled = false; await operations.WaitAsync();
        try { await action(); }
        catch (Exception error) { if (api is not null && (error is TaskCanceledException || error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500 || http.StatusCode == System.Net.HttpStatusCode.Unauthorized))) { needsReconnect = true; ClearCurrent("Connection interrupted. Saved values are retained."); } Status(error.Message); }
        finally { busy = false; JoinButton.IsEnabled = true; PasteButton.IsEnabled = true; RetryButton.IsEnabled = true; ReaderPanel.IsEnabled = api is not null && JoinPanel.Visibility == Visibility.Collapsed; operations.Release(); }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ExecuteAsync(RestoreAsync);
    private async void Retry_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(RestoreAsync);
    private async Task RestoreAsync()
    {
        var saved = SessionStore.Load(); if (saved is null) return; EndpointInput.Text = saved.Endpoint; api = new PartyApi(saved.Endpoint, saved.Token);
        try { var auth = await api.RefreshAsync(); SessionStore.Save(new SavedSession(saved.Endpoint, auth.Token)); await ConnectAsync(auth); }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
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
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; activeCharacterId = ""; addingCharacter = false;
        AccountLabel.Text = auth.Record.DisplayName; JoinPanel.Visibility = Visibility.Collapsed; ReaderPanel.Visibility = Visibility.Visible; RetryButton.Visibility = Visibility.Collapsed;
        await ReloadAsync();
        if (CharactersInput.SelectedItem is Character c) LoadCharacter(c);
        else BeginCharacterDraft(manual: false);
        Status("Connected · values stay saved to your account.");
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
        ApplyRoom();
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
        if (e.Key == Key.Enter && JoinPanel.Visibility == Visibility.Visible) { e.Handled = true; await ExecuteAsync(PairAsync); }
    }
    private async Task PairAsync()
    {
        var connection = ConnectionDetails.Parse(CodeInput.Password, EndpointInput.Text);
        var next = new PartyApi(connection.Endpoint);
        try { var auth = await next.PairAsync(connection.Code); api?.Dispose(); api = next; EndpointInput.Text = connection.Endpoint; SessionStore.Save(new SavedSession(connection.Endpoint, auth.Token)); CodeInput.Clear(); await ConnectAsync(auth); }
        catch { if (api != next) next.Dispose(); throw; }
    }
    private void LoadCharacter(Character c)
    {
        activeCharacterId = ""; selectedCharacterId = c.Id; addingCharacter = false;
        CharacterInput.Text = c.CharacterName; ServerInput.Text = c.Server; ValueLabel.Text = c.Confirmed ? c.Value.ToString("N0") : "—";
        foreach (ComboBoxItem item in RegionInput.Items) if ((string)item.Tag == c.Region) RegionInput.SelectedItem = item;
        CharacterInput.IsReadOnly = true; ServerInput.IsReadOnly = true; RegionInput.IsEnabled = false;
        SaveCharacterButton.Visibility = Visibility.Collapsed; CancelCharacterButton.Visibility = Visibility.Collapsed;
        CharacterDetails.Header = "Character details"; CharacterDetails.IsExpanded = false; ReportButton.IsEnabled = true; NoCharactersLabel.Visibility = Visibility.Collapsed;
        ReportButton.Content = capture is null ? "Start packet reader" : "Stop packet reader";
        if (capture is null) PacketStateLabel.Text = "Stopped · start the reader before logging into the game.";
        ClearCurrent(c.Confirmed ? "Historical value · waiting for verified packets." : "Character saved · waiting for a game reading.");
    }
    private void BeginCharacterDraft(bool manual = true)
    {
        addingCharacter = true; selectedCharacterId = "";
        loading = true; CharactersInput.SelectedItem = null; loading = false;
        CharacterInput.Clear(); CharacterInput.IsReadOnly = false; ServerInput.IsReadOnly = false; RegionInput.IsEnabled = true;
        SaveCharacterButton.Visibility = Visibility.Visible; CancelCharacterButton.Visibility = CharactersInput.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCharactersLabel.Text = CharactersInput.Items.Count == 0 ? "No characters yet" : "Choose a saved character"; NoCharactersLabel.Visibility = Visibility.Visible;
        CharacterDetails.Header = manual ? "Add a character" : "Manual character setup"; CharacterDetails.IsExpanded = manual; ReportButton.IsEnabled = !manual; ValueLabel.Text = "—";
        ReportButton.Content = manual ? "Start packet reader" : "Detect character from game";
        PacketStateLabel.Text = manual ? "Save your character, then start the reader." : "Start the reader, then log into your game character.";
        ClearCurrent(manual ? "Enter the name, server and region below." : "Your character and server can be detected from the game.");
        if (manual) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { CharacterInput.BringIntoView(); CharacterInput.Focus(); });
    }
    private async void SaveCharacter_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null || !addingCharacter) return;
        if (RegionInput.SelectedItem is not ComboBoxItem region) throw new InvalidOperationException("Select a region.");
        Status("Saving character…");
        var saved = await api.SaveCharacterAsync(CharacterInput.Text, ServerInput.Text, (string)region.Tag);
        LoadCharacter(saved); await ReloadAsync();
        PacketStateLabel.Text = "Stopped · start the reader before logging into the game.";
        Status("Character saved. Odyle will come from the game; no value was entered.");
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
        await ExecuteAsync(async () =>
        {
            await StopReaderAsync();
            if (activeCharacterId.Length > 0 && activeCharacterId != c.Id) await api.InvalidateAsync(activeCharacterId, sessionId);
            LoadCharacter(c);
        });
    }
    private void Room_Changed(object sender, SelectionChangedEventArgs e) { if (!loading) ApplyRoom(); }
    private void ApplyRoom()
    {
        var room = RoomsInput.SelectedItem as Room; ShareToggle.IsChecked = room?.Sharing == true; ShareToggle.IsEnabled = room is not null && room.Role != "viewer";
    }
    private async void JoinRoom_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return; var joined = await api.JoinAsync(InviteInput.Password); InviteInput.Clear(); await ReloadAsync();
        foreach (Room room in RoomsInput.Items) if (room.Id == joined.Room.Id) RoomsInput.SelectedItem = room;
        Status("Joined " + joined.Room.Name + ". Enable sharing if you want your saved characters visible here.");
    });
    private async void Share_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null || RoomsInput.SelectedItem is not Room room) return;
        try { await api.SetSharingAsync(room.Id, ShareToggle.IsChecked == true); await ReloadAsync(); Status("Room sharing updated. Saved character values are retained."); }
        catch { ShareToggle.IsChecked = room.Sharing; throw; }
    });
    private void Npcap_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });
    private async Task StopReaderAsync()
    {
        var old = capture; capture = null; detectedCharacterKey = ""; if (old is not null) await old.DisposeAsync();
        ReportButton.Content = "Start packet reader"; PacketStateLabel.Text = "Stopped · last saved values stay on your account.";
        IdentityPanel.IsEnabled = true;
        if (selectedCharacterId.Length == 0 && !CharacterDetails.IsExpanded) ReportButton.Content = "Detect character from game";
    }
    private async void Reader_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (capture is not null) { await StopReaderAsync(); return; }
        if (api is null) return;
        var next = new PacketCapture();
        next.Status += message => Dispatcher.BeginInvoke(() => { if (capture == next) PacketStateLabel.Text = message; });
        next.Candidate += observation => Dispatcher.BeginInvoke(() =>
        {
            if (capture != next) return;
            PacketStateLabel.Text = $"Unverified: {observation.Primary:N0} + {observation.Bonus:N0} = {observation.Total:N0}. Not shared · Global validation required.";
            // Candidate data cannot be reported as a verified reading.
            ValueLabel.Text = "—"; ClearCurrent("Waiting for a verified Global game reading."); IdentityPanel.IsEnabled = false;
        });
        next.CharacterChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (capture != next) return; detectedCharacterKey = ""; ValueLabel.Text = "—"; ClearCurrent("Character changed · detecting the new character.");
        });
        next.IdentityObserved += identity => Dispatcher.BeginInvoke(async () => await SaveDetectedCharacterAsync(next, identity));
        try { next.Start(); capture = next; ReportButton.Content = "Stop packet reader"; IdentityPanel.IsEnabled = false; Status("Experimental capture · reference packet format is not Global-verified."); }
        catch { await next.DisposeAsync(); throw; }
    });
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
                ValueLabel.Text = "—"; ClearCurrent("Character detected · server setup is incomplete.");
                Status("Character detected, but this server/region is not mapped. Stop the reader and complete manual setup.");
                return;
            }
            var saved = await api.SaveCharacterAsync(identity.CharacterName, identity.Server, identity.Region);
            LoadCharacter(saved); await ReloadAsync(); detectedCharacterKey = key;
            Status($"Detected {identity.CharacterName} · {identity.Server}. Character saved to your account.");
        }
        catch (Exception error) { Status("Could not save the detected character: " + error.Message); }
        finally { operations.Release(); }
    }
    private async void Switch_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        await StopReaderAsync();
        if (activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId);
        previousCharacterId = selectedCharacterId; activeCharacterId = "";
        BeginCharacterDraft(); Status("Add the character name and server, then click Save character.");
    });
    private async Task ReconnectAsync()
    {
        if (api is null) return;
        PublisherSession session;
        try { session = await api.StartSessionAsync(sessionId); } catch (HttpRequestException) { session = await api.StartSessionAsync(); }
        await StopReaderAsync(); sessionId = session.SessionId; needsReconnect = false; ClearCurrent("Reconnected. Restart the reader for a fresh game snapshot.");
    }
    private async Task PulseAsync()
    {
        if (api is null || !await operations.WaitAsync(0)) return;
        try { var now = DateTimeOffset.UtcNow; if (needsReconnect || now - lastTick > TimeSpan.FromSeconds(45)) await ReconnectAsync(); await api.HeartbeatAsync(sessionId, activeCharacterId.Length > 0 ? activeCharacterId : null); lastTick = now; }
        catch (Exception error) { needsReconnect = true; ClearCurrent("Connection interrupted. Character history is retained."); Status(error.Message); }
        finally { operations.Release(); }
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        await StopReaderAsync();
        if (api is not null && activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId);
        heartbeat.Stop(); api?.Dispose(); api = null; SessionStore.Clear(); activeCharacterId = ""; selectedCharacterId = "";
        JoinPanel.Visibility = Visibility.Visible; ReaderPanel.Visibility = Visibility.Collapsed; AccountLabel.Text = "Connect your Discord account"; ValueLabel.Text = "—"; ClearCurrent("Disconnected · your characters stay saved.");
    });
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exit) { e.Cancel = true; Hide(); } }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        heartbeat.Stop(); await operations.WaitAsync();
        try { await StopReaderAsync(); if (api is not null) { try { if (activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId); } catch { } api.Dispose(); } }
        finally { operations.Release(); tray.Dispose(); exit = true; System.Windows.Application.Current.Shutdown(); }
    }
}
