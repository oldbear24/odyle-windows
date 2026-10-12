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
    private bool exit, busy, needsReconnect, loading;
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
        catch (Exception error) { if (error is HttpRequestException or TaskCanceledException && api is not null) { needsReconnect = true; ClearCurrent("Connection interrupted. Saved values are retained."); } Status(error.Message); }
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
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; activeCharacterId = "";
        AccountLabel.Text = auth.Record.DisplayName; JoinPanel.Visibility = Visibility.Collapsed; ReaderPanel.Visibility = Visibility.Visible; RetryButton.Visibility = Visibility.Collapsed;
        await ReloadAsync();
        if (CharactersInput.SelectedItem is Character c) LoadCharacter(c);
        else { CharacterDetails.IsExpanded = true; ClearCurrent("Add your first character below."); }
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
            if (selectedCharacterId.Length == 0 && characters.Length > 0) selectedCharacterId = characters[0].Id;
            CharactersInput.ItemsSource = characters; CharactersInput.SelectedItem = Array.Find(characters, c => c.Id == selectedCharacterId);
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
        activeCharacterId = ""; selectedCharacterId = c.Id;
        CharacterInput.Text = c.CharacterName; ServerInput.Text = c.Server; ValueLabel.Text = c.Confirmed ? c.Value.ToString("N0") : "—";
        foreach (ComboBoxItem item in RegionInput.Items) if ((string)item.Tag == c.Region) RegionInput.SelectedItem = item;
        CharacterDetails.IsExpanded = false; ClearCurrent("Historical value · waiting for verified packets.");
    }
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
        var old = capture; capture = null; if (old is not null) await old.DisposeAsync();
        ReportButton.Content = "Start packet reader"; PacketStateLabel.Text = "Stopped · last saved values stay on your account.";
        IdentityPanel.IsEnabled = true;
    }
    private async void Reader_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (capture is not null) { await StopReaderAsync(); return; }
        if (api is null) return;
        if (string.IsNullOrWhiteSpace(CharacterInput.Text) || string.IsNullOrWhiteSpace(ServerInput.Text)) { CharacterDetails.IsExpanded = true; throw new InvalidOperationException("Select your character or enter its name and server first."); }
        var next = new PacketCapture();
        next.Status += message => Dispatcher.BeginInvoke(() => { if (capture == next) PacketStateLabel.Text = message; });
        next.Candidate += observation => Dispatcher.BeginInvoke(() =>
        {
            if (capture != next) return;
            PacketStateLabel.Text = $"Unverified: {observation.Primary:N0} + {observation.Bonus:N0} = {observation.Total:N0}. Not shared · Global validation required.";
            // Candidate data cannot be reported as a verified reading.
            ValueLabel.Text = "—"; ClearCurrent("Waiting for a verified Global game reading."); IdentityPanel.IsEnabled = false;
        });
        next.CharacterChanged += () => Dispatcher.BeginInvoke(async () =>
        {
            if (capture != next) return; await ExecuteAsync(StopReaderAsync); ClearCurrent("Character changed. Select the correct character before restarting.");
        });
        try { next.Start(); capture = next; ReportButton.Content = "Stop packet reader"; IdentityPanel.IsEnabled = false; Status("Experimental capture · reference packet format is not Global-verified."); }
        catch { await next.DisposeAsync(); throw; }
    });
    private async void Switch_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        await StopReaderAsync();
        if (activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId);
        activeCharacterId = ""; selectedCharacterId = ""; loading = true; CharactersInput.SelectedItem = null; loading = false;
        CharacterInput.Clear(); ValueLabel.Text = "—"; CharacterDetails.IsExpanded = true; ClearCurrent("Add a character · previous values stay saved."); CharacterInput.Focus();
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
