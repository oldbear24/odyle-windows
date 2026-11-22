using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Odyle.Core;

namespace Odyle.Reader;
// One request at a time to one of the server's routes for what the reader sees beside Odyle (the
// location, the party and force, the teammates' names, PvP). A request asked for while one is on its
// way runs once more after it (next is asked again then, and answers null when there is nothing to
// send or not yet). A 400 naming the route's adapter stops the route for the rest of the connection;
// a 429 pauses it until the server allows requests again; anything else (a server from before the
// route answers 404, paused readers, an expired session, no connection) is logged once until a request
// goes through, and the next request tries again (#92).
public sealed class OneAtATime(string adapterRefused, string refusedLog, string failedLog)
{
    private bool sending, again, failing;
    public bool Refused { get; private set; }
    public DateTimeOffset BlockedUntil { get; private set; }
    // Neither refused nor paused by the server.
    public bool Open => !Refused && DateTimeOffset.UtcNow >= BlockedUntil;
    // refused runs once the server refuses the adapter (dropping what waits to be sent, say).
    public async Task RunAsync(Func<Task?> next, Action? refused = null)
    {
        if (!Open) return;
        if (sending) { again = true; return; }
        sending = true;
        try
        {
            do
            {
                again = false;
                try
                {
                    if (next() is not { } request) break;
                    await request; failing = false;
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message == adapterRefused)
                {
                    Refused = true; refused?.Invoke(); Log.Warn(refusedLog); break;
                }
                catch (ServerBusyException error) { BlockedUntil = error.Until; break; }
                catch (Exception error) { if (!failing) Log.Info($"{failedLog}: {MainWindow.Describe(error)}"); failing = true; }
            }
            while (again && Open);
        }
        finally { sending = false; }
    }
}
// What one connection to a server answered and refused: made when the window sets up its request client
// (UseApi) and replaced when it drops it (DropApi), so a reset is one assignment and nothing one server
// said carries over to the next, also when this PC pairs with another server without a restart (#92).
// A new publisher session on the same connection (ReconnectAsync) keeps it.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051", Justification = "A plain bag of per-connection state that the window and the checks read and set in place.")]
public sealed class ConnectionState
{
    // Location sharing (an opt-in on the dashboard): the newest position waits while one is on its way.
    public bool ShareLocation;
    public LocationReport? LocationNext;
    public readonly OneAtATime Location = new(PartyApi.LocationAdapterRefused, $"The server does not accept the location adapter {LocationReporter.Adapter}; no positions are sent until Odyle connects again.", "A position was not sent");
    // The party and force the named character is in.
    public readonly OneAtATime Group = new(PartyApi.GroupAdapterRefused, $"The server does not accept the group adapter {GroupTracker.Adapter}; parties and forces are not sent until Odyle connects again.", "The party and force were not sent");
    // The party and force members' names: each sent once per connection; the account can turn them off.
    public bool TeammatesOff;
    public readonly HashSet<string> TeammatesSent = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> TeammatesWaiting = [];
    public readonly OneAtATime Teammates = new(PartyApi.TeammateAdapterRefused, $"The server does not accept the teammate adapter {GameGroups.TeammatesAdapter}; party and force members' names are not sent until Odyle connects again.", "Party and force members' names were not sent");
    // PvP stats (an opt-in on the dashboard): events wait up to an hour for the next try.
    public bool PvpStats;
    public readonly PvpOutbox PvpOutbox = new();
    public readonly OneAtATime Pvp = new(PartyApi.PvpAdapterRefused, $"The server does not accept the PvP adapter {PvpTracker.Adapter}; PvP is not read or sent until Odyle connects again.", "PvP events were not sent");
    // The character whose missing Odyle the rooms were told of.
    public string NoOdyleSent = "";
    // The server refused this build's adapter name: Odyle needs an update to keep reading.
    public bool AdapterOutdated;
    // When the server's game servers were last asked for (the first character named on the connection),
    // and whether that answer was read.
    public DateTimeOffset ServersAskedAt = DateTimeOffset.MinValue;
    public bool ServersKnown;
    // The dashboard version the server named (PartyApi.ServerVersion) as last noted for this connection, and whether
    // it was noted yet (logged once, and again when it changes).
    public string? ServerVersion;
    public bool ServerVersionNoted;
    // The Discord application the server signs in with (the Discord status), or none.
    public string? DiscordApp;
    public bool DiscordMissing;
    public DateTimeOffset DiscordAskedAt = DateTimeOffset.MinValue;
    // Whether the server serves the dashboard's pages at their own paths.
    public bool? DashboardPaths;
    public DateTimeOffset PathsAskedAt = DateTimeOffset.MinValue;
}
