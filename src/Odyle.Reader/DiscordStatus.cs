using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Odyle.Core;

namespace Odyle.Reader;
// The Discord status (Settings → Window): Odyle talks to the Discord app on this PC over its local
// pipe, nothing goes over the network. Show sets what Discord should show, null clears it. A change
// is sent at most every 15 seconds (Discord allows five in 20), a Discord that is not running is
// looked for every 30 seconds, and clearing closes the pipe, which ends the status at once.
public sealed class DiscordStatus : IDisposable
{
    public enum State { Off, NotRunning, Shown, Refused }
    private static readonly TimeSpan SendEvery = TimeSpan.FromSeconds(15), LookEvery = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim busy = new(1, 1), writing = new(1, 1);
    private NamedPipeClientStream? pipe;
    // refusedBy: the application Discord turned down (an unknown one); it is not tried again.
    private string application = "", connectedTo = "", refusedBy = "";
    private DiscordActivity? wanted, sent;
    private DateTimeOffset sentAt = DateTimeOffset.MinValue, lookedAt = DateTimeOffset.MinValue;
    public State Now { get; private set; } = State.Off;
    public string Problem { get; private set; } = "";
    public event Action? Changed;

    public void Show(string application, DiscordActivity? activity)
    {
        this.application = application; wanted = activity;
        if (activity is null && pipe is null) { Set(State.Off, ""); return; }
        if (application == refusedBy) return;
        if (activity == sent && application == connectedTo && pipe is not null) return;
        var now = DateTimeOffset.UtcNow;
        if (activity is not null && pipe is not null && now - sentAt < SendEvery) return;
        if (activity is not null && pipe is null && now - lookedAt < LookEvery) return;
        _ = PushAsync();
    }
    private async Task PushAsync()
    {
        if (!await busy.WaitAsync(0)) return;
        try
        {
            if (pipe is not null && (wanted is null || application != connectedTo)) Disconnect();
            if (wanted is null) { Set(State.Off, ""); return; }
            if (pipe is null && !await ConnectAsync()) return;
            var activity = wanted;
            await WriteAsync(DiscordPresence.Message, DiscordPresence.SetActivityJson(Environment.ProcessId, activity, Guid.NewGuid().ToString()));
            sent = activity; sentAt = DateTimeOffset.UtcNow; Set(State.Shown, "");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException or TimeoutException)
        {
            Log.Warn("The Discord status could not be set: " + error.Message); Disconnect(); Set(State.NotRunning, "");
        }
        finally { busy.Release(); }
    }
    private async Task<bool> ConnectAsync()
    {
        lookedAt = DateTimeOffset.UtcNow;
        // The pipes are listed rather than tried, so a Discord that is not running costs nothing.
        string[] names;
        try { names = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).Where(n => n is not null && n.StartsWith("discord-ipc-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray()!; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { names = []; }
        foreach (var name in names)
        {
            // CurrentUserOnly: only a pipe of this Windows account's own Discord is talked to.
            var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await next.ConnectAsync(wait.Token);
                pipe = next;
                await WriteAsync(DiscordPresence.Handshake, DiscordPresence.HandshakeJson(application));
                using var answer = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var frame = await DiscordPresence.ReadFrameAsync(next, answer.Token);
                var (ready, refused) = frame is { } f ? DiscordPresence.Answer(f.Opcode, f.Json) : (false, "closed");
                if (!ready)
                {
                    Disconnect(); Log.Warn($"Discord turned the status down: {refused}.");
                    if (frame is { Opcode: DiscordPresence.Close }) refusedBy = application;
                    Set(State.Refused, refused ?? ""); return false;
                }
                connectedTo = application; Log.Info("Connected to Discord for the status.");
                _ = Task.Run(() => ListenAsync(next));
                return true;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException or InvalidDataException)
            {
                if (pipe == next) pipe = null;
                next.Dispose();
            }
        }
        Set(State.NotRunning, ""); return false;
    }
    // Discord pings now and then and closes the pipe when it quits; whatever it answers is read so
    // the pipe never fills up.
    private async Task ListenAsync(NamedPipeClientStream from)
    {
        try
        {
            while (await DiscordPresence.ReadFrameAsync(from, CancellationToken.None) is { } frame)
            {
                if (frame.Opcode == DiscordPresence.Ping) await WriteAsync(DiscordPresence.Pong, frame.Json, from);
                else if (frame.Opcode == DiscordPresence.Close) break;
                else if (DiscordPresence.Answer(frame.Opcode, frame.Json).Error is { } error) Log.Warn("Discord did not take the status: " + error);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException or InvalidOperationException) { }
        if (pipe == from) { Disconnect(); Set(State.NotRunning, ""); Log.Info("Discord closed the status connection."); }
    }
    private async Task WriteAsync(int opcode, string json, NamedPipeClientStream? to = null)
    {
        var target = to ?? pipe ?? throw new IOException("Discord is not connected.");
        var frame = DiscordPresence.Frame(opcode, json);
        await writing.WaitAsync();
        try { await target.WriteAsync(frame); await target.FlushAsync(); }
        finally { writing.Release(); }
    }
    private void Disconnect() { var old = pipe; pipe = null; sent = null; connectedTo = ""; old?.Dispose(); }
    private void Set(State state, string problem) { if (Now == state && Problem == problem) return; Now = state; Problem = problem; Changed?.Invoke(); }
    public void Dispose() { wanted = null; Disconnect(); }
}
