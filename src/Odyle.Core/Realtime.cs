using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Odyle.Core;
// News from the backend's realtime topics (the dashboard's docs/api.md#realtime): a room's
// changes and its characters' heartbeats, and the account's own changes. Refresh means
// "fetch again"; Characters are changed characters to apply in place, Seen heartbeats
// (character id → lastSeenAt) and Removed that the account left the room. News for everyone
// comes on the account's topic too: Release is the version of a newer Windows release, and
// AnnouncementChanged says the announcement changed to Announcement (null when cleared).
// Notifications are the account's notification settings, saved meanwhile on the dashboard, and
// CapAlerts the cap alerts of its characters (CapAlerts.cs).
public sealed record RealtimeNews(bool Refresh, bool Removed, Character[] Characters, IReadOnlyDictionary<string, string> Seen)
{
    public Version? Release { get; init; }
    public bool AnnouncementChanged { get; init; }
    public Announcement? Announcement { get; init; }
    public NotificationSettings? Notifications { get; init; }
    public CapAlert[] CapAlerts { get; init; } = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // A message the app cannot read means "fetch again".
    public static RealtimeNews Parse(JsonElement data)
    {
        var again = new RealtimeNews(true, false, [], new Dictionary<string, string>());
        if (data.ValueKind != JsonValueKind.Object) return again;
        bool Flag(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        Character[] characters;
        try { characters = data.TryGetProperty("characters", out var list) && list.ValueKind == JsonValueKind.Array ? list.Deserialize<Character[]>(Json) ?? [] : []; }
        catch (JsonException) { return again; }
        var seen = new Dictionary<string, string>();
        if (data.TryGetProperty("seen", out var times) && times.ValueKind == JsonValueKind.Object)
            foreach (var entry in times.EnumerateObject()) if (entry.Value.ValueKind == JsonValueKind.String) seen[entry.Name] = entry.Value.GetString()!;
        Version? release = null;
        if (data.TryGetProperty("release", out var news) && news.ValueKind == JsonValueKind.Object && news.TryGetProperty("version", out var text) && text.ValueKind == JsonValueKind.String && Version.TryParse(text.GetString(), out var parsed))
            release = ReleaseFeed.Normalize(parsed);
        var changed = data.TryGetProperty("announcement", out var message) && message.ValueKind is JsonValueKind.Object or JsonValueKind.Null;
        Announcement? announcement = null;
        if (changed && message.ValueKind == JsonValueKind.Object)
        {
            try { announcement = message.Deserialize<Announcement>(Json); } catch (JsonException) { changed = false; }
            if (announcement is not { Text.Length: > 0 }) announcement = null;
        }
        NotificationSettings? notifications = null;
        if (data.TryGetProperty("notifications", out var settings) && settings.ValueKind == JsonValueKind.Object)
            try { notifications = settings.Deserialize<NotificationSettings>(Json); } catch (JsonException) { }
        CapAlert[] alerts = [];
        if (data.TryGetProperty("capAlerts", out var told) && told.ValueKind == JsonValueKind.Array)
            try { alerts = told.Deserialize<CapAlert[]>(Json)?.Where(a => a is { CharacterName.Length: > 0, State: "soon" or "full" }).ToArray() ?? []; } catch (JsonException) { }
        return new(Flag("refresh"), Flag("removed"), characters, seen) { Release = release, AnnouncementChanged = changed, Announcement = announcement, Notifications = notifications, CapAlerts = alerts };
    }
}
// The topics the app listens to: its account's own and, for each of its rooms, the room's
// changes and its heartbeats.
public static class RealtimeTopics
{
    public const string Own = "odyle.user.", Room = "odyle.room.", Presence = "odyle.presence.";
    public static string[] For(string accountId, IEnumerable<Room> rooms) => [Own + accountId, .. rooms.SelectMany(r => new[] { Room + r.Id, Presence + r.Id })];
}
// Realtime news applied to what the app shows, without fetching it again. complete is
// false when a changed character was not listed yet: the app then fetches again.
public static class PartyNews
{
    public static RoomSnapshot[] WithCharacters(RoomSnapshot[] snapshots, string roomId, Character[] changed, out bool complete)
    {
        var changedById = ById(changed);
        var found = new HashSet<string>();
        var next = snapshots.Select(s => s.Room.Id != roomId ? s : s with
        {
            // A copy without the room's shared flag keeps the one the snapshot had.
            Members = s.Members.Select(m => m with { Characters = m.Characters.Select(c => { if (!changedById.TryGetValue(c.Id, out var n)) return c; found.Add(c.Id); return n with { Shared = n.Shared ?? c.Shared }; }).ToArray() }).ToArray(),
        }).ToArray();
        complete = changedById.Keys.All(found.Contains);
        return next;
    }
    public static Character[] WithCharacters(Character[] own, Character[] changed, out bool complete)
    {
        var known = own.Select(c => c.Id).ToHashSet();
        complete = changed.All(c => known.Contains(c.Id));
        var changedById = ById(changed);
        return own.Select(c => changedById.TryGetValue(c.Id, out var n) ? n : c).ToArray();
    }
    // Heartbeats: a later lastSeenAt replaces an earlier one, never the other way round.
    public static RoomSnapshot[] WithSeen(RoomSnapshot[] snapshots, string roomId, IReadOnlyDictionary<string, string> seen) =>
        snapshots.Select(s => s.Room.Id != roomId ? s : s with
        {
            Members = s.Members.Select(m => m with { Characters = m.Characters.Select(c => seen.TryGetValue(c.Id, out var at) && Later(at, c.LastSeenAt) ? c with { LastSeenAt = at } : c).ToArray() }).ToArray(),
        }).ToArray();
    // The last copy of each character.
    private static Dictionary<string, Character> ById(Character[] changed) => changed.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
    private static bool Later(string at, string than) => Readiness.Parse(at) is { } next && (Readiness.Parse(than) is not { } before || next > before);
}
// The realtime connection: PocketBase's server-sent events at /api/realtime. Once started
// it keeps itself up: after a drop it connects again (1, 2, 5, 10 s, then every 30 s) and
// subscribes its topics again with the account's current token, and a connection silent
// for six minutes (the server closes idle ones after five) counts as dropped. Connected
// tells whether it is a reconnect: news may have been missed meanwhile. Events come from a
// background thread.
public sealed class RealtimeClient : IDisposable
{
    private static readonly TimeSpan[] Steps = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    public static readonly TimeSpan Silence = TimeSpan.FromMinutes(6);
    private readonly PartyApi api;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private string[] topics = [];
    private string? clientId;
    private bool started, connectedBefore;
    public event Action<bool>? Connected;
    public event Action? Disconnected;
    public event Action<string, JsonElement>? Message;
    public bool IsConnected { get; private set; }
    // Why the last connection failed or ended, for the log.
    public string? LastProblem { get; private set; }
    public static TimeSpan Backoff(int attempt) => Steps[Math.Clamp(attempt, 0, Steps.Length - 1)];
    public RealtimeClient(PartyApi api, HttpMessageHandler? handler = null)
    {
        this.api = api;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(api.Endpoint + "/"); http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public void Start(IEnumerable<string> listen)
    {
        lock (gate) { topics = listen.Distinct().ToArray(); if (started) return; started = true; }
        _ = Task.Run(() => RunAsync(stop.Token));
    }
    // Changes the topics, for example after the account joined or left a room.
    public async Task ListenAsync(IEnumerable<string> listen)
    {
        var next = listen.Distinct().ToArray();
        lock (gate) { if (next.SequenceEqual(topics)) return; topics = next; }
        if (IsConnected) { try { await SubscribeAsync(); } catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException) { LastProblem = error.Message; } }
    }
    private async Task SubscribeAsync()
    {
        string? id; string[] wanted;
        lock (gate) { id = clientId; wanted = topics; }
        if (id is null) return;
        try { await api.SubscribeAsync(id, wanted); }
        // A room the account no longer belongs to is refused: listen to the rest until the
        // app has the new list of rooms.
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden && wanted.Length > 1)
        {
            await api.SubscribeAsync(id, wanted.Where(t => t.StartsWith(RealtimeTopics.Own, StringComparison.Ordinal)).ToArray());
        }
    }
    private async Task RunAsync(CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/realtime");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? name = null; var data = new StringBuilder();
                while (true)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(token); quiet.CancelAfter(Silence);
                    var line = await reader.ReadLineAsync(quiet.Token);
                    if (line is null) break;
                    if (line.Length > 0)
                    {
                        if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(data.Length > 0 ? "\n" : "").Append(line[5..].TrimStart());
                        continue;
                    }
                    if (name is not null && data.Length > 0 && await DispatchAsync(name, data.ToString())) attempt = 0;
                    name = null; data.Clear();
                }
                LastProblem = "The server closed the realtime connection.";
            }
            catch (Exception) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { LastProblem = error.Message; }
            lock (gate) clientId = null;
            if (IsConnected) { IsConnected = false; Disconnected?.Invoke(); }
            try { await Task.Delay(Backoff(attempt++), token); } catch (OperationCanceledException) { break; }
        }
    }
    // Answers whether the event was the connection's start, subscribed.
    private async Task<bool> DispatchAsync(string name, string data)
    {
        using var parsed = JsonDocument.Parse(data);
        if (name == "PB_CONNECT")
        {
            lock (gate) clientId = parsed.RootElement.GetProperty("clientId").GetString();
            await SubscribeAsync();
            var again = connectedBefore; connectedBefore = true; IsConnected = true; LastProblem = null;
            Connected?.Invoke(again);
            return true;
        }
        Message?.Invoke(name, parsed.RootElement.Clone());
        return false;
    }
    public void Dispose()
    {
        stop.Cancel(); http.Dispose();
    }
}
