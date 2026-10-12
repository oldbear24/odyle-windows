using System;
using System.ComponentModel;
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
    private string sessionId = "";
    private long sequence;
    private DateTimeOffset? confirmedAt;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow;
    private bool exit, busy, needsReconnect;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer freshness = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon tray;
    public MainWindow()
    {
        InitializeComponent();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Odyle · No reading", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Odyle", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Quit", null, async (_, _) => await Dispatcher.InvokeAsync(QuitAsync).Task.Unwrap());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        heartbeat.Tick += async (_, _) => await PulseAsync();
        freshness.Tick += (_, _) => UpdateFreshness(); freshness.Start();
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Status(string message) { StatusLabel.Text = message; }
    private void ClearReading(string message)
    {
        confirmedAt = null; ValueLabel.Text = "—"; ReadingLabel.Text = message; ConfirmToggle.IsChecked = false;
        IdentityPanel.IsEnabled = true; tray.Text = "Odyle · No confirmed reading";
    }
    private void UpdateFreshness()
    {
        if (confirmedAt is not { } stamp) return;
        var age = (int)(DateTimeOffset.UtcNow - stamp).TotalSeconds;
        ReadingLabel.Text = age >= 45 ? $"Stale · confirmed {age}s ago. Check and confirm again." : $"Manual confirmed · {age}s ago";
        if (age >= 45) tray.Text = "Odyle · Stale reading";
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; JoinButton.IsEnabled = false; ReaderPanel.IsEnabled = false;
        await operations.WaitAsync();
        try { await action(); }
        catch (Exception error)
        {
            if (error is HttpRequestException or TaskCanceledException && api is not null) { needsReconnect = true; ClearReading("Connection interrupted. Reconfirm after reconnect."); }
            Status(error.Message);
        }
        finally { busy = false; JoinButton.IsEnabled = true; ReaderPanel.IsEnabled = api is not null && JoinPanel.Visibility == Visibility.Collapsed; operations.Release(); }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var saved = SessionStore.Load(); if (saved is null) return;
        EndpointInput.Text = saved.Endpoint;
        api = new PartyApi(saved.Endpoint, saved.Token);
        try
        {
            var auth = await api.RefreshAsync();
            if (auth.Record.Role == "viewer") throw new InvalidOperationException("A publisher credential is required.");
            SessionStore.Save(new SavedSession(saved.Endpoint, auth.Token));
            await ConnectAsync(auth);
        }
        catch { api.Dispose(); api = null; SessionStore.Clear(); Status("Your room session expired or is unreachable. Join with a fresh invite."); }
    });
    private async Task ConnectAsync(AuthResponse auth)
    {
        if (api is null) return;
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; sequence = 0;
        await api.SetSharingAsync(false);
        NameInput.Text = auth.Record.DisplayName; CharacterInput.Text = auth.Record.CharacterName.Length > 0 ? auth.Record.CharacterName : auth.Record.DisplayName;
        ServerInput.Text = auth.Record.Server;
        foreach (ComboBoxItem item in RegionInput.Items) if ((string)item.Tag == auth.Record.Region) RegionInput.SelectedItem = item;
        JoinPanel.Visibility = Visibility.Collapsed; ReaderPanel.IsEnabled = true; ShareToggle.IsChecked = false;
        RoomLabel.Text = auth.Room?.Name ?? "Connected to your private party";
        ClearReading("Choose your character and confirm a new reading."); Status("Connected. Sharing is off until you opt in.");
        lastTick = DateTimeOffset.UtcNow; needsReconnect = false; heartbeat.Start();
    }
    private async void Join_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var next = new PartyApi(EndpointInput.Text);
        try
        {
            var auth = await next.JoinAsync(CodeInput.Password, NameInput.Text.Trim());
            api?.Dispose(); api = next;
            SessionStore.Save(new SavedSession(EndpointInput.Text, auth.Token)); CodeInput.Clear();
            await ConnectAsync(auth);
        }
        catch { if (api != next) next.Dispose(); throw; }
    });
    private async void Share_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        var enabled = ShareToggle.IsChecked == true;
        ClearReading(enabled ? "Sharing enabled. Confirm a current reading." : "Sharing stopped. Your party reading is cleared.");
        try { await api.SetSharingAsync(enabled); Status(enabled ? "Your next confirmed value will be shared." : "Sharing disabled."); }
        catch { ShareToggle.IsChecked = false; throw; }
    });
    private async void Report_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        if (needsReconnect) { await ReconnectAsync(); throw new InvalidOperationException("Reconnected. Check your current in-game value and confirm again."); }
        if (ShareToggle.IsChecked != true || ConfirmToggle.IsChecked != true) throw new InvalidOperationException("Enable sharing and confirm that you checked this total in-game.");
        if (!int.TryParse(OdyleInput.Text, out var value) || value < 0) throw new InvalidOperationException("Enter a whole Odyle value. Zero is valid.");
        await api.ReportAsync(new ManualReading(sessionId, ++sequence, CharacterInput.Text.Trim(), ServerInput.Text.Trim(), (string)((ComboBoxItem)RegionInput.SelectedItem).Tag, value));
        confirmedAt = DateTimeOffset.UtcNow; ValueLabel.Text = value.ToString("N0"); IdentityPanel.IsEnabled = false; ConfirmToggle.IsChecked = false;
        tray.Text = $"Odyle · {value} confirmed"; UpdateFreshness(); Status("Your confirmed reading is visible to your party.");
    });
    private async void Switch_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        ClearReading("Previous character reading cleared."); OdyleInput.Clear();
        try { await api.InvalidateAsync(); Status("Select the new character before confirming a new value."); }
        catch { needsReconnect = true; throw; }
    });
    private async Task ReconnectAsync()
    {
        if (api is null) return;
        var session = await api.StartSessionAsync(); sessionId = session.SessionId; sequence = 0;
        // Resend consent only; never resend an old number after reconnect or resume.
        await api.SetSharingAsync(ShareToggle.IsChecked == true); needsReconnect = false;
        ClearReading("Reconnected. A new confirmation is required.");
    }
    private async Task PulseAsync()
    {
        if (api is null || !await operations.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (needsReconnect || now - lastTick > TimeSpan.FromSeconds(45)) await ReconnectAsync();
            await api.HeartbeatAsync(); lastTick = now;
        }
        catch (Exception error) { needsReconnect = true; ClearReading("Connection interrupted. Reconfirm after reconnect."); Status(error.Message); }
        finally { operations.Release(); }
    }
    private async void Leave_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (api is null) return;
        await api.LeaveAsync(); heartbeat.Stop(); api.Dispose(); api = null; SessionStore.Clear();
        ReaderPanel.IsEnabled = false; JoinPanel.Visibility = Visibility.Visible; ShareToggle.IsChecked = false; RoomLabel.Text = "Not connected to a party"; ClearReading("No confirmed Odyle reading"); Status("Left the room. Your credential has been revoked.");
    });
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exit) { e.Cancel = true; Hide(); } }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        heartbeat.Stop(); freshness.Stop(); await operations.WaitAsync();
        try { if (api is not null) { try { await api.SetSharingAsync(false); } catch { } api.Dispose(); } }
        finally { operations.Release(); tray.Dispose(); exit = true; System.Windows.Application.Current.Shutdown(); }
    }
}
