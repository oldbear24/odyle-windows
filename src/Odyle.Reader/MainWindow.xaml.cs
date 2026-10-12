using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Odyle.Core;
using Forms = System.Windows.Forms;

namespace Odyle.Reader;
public partial class MainWindow : Window
{
    private PartyApi? api;
    private string sessionId = "", activeCharacterId = "", selectedCharacterId = "";
    private long sequence;
    private DateTimeOffset? confirmedAt;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow;
    private bool exit, busy, needsReconnect, loading;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer freshness = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon tray;
    public MainWindow()
    {
        InitializeComponent();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Odyle · No reading", Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Odyle", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Quit", null, async (_, _) => await Dispatcher.InvokeAsync(QuitAsync).Task.Unwrap()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        heartbeat.Tick += async (_, _) => await PulseAsync(); freshness.Tick += (_, _) => UpdateFreshness(); freshness.Start();
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Status(string message) => StatusLabel.Text = message;
    private void ClearCurrent(string message)
    {
        confirmedAt = null; ReadingLabel.Text = message; ConfirmToggle.IsChecked = false; IdentityPanel.IsEnabled = true;
        tray.Text = "Odyle · Saved values need confirmation";
    }
    private void UpdateFreshness()
    {
        if (confirmedAt is not { } stamp) return;
        var age = (int)(DateTimeOffset.UtcNow - stamp).TotalSeconds;
        ReadingLabel.Text = age >= 45 ? $"Saved · stale ({age}s). Check and confirm again." : $"Manual confirmed · {age}s ago";
        if (age >= 45) tray.Text = "Odyle · Stale saved reading";
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; busy = true; JoinButton.IsEnabled = false; ReaderPanel.IsEnabled = false; await operations.WaitAsync();
        try { await action(); }
        catch (Exception error) { if (error is HttpRequestException or TaskCanceledException && api is not null) { needsReconnect = true; ClearCurrent("Connection interrupted. Saved values are retained."); } Status(error.Message); }
        finally { busy = false; JoinButton.IsEnabled = true; ReaderPanel.IsEnabled = api is not null && JoinPanel.Visibility == Visibility.Collapsed; operations.Release(); }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var saved = SessionStore.Load(); if (saved is null) return; EndpointInput.Text = saved.Endpoint; api = new PartyApi(saved.Endpoint, saved.Token);
        try { var auth = await api.RefreshAsync(); SessionStore.Save(new SavedSession(saved.Endpoint, auth.Token)); await ConnectAsync(auth); }
        catch { api.Dispose(); api = null; SessionStore.Clear(); Status("Pair this app again from your Discord account dashboard."); }
    });
    private async Task ConnectAsync(AuthResponse auth)
    {
        if (api is null) return;
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; sequence = 0; activeCharacterId = "";
        AccountLabel.Text = auth.Record.DisplayName + " · Discord account"; JoinPanel.Visibility = Visibility.Collapsed;
        await ReloadAsync(); ClearCurrent("Select a saved character or add another one."); Status("Account connected. Room sharing is controlled separately for each room.");
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
            CharactersInput.ItemsSource = characters; CharactersInput.SelectedItem = Array.Find(characters, c => c.Id == selectedCharacterId);
            RoomsInput.ItemsSource = rooms; RoomsInput.SelectedItem = Array.Find(rooms, r => r.Id == selectedRoomId) ?? (rooms.Length > 0 ? rooms[0] : null);
        }
        finally { loading = false; }
        ApplyRoom();
    }
    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        try { using var validate = new PartyApi(EndpointInput.Text); Process.Start(new ProcessStartInfo(EndpointInput.Text.Trim()) { UseShellExecute = true }); }
        catch (Exception error) { Status(error.Message); }
    }
    private async void Pair_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var next = new PartyApi(EndpointInput.Text);
        try { var auth = await next.PairAsync(CodeInput.Password); api?.Dispose(); api = next; SessionStore.Save(new SavedSession(EndpointInput.Text, auth.Token)); CodeInput.Clear(); await ConnectAsync(auth); }
        catch { if (api != next) next.Dispose(); throw; }
    });
    private async void Character_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || api is null || CharactersInput.SelectedItem is not Character c) return;
        await ExecuteAsync(async () =>
        {
            if (activeCharacterId.Length > 0 && activeCharacterId != c.Id) await api.InvalidateAsync(activeCharacterId, sessionId);
            activeCharacterId = ""; selectedCharacterId = c.Id;
            CharacterInput.Text = c.CharacterName; ServerInput.Text = c.Server; OdyleInput.Text = c.Confirmed ? c.Value.ToString() : ""; ValueLabel.Text = c.Confirmed ? c.Value.ToString("N0") : "—";
            foreach (ComboBoxItem item in RegionInput.Items) if ((string)item.Tag == c.Region) RegionInput.SelectedItem = item;
            ClearCurrent("Last saved value. Check in-game before confirming again.");
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
    private async void Report_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        if (needsReconnect) { await ReconnectAsync(); throw new InvalidOperationException("Reconnected. Check your in-game value and confirm again."); }
        if (ConfirmToggle.IsChecked != true) throw new InvalidOperationException("Confirm that you checked this character’s total in-game.");
        if (!int.TryParse(OdyleInput.Text, out var value) || value < 0) throw new InvalidOperationException("Enter a whole Odyle value. Zero is valid.");
        var c = await api.ReportAsync(new ManualReading(sessionId, ++sequence, CharacterInput.Text.Trim(), ServerInput.Text.Trim(), (string)((ComboBoxItem)RegionInput.SelectedItem).Tag, value, selectedCharacterId.Length > 0 ? selectedCharacterId : null));
        selectedCharacterId = c.Id; activeCharacterId = c.Id; confirmedAt = DateTimeOffset.UtcNow; ValueLabel.Text = value.ToString("N0"); IdentityPanel.IsEnabled = false; ConfirmToggle.IsChecked = false;
        tray.Text = $"Odyle · {c.CharacterName}: {value}"; await ReloadAsync(); UpdateFreshness(); Status("Saved for this character and visible in rooms where you enabled sharing.");
    });
    private async void Switch_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        if (activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId);
        activeCharacterId = ""; selectedCharacterId = ""; loading = true; CharactersInput.SelectedItem = null; loading = false;
        CharacterInput.Clear(); ServerInput.Clear(); OdyleInput.Clear(); ValueLabel.Text = "—"; ClearCurrent("Previous character value stays saved. Enter the next character.");
    });
    private async Task ReconnectAsync()
    {
        if (api is null) return;
        PublisherSession session;
        try { session = await api.StartSessionAsync(sessionId); } catch (HttpRequestException) { session = await api.StartSessionAsync(); }
        sessionId = session.SessionId; sequence = 0; needsReconnect = false; ClearCurrent("Reconnected. Last values are retained; reconfirm the active character.");
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
        if (api is not null && activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId);
        heartbeat.Stop(); api?.Dispose(); api = null; SessionStore.Clear(); activeCharacterId = ""; selectedCharacterId = "";
        JoinPanel.Visibility = Visibility.Visible; AccountLabel.Text = "Pair your Discord account"; ValueLabel.Text = "—"; ClearCurrent("Signed out. Your characters and rooms remain saved.");
    });
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exit) { e.Cancel = true; Hide(); } }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        heartbeat.Stop(); freshness.Stop(); await operations.WaitAsync();
        try { if (api is not null) { try { if (activeCharacterId.Length > 0) await api.InvalidateAsync(activeCharacterId, sessionId); } catch { } api.Dispose(); } }
        finally { operations.Release(); tray.Dispose(); exit = true; System.Windows.Application.Current.Shutdown(); }
    }
}
