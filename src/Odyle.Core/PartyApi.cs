using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odyle.Core;
public sealed record User(string Id, string DisplayName);
// Pairing also answers with the app's device key, the pairing's lasting credential (a server without device keys
// cannot be paired with). ServerVersion: the dashboard version the server runs (ServerVersions), named by
// dashboard 1.0.0 and newer in the pair, sign-in, session and heartbeat answers; null from older ones.
public sealed record AuthResponse(string Token, User Record, string? DeviceKey = null, string? ServerVersion = null);
public sealed record Room(string Id, string Name, int Cost, string Role, bool Sharing, string[]? SharedCharacters = null)
{
    public bool Shares(string characterId) => SharedCharacters?.Contains(characterId) == true;
}
// The message superusers set on the server (Server controls), shown on the card; level is "info" or "warning".
public sealed record Announcement(string Text, string Level = "info")
{
    public bool IsWarning => Level == "warning";
}
// ReaderPaused: the server's operators paused every packet reader (Server controls); the rooms,
// session and heartbeat answers carry it. Backends from before the switch leave it out (null),
// as they leave out Clock, the Odyle clock's rules (OdyleClock.cs).
// Maps are the game's maps as the server's superusers named them (location sharing); null from backends before it.
public sealed record RoomsResponse(Room[] Rooms, int OdylePerCube = 40, Announcement? Announcement = null, bool? ReaderPaused = null, ClockRules? Clock = null, NotificationSettings? Notifications = null, GameMap[]? Maps = null)
{
    // What the app relies on, whatever a server sends (#83): a list where it expects one, rooms with
    // an id, at least one Odyle per cube (it divides by it every second) and clock rules without holes.
    public RoomsResponse Normalized() => this with
    {
        Rooms = (Rooms ?? []).Where(r => r is { Id.Length: > 0 }).Select(r => r with { Name = r.Name ?? "", Role = r.Role ?? "" }).ToArray(),
        OdylePerCube = Math.Max(1, OdylePerCube),
        Clock = Clock?.Normalized(),
        Maps = Maps?.Where(m => m is not null).ToArray(),
    };
}
public sealed record NotificationsResponse(NotificationSettings Notifications);
public sealed record Character(string Id, string User, string CharacterName, string Server, string Region, int Value, bool Confirmed, bool Valid, string LastReadingAt, string LastSeenAt = "", string Source = "", OdyleParts? Parts = null, int? RegenTick = null, int? NaturalMax = null, bool? Shared = null, int? AnsweredCap = null, int Level = 0, string? ClassName = null,
    string? Portrait = null, string? OfficialId = null, string? OfficialRegion = null, int? OfficialServerId = null, Place? Location = null, GameGroup? GameGroup = null)
{
    // Portrait: the game's portrait the server keeps (a public file); OfficialId, OfficialRegion and
    // OfficialServerId: its PLAYNC profile once the daily lookup found it. The Discord status shows both.
    // Location: where the character was last seen, while its player shares their location (Places).
    // GameGroup: the party and force the game put it in, as its player's app read them (GameBands).
    public string Label => $"{CharacterName} · {Server} ({(Confirmed ? Value.ToString("N0", CultureInfo.CurrentCulture) : "—")} Odyle)";
    public string RegionLabel => Region.Replace("GLOBAL_", "") switch { "EU" => "EU", "NA" => "NA-E", "NAW" => "NA-W", "SA" => "SA", "ASIA" => Localizer.T("Asia"), var other => other };
    public string ServerLine => $"{Server} · {RegionLabel}";
    // Class and level as PLAYNC's official search lists them (the dashboard's daily lookup, #25):
    // "Chanter · Lv 45", either alone, or "" until the character was found. Never typed in (R1).
    public string ClassLine => string.Join(" · ", new[] { ClassName ?? "", Level > 0 ? Localizer.T("Lv {0}", Level) : "" }.Where(part => part.Length > 0));
}
// The game's two numbers of a character's last reading: its natural pool and what items added
// (null when that reading did not send them, or the backend is older). RegenTick and NaturalMax
// are what the character's readings showed for the Odyle clock: its last regeneration tick and
// its fullest natural pool of the last 30 days.
public sealed record OdyleParts(int Base, int Bonus);
public sealed record RecordList<T>(T[] Items);
// A room as its members see it: others' characters only when linked to this room.
// Sharing (whether the member shares any character here) and each character's Shared say what the room sees.
public sealed record RoomMember(string Id, string User, string Role, string DisplayName, Character[] Characters, bool? Sharing = null);
// A planned run of a room: planner and attendees are account ids, startsAt a PocketBase date. Waiting are the attendees beyond its dungeon's party size, who wait for a place: they stay in Attendees (null from backends before dashboard 0.41).
public sealed record PlannedRun(string Id, string Dungeon, string StartsAt, string Note = "", string Planner = "", string[]? Attendees = null, string[]? Waiting = null);
public sealed record RoomSnapshot(Room Room, RoomMember[] Members, string ServerNow, PlannedRun[]? Runs = null);
// ShareLocation: the account shares its characters' location with its rooms (an opt-in on the
// dashboard); backends from before location sharing leave it out, which means no.
// PvpStats: the account turned on PvP stats (an opt-in on the dashboard too), so the app reads and
// sends its characters' kills, assists and deaths; left out by backends from before them: no.
// ShareTeammates: the account lets the app send its party and force members' names (an account switch on
// the dashboard, on by default, #81); backends from before the switch leave it out, which means yes.
public sealed record PublisherSession(string SessionId, long Sequence, bool? ReaderPaused = null, bool? ShareLocation = null, bool? PvpStats = null, bool? ShareTeammates = null, string? ServerVersion = null);
public sealed record HeartbeatResponse(bool? ReaderPaused = null, bool? ShareLocation = null, bool? PvpStats = null, bool? ShareTeammates = null, string? ServerVersion = null);
// Shared is false when the account turned the names off: the server queued none. Older backends leave it
// out, which means the names were taken.
public sealed record TeammatesResponse(bool? Shared = null, int Queued = 0)
{
    public bool Taken => Shared != false;
}
// Enabled is false when the account has PvP stats off: the server stored nothing.
public sealed record PvpResponse(bool Enabled, int Stored = 0);
// Shared is false when the account does not share its location: the server stored nothing.
public sealed record LocationResponse(bool Shared);
// Base is the natural pool (it regenerates up to a cap), Bonus what items added; Kind and Delta
// say what changed since the reading before it (OdyleChange). All four are optional: older
// backends ignore them, and the total in Value is what they store.
public sealed record PacketReading(string SessionId, long Sequence, string CharacterName, string Server, string Region, int Value, string ParserVersion, string? CharacterId = null, string Source = "packet", bool Confirmed = true, int? Base = null, int? Bonus = null, string? Kind = null, int? Delta = null);
public sealed record ReportResponse(Character Character);
public sealed class PartyApi : IDisposable
{
    // The backend's 400 answer to a reading whose adapter its registry does not list
    // (backend/odyle/validation.go); the app recognises it by this exact text.
    public const string AdapterRefused = "This packet adapter has not been validated for Global. No reading was saved.";
    // The backend's 503 answer to a reading while its operators pause packet readers; the app then
    // stops capturing until the rooms, session or heartbeat answer says readerPaused is false.
    public const string ReadersPaused = "Packet readers are paused by the server operators.";
    // The backend's 400 answer to a position whose adapter it does not accept; the app sends no more
    // positions until it restarts.
    public const string LocationAdapterRefused = "This location adapter is not accepted.";
    // The same for the party and force ids (POST /api/odyle/game-group).
    public const string GroupAdapterRefused = "This group adapter is not accepted.";
    // The same for the PvP adapter (PvpTracker.Adapter): the app stops reading and sending PvP until it restarts.
    public const string PvpAdapterRefused = "This PvP adapter is not accepted.";
    // The same for the party and force members' names (POST /api/odyle/teammates): none are sent until the app restarts.
    public const string TeammateAdapterRefused = "This teammate adapter is not accepted.";
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // The window's requests run on its UI thread and the realtime connection's on a pool thread (#92): the
    // shared sign-in is taken under a lock, and the state both read is written whole (volatile, and the
    // 429 pause as ticks through Interlocked, since a DateTimeOffset is two words).
    private readonly object signInLock = new();
    private Task<AuthResponse>? signingIn;
    private volatile string? token, deviceKey, serverVersion;
    private volatile bool pairingEnded;
    private long busyUntilTicks;
    public string Endpoint { get; }
    public string? Token { get => token; private set => token = value; }
    // The pairing's lasting credential. Account tokens run out after seven days; the key
    // signs in again for a fresh one, so the app stays paired until it disconnects.
    public string? DeviceKey { get => deviceKey; private set => deviceKey = value; }
    // The dashboard version the server named in its latest pair, sign-in, session or heartbeat answer that named
    // one; null while none did (dashboards before 1.0.0 name none). The window shows ServerVersions' notice by it.
    public string? ServerVersion { get => serverVersion; private set => serverVersion = value; }
    private T Named<T>(T answer, string? version) { if (!string.IsNullOrWhiteSpace(version)) ServerVersion = version; return answer; }
    // Set once the server refused the pairing itself (a sign-in answered 401): every later
    // request fails at once with 401 instead of signing in again and again.
    public bool PairingEnded { get => pairingEnded; private set => pairingEnded = value; }
    // After a 429 every request waits until then without reaching the server.
    public DateTimeOffset BusyUntil
    {
        get => new(Interlocked.Read(ref busyUntilTicks), TimeSpan.Zero);
        private set => Interlocked.Exchange(ref busyUntilTicks, value.UtcTicks);
    }
    // Every sign-in answer (pairing, start-up, the daily renewal, a renewal after a 401)
    // names the account as it is now, for example a display name changed on the dashboard.
    public event Action<AuthResponse>? SignedIn;
    // The name the dashboard lists this PC under: its Windows computer name, at most 40 characters.
    public static string DeviceName => NameOf(Environment.MachineName);
    public static string NameOf(string? machine) { var name = (machine ?? "").Trim(); return name.Length == 0 ? "Windows PC" : name.Length > 40 ? name[..40].TrimEnd() : name; }
    // The app version (for example 0.6.0.12) sent with each reader session, so the server's
    // superuser dashboard can list which versions are connected.
    public string? AppVersion { get; set; }
    public const int ResponseLimit = 4 * 1024 * 1024;
    // The handler the checks passed (null in the app), which the realtime connection then uses too.
    public HttpMessageHandler? Handler { get; }
    // The checks pass a handler that answers like the server; the app always uses the network.
    public PartyApi(string endpoint, string? token = null, string? deviceKey = null, HttpMessageHandler? handler = null)
    {
        Endpoint = ConnectionDetails.NormalizeEndpoint(endpoint);
        client = handler is null ? new HttpClient(NewHandler()) : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(Endpoint + "/"); client.Timeout = TimeSpan.FromSeconds(12);
        // No answer of the API comes near this; a larger one fails like any unreadable answer (#83).
        client.MaxResponseContentBufferSize = ResponseLimit;
        Handler = handler;
        Token = string.IsNullOrWhiteSpace(token) ? null : token; DeviceKey = string.IsNullOrWhiteSpace(deviceKey) ? null : deviceKey;
    }
    // The server's API never redirects. Following one would send the request again, a POST's body
    // with its device key included, to wherever a server or a proxy in front of it points, so a
    // redirect stays an answer the app does not take (RealtimeClient uses the same handler).
    public static HttpMessageHandler NewHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };
    // Whether a 401 answer is the server's own: PocketBase answers every error as a JSON object with
    // its status and a message. Only that answer ends the pairing; a 401 from something in front of
    // the server (a sign-in page, an auth proxy, a misconfigured reverse proxy) is a server that
    // cannot be reached, and the app keeps its pairing and tries again.
    public static bool IsServersOwn401(string? body)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? "");
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 401
                && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String;
        }
        catch (JsonException) { return false; }
    }
    public async Task<AuthResponse> PairAsync(string code)
    {
        var result = await PostAsync<AuthResponse>("api/odyle/pair", new { code, name = DeviceName }, renew: false);
        Named(result, result.ServerVersion);
        // Without a device key the pairing would end with its token, a week later: such a server is too old to pair with.
        if (string.IsNullOrWhiteSpace(result.DeviceKey)) throw new HttpRequestException(Localizer.T("This server runs an Odyle too old to pair with. Ask its operator to update."), null, HttpStatusCode.UpgradeRequired);
        Token = result.Token; DeviceKey = result.DeviceKey; SignedIn?.Invoke(result); return result;
    }
    // A fresh account token from the device key; requests running at once share one sign-in.
    // A 401 here means the server no longer knows this PC: the pairing is over. Without a key
    // (a pairing from before device keys, or one disconnected) there is nothing to sign in with.
    public async Task<AuthResponse> SignInAsync()
    {
        if (DeviceKey is null) { PairingEnded = true; throw new HttpRequestException(Localizer.T("This PC is no longer paired with an account."), null, HttpStatusCode.Unauthorized); }
        Task<AuthResponse> pending;
        lock (signInLock) pending = signingIn ??= PostAsync<AuthResponse>("api/odyle/desktop-sign-in", new { deviceKey = DeviceKey, name = DeviceName }, renew: false);
        try { var result = await pending; Named(result, result.ServerVersion); Token = result.Token; SignedIn?.Invoke(result); return result; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { PairingEnded = true; throw; }
        finally { lock (signInLock) if (signingIn == pending) signingIn = null; }
    }
    // Disconnecting forgets the key on the server too, so it can never sign in again.
    public async Task UnpairAsync()
    {
        if (DeviceKey is not { } key) return;
        DeviceKey = null; await PostAsync<JsonElement>("api/odyle/unpair", new { deviceKey = key }, renew: false);
    }
    public async Task<RoomsResponse> GetRoomsAsync() => (await GetAsync<RoomsResponse>("api/odyle/rooms")).Normalized();
    // The Windows versions the server's operators hold back (#19); older backends answer none.
    public async Task<Version[]> GetHeldBackAsync() => ReleaseFeed.HeldBack((await GetAsync<WindowsReleaseInfo>("api/odyle/windows-release")).Blocked);
    // The regions and game servers the server knows (the armory's search form lists them).
    public Task<ServerRegions> GetServersAsync() => GetAsync<ServerRegions>("api/odyle/armory/servers");
    // The Discord application the server signs in with, for the Discord status; null without Discord sign-in.
    public async Task<string?> GetDiscordApplicationAsync() => DiscordPresence.ApplicationOf((await GetAsync<JsonElement>("api/collections/users/auth-methods")).GetRawText());
    // Whether the backend serves the dashboard's pages at their own paths (DashboardPages): its public start page says so.
    public async Task<bool> ServesPathsAsync()
    {
        using var response = await client.GetAsync("");
        return response.IsSuccessStatusCode && DashboardPages.ServesPaths(await response.Content.ReadAsStringAsync());
    }
    public async Task<Character[]> GetCharactersAsync() => (await GetAsync<RecordList<Character>>("api/collections/characters/records?perPage=500&sort=created")).Items;
    // A paired app names its device key, so a PC removed on the dashboard gets 401 here
    // and ends its pairing like an unknown key at sign-in. Absent fields are left out;
    // backends from before app versions ignore the version.
    public async Task<PublisherSession> StartSessionAsync(string? previousSessionId = null)
    {
        var body = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(previousSessionId)) body["previousSessionId"] = previousSessionId;
        if (DeviceKey is { } key) body["deviceKey"] = key;
        if (!string.IsNullOrEmpty(AppVersion)) body["appVersion"] = AppVersion;
        var session = await PostAsync<PublisherSession>("api/odyle/session", body);
        return Named(session, session.ServerVersion);
    }
    public Task<RoomSnapshot> GetSnapshotAsync(string roomId) => GetAsync<RoomSnapshot>("api/odyle/snapshot/" + Uri.EscapeDataString(roomId));
    // Subscribes a realtime connection (RealtimeClient) to its topics; the server answers 204.
    public async Task SubscribeAsync(string clientId, IReadOnlyCollection<string> topics)
    {
        if (PairingEnded) throw new HttpRequestException(Localizer.T("This PC is no longer paired with an account."), null, HttpStatusCode.Unauthorized);
        var body = new { clientId, subscriptions = topics };
        using (var response = await SendOnceAsync(HttpMethod.Post, "api/realtime", body))
            if (response.StatusCode != HttpStatusCode.Unauthorized || DeviceKey is null) { await FailUnlessSuccessAsync(response); return; }
        await SignInAsync();
        using var again = await SendOnceAsync(HttpMethod.Post, "api/realtime", body);
        await FailUnlessSuccessAsync(again);
    }
    // The account's private daily and weekly checklist; a change answers with the whole list again.
    public Task<TasksResponse> GetTasksAsync() => GetAsync<TasksResponse>("api/odyle/tasks");
    public Task<TasksResponse> SetTaskDoneAsync(string id, bool done) => PostAsync<TasksResponse>("api/odyle/task", new { id, done });
    // reader is what the packet reader is doing (waiting, listening, midstream, reading, problem, npcap,
    // two-clients, paused, stopped) and game the Steam build it reads; the backend's reader health
    // keeps both in memory to warn its operator when a game patch breaks reading. Older backends ignore them.
    public async Task<HeartbeatResponse> HeartbeatAsync(string sessionId, string? characterId, string? reader = null, string? game = null)
    {
        var answer = await PostAsync<HeartbeatResponse>("api/odyle/heartbeat", new { sessionId, characterId, reader, game });
        return Named(answer, answer.ServerVersion);
    }
    // Saves the account's notification settings as a whole (the app does it once, to move its
    // old local "remind me of planned runs" switch to the account).
    public async Task<NotificationSettings> SaveNotificationsAsync(NotificationSettings settings) =>
        (await PostAsync<NotificationsResponse>("api/odyle/notifications", new { channels = settings.Where, leadMinutes = settings.Leads, kinds = settings.What, capTicks = settings.CapTicks ?? 1, quiet = settings.Quiet ?? new QuietHours() })).Notifications;
    // Going to a planned run or not: the card's one run action (planning stays on the dashboard).
    public Task GoingAsync(string roomId, string runId, bool going) => PostAsync<System.Text.Json.JsonElement>("api/odyle/plan", new { roomId, id = runId, going });
    public Task InvalidateAsync(string characterId, string sessionId) => PostAsync<JsonElement>("api/odyle/invalidate", new { characterId, sessionId });
    // A character the game sent a complete snapshot of without its Odyle record (under level 22):
    // rooms say so until its first reading. Sent on its own, so a server from before it answers 404.
    public Task NoOdyleAsync(string characterId) => PostAsync<JsonElement>("api/odyle/no-odyle", new { characterId });
    // The named character's map and position, for an account that shares its location; false when it
    // does not (nothing was stored). Sent on its own, so a server from before it answers 404.
    public async Task<bool> LocationAsync(string sessionId, LocationReport report) =>
        (await PostAsync<LocationResponse>("api/odyle/location", new { sessionId, characterId = report.CharacterId, adapter = LocationReporter.Adapter, map = report.Map, x = Math.Round(report.X, 1), y = Math.Round(report.Y, 1), z = Math.Round(report.Z, 1), heading = report.Heading is { } h ? Math.Round(h, 1) : (double?)null, source = report.Source })).Shared;
    // The party and force the named character is in (GroupTracker), null outside one, so rooms can put
    // players of one party together. Sent on its own, so a server from before it answers 404.
    public Task GameGroupAsync(string sessionId, string characterId, string party, string force) =>
        PostAsync<JsonElement>("api/odyle/game-group", new { sessionId, characterId, adapter = GroupTracker.Adapter, party = party.Length > 0 ? party : null, force = force.Length > 0 ? force : null });
    // The names of the other members of the named character's party and force (GameGroups.Teammates),
    // up to 48, without the party or force, for the armory's scout on the server; it keeps none of
    // them. Answers false when the account turned the names off on the dashboard (nothing was queued).
    // A server from before it answers 404.
    public async Task<bool> TeammatesAsync(string sessionId, string characterId, IReadOnlyCollection<string> names) =>
        (await PostAsync<TeammatesResponse>("api/odyle/teammates", new { sessionId, characterId, adapter = GameGroups.TeammatesAdapter, names })).Taken;
    // The named character's PvP kills, assists and deaths (PvpOutbox), up to 20 at a time, for an
    // account that turned on PvP stats; Enabled is false when it has not (nothing was stored). An
    // event the server already has is not stored twice. A server from before PvP stats answers 404.
    public Task<PvpResponse> PvpAsync(string sessionId, string characterId, IReadOnlyCollection<PvpReport> events) =>
        PostAsync<PvpResponse>("api/odyle/pvp", new { sessionId, characterId, adapter = PvpTracker.Adapter, events });
    public async Task<Character> SaveCharacterAsync(string characterName, string server, string region)
    {
        if (string.IsNullOrWhiteSpace(characterName) || characterName.Trim().Length > 40) throw new ArgumentException(Localizer.T("Enter a character name (up to 40 characters)."));
        if (string.IsNullOrWhiteSpace(server) || server.Trim().Length > 40) throw new ArgumentException(Localizer.T("Enter a server (up to 40 characters)."));
        if (region is not ("GLOBAL_EU" or "GLOBAL_NA" or "GLOBAL_NAW" or "GLOBAL_SA" or "GLOBAL_ASIA")) throw new ArgumentException(Localizer.T("Select a Global region."));
        return (await PostAsync<ReportResponse>("api/odyle/character", new { characterName = characterName.Trim(), server = server.Trim(), region })).Character;
    }
    public async Task<Character> ReportAsync(PacketReading reading)
    {
        if (reading.Source != "packet" || string.IsNullOrWhiteSpace(reading.ParserVersion)) throw new ArgumentException(Localizer.T("A packet adapter name is required."));
        if (reading.Value is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(reading), Localizer.T("Odyle must be between 0 and 1,000,000."));
        if (string.IsNullOrWhiteSpace(reading.CharacterName) || string.IsNullOrWhiteSpace(reading.Server)) throw new ArgumentException(Localizer.T("Character and server are required."));
        return (await PostAsync<ReportResponse>("api/odyle/report", reading)).Character;
    }
    private Task<T> GetAsync<T>(string route) => SendAsync<T>(HttpMethod.Get, route, null, renew: true);
    private Task<T> PostAsync<T>(string route, object body, bool renew = true) => SendAsync<T>(HttpMethod.Post, route, body, renew);
    // A 401 means the account token expired or was replaced; a paired app signs in with
    // its device key and repeats the request once. The server refused it before acting on
    // it. A 401 from that sign-in ends the pairing, so nothing ever loops.
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, bool renew)
    {
        if (PairingEnded) throw new HttpRequestException(Localizer.T("This PC is no longer paired with an account."), null, HttpStatusCode.Unauthorized);
        using (var response = await SendOnceAsync(method, route, body))
            if (response.StatusCode != HttpStatusCode.Unauthorized || !renew || DeviceKey is null) return await ReadAsync<T>(response);
        await SignInAsync();
        using var again = await SendOnceAsync(method, route, body);
        return await ReadAsync<T>(again);
    }
    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string route, object? body)
    {
        if (DateTimeOffset.UtcNow < BusyUntil) throw new ServerBusyException(Localizer.T("Too Many Requests."), BusyUntil);
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (Token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await client.SendAsync(request);
    }
    // Whatever the server sends, a failure is an HttpRequestException with its status code:
    // the server's message when the body has one, else "Server returned 502." An empty,
    // null or unreadable success body is one too, never a JSON or null-reference error.
    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        await FailUnlessSuccessAsync(response);
        var body = await response.Content.ReadAsStringAsync();
        try { if (JsonSerializer.Deserialize<T>(body, Json) is { } value) return value; }
        catch (JsonException) { throw new HttpRequestException(Localizer.T("The server sent an answer the app cannot read."), null, status); }
        throw new HttpRequestException(Localizer.T("The server returned an empty response."), null, status);
    }
    private async Task FailUnlessSuccessAsync(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (response.IsSuccessStatusCode) return;
        var message = Localizer.T("Server returned {0}.", (int)status);
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())) message = text.GetString()!;
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException) { }
        // A 401 that is not the server's own reads as a server that cannot be reached (502), so the
        // saved pairing is kept and the app tries again (IsServersOwn401).
        if (status == HttpStatusCode.Unauthorized && !IsServersOwn401(body)) throw new HttpRequestException(Localizer.T("The server could not be reached: something in front of it answered 401."), null, HttpStatusCode.BadGateway);
        if (status != HttpStatusCode.TooManyRequests) throw new HttpRequestException(message, null, status);
        var now = DateTimeOffset.UtcNow;
        BusyUntil = now + Retry.After(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null, now);
        throw new ServerBusyException(message, BusyUntil);
    }
    public void Dispose() => client.Dispose();
}
public sealed record WindowsReleaseInfo(string[]? Blocked = null);
public sealed record GameServer(int Id, string Name);
public sealed record ServerRegion(string Region, GameServer[]? Servers = null);
// Each region's servers with their ids, as the backend reads them from PLAYNC (GameIdentity.Learn).
public sealed record ServerRegions(ServerRegion[]? Regions)
{
    public IEnumerable<(int Id, string Name)> Servers => (Regions ?? []).SelectMany(r => r?.Servers ?? []).Where(s => s?.Name is not null).Select(s => (s.Id, s.Name));
}
