using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Threading;
using Odyle.Core;

namespace Odyle.Reader;
// The connection to the paired server through its life (W#90: moved out of MainWindow, which shows it): the
// request client, the publisher session and its heartbeat, and the retries of a saved pairing the server
// could not confirm. What the server answered and refused on this connection is ConnectionState's.
//
// A saved pairing the server could not confirm at start (offline, 403, 5xx, 429) is tried again by itself:
// 15 s, 30 s, 1 min, 2 min, then every 5 minutes (Retry.Backoff), or when a 429 allows it, if later. The
// heartbeat runs every 20 seconds while connected; a session that missed 45 seconds of it, or failed in a way
// a new session may cure, is started again (NeedsReconnect), and the account token is renewed daily.
internal sealed class ConnectionLifecycle
{
    internal static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(20), SessionLost = TimeSpan.FromSeconds(45), TokenRenewal = TimeSpan.FromHours(24);
    internal readonly DispatcherTimer Heartbeat = new() { Interval = HeartbeatEvery };
    internal readonly DispatcherTimer RetryTimer = new();
    // The request client of the connection, null while not connected.
    internal PartyApi? Api;
    // The server of the connection, or of the saved pairing while it is tried again; "" once unpaired.
    internal string Endpoint = "";
    internal string SessionId = "";
    internal long Sequence;
    internal DateTimeOffset LastTick = DateTimeOffset.UtcNow, TokenRenewedAt;
    internal bool NeedsReconnect;
    // The next automatic try of a saved pairing, and why the last one failed ("busy" for a 429).
    internal DateTimeOffset? RetryAt;
    internal int Attempt;
    internal string RetryProblem = "";
    // Paired, but the server has not confirmed the pairing yet: it is tried again by itself.
    internal bool Offline => Api is null && RetryAt is not null;

    internal void Use(PartyApi next) { Api = next; Endpoint = next.Endpoint; next.AppVersion = Updater.Current.ToString(); }
    // Stops the heartbeat and closes the request client.
    internal void Drop() { Heartbeat.Stop(); Api?.Dispose(); Api = null; }
    // Unpaired: nothing is tried again and the pairing's server is forgotten.
    internal void Forget() { StopRetrying(); Endpoint = ""; }
    internal void StopRetrying() { RetryTimer.Stop(); RetryAt = null; }
    // Schedules the next try of the saved pairing after error; answers the wait and logs it.
    internal TimeSpan ScheduleRetry(Exception error, DateTimeOffset now)
    {
        var wait = Retry.Backoff(Attempt++);
        if (error is ServerBusyException paused && paused.Until - now > wait) wait = paused.Until - now;
        RetryAt = now + wait; RetryProblem = error is ServerBusyException ? "busy" : ""; RetryTimer.Interval = wait; RetryTimer.Start();
        Log.Warn($"The saved pairing could not be confirmed ({(error is HttpRequestException { StatusCode: { } code } ? (int)code : error.GetType().Name)}: {error.Message}); attempt {Attempt}, next in {wait.TotalSeconds:0} s.");
        return wait;
    }
    // A session started on a connected account: the retries end and the heartbeat starts.
    internal void Connected(PublisherSession session)
    {
        SessionId = session.SessionId; Sequence = session.Sequence;
        StopRetrying(); Attempt = 0;
    }
    internal void StartHeartbeat(DateTimeOffset now) { LastTick = TokenRenewedAt = now; NeedsReconnect = false; Heartbeat.Start(); }
    // A new publisher session. The previous one may be gone (expired, or deleted with a PC removed on the
    // dashboard); a fresh one names the device key, and a removed PC answers 401 there. Only a server that
    // answered (below 500) is asked again without it: offline, a timeout or a server error would fail the same
    // way, and Stop or Disconnect would wait for both (#86).
    internal async Task<PublisherSession> RenewSessionAsync(PartyApi api)
    {
        PublisherSession session;
        try { session = await api.StartSessionAsync(SessionId); }
        catch (HttpRequestException error) when (SessionGone(error)) { session = await api.StartSessionAsync(); }
        SessionId = session.SessionId; Sequence = session.Sequence; NeedsReconnect = false;
        return session;
    }
    // Whether a failed session start with the previous session id is worth one more try without it: the server
    // answered, and not 401 (the pairing ended) or 429 (it asks to wait).
    internal static bool SessionGone(HttpRequestException error) => error.StatusCode is { } status && (int)status < 500 && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests);
    // The server asked the app to wait (429): nothing is sent until then, and the quiet time does not count as a
    // lost connection.
    internal bool Waiting(PartyApi api, DateTimeOffset now)
    {
        if (api.BusyUntil <= now) return false;
        LastTick = api.BusyUntil;
        return true;
    }
    // Account tokens last seven days: renewed daily so a long-running app never holds an expired one.
    internal bool TokenDue(DateTimeOffset now) => now - TokenRenewedAt > TokenRenewal;
    internal bool SessionDue(DateTimeOffset now) => NeedsReconnect || now - LastTick > SessionLost;
    // A failed operation on a connected account: a timeout, no connection, a server error or a 401 start a new
    // session at the next heartbeat. A 429 only pauses requests; it never rotates the publisher session.
    internal void NoteFailure(Exception error)
    {
        if (Api is not null && error is not ServerBusyException && (error is TaskCanceledException || error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500 || http.StatusCode == HttpStatusCode.Unauthorized))) NeedsReconnect = true;
    }
}
