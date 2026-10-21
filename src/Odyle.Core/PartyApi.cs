using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odyle.Core;
public sealed record User(string Id, string DisplayName);
// Pairing also answers with the app's device key; backends from before device keys omit it.
public sealed record AuthResponse(string Token, User Record, string? DeviceKey = null);
public sealed record DeviceKeyResponse(string DeviceKey);
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
public sealed record RoomsResponse(Room[] Rooms, int OdylePerCube = 40, Announcement? Announcement = null, bool? ReaderPaused = null, ClockRules? Clock = null, NotificationSettings? Notifications = null);
public sealed record NotificationsResponse(NotificationSettings Notifications);
public sealed record Character(string Id, string User, string CharacterName, string Server, string Region, int Value, bool Confirmed, bool Valid, string LastReadingAt, string LastSeenAt = "", string Source = "", OdyleParts? Parts = null, int? RegenTick = null, int? NaturalMax = null, bool? Shared = null, int? AnsweredCap = null, int Level = 0, string? ClassName = null)
{
    public string Label => $"{CharacterName} · {Server} ({(Confirmed ? Value.ToString("N0") : "—")} Odyle)";
    public string RegionLabel => Region.Replace("GLOBAL_", "") switch { "EU" => "EU", "NA" => "NA-E", "NAW" => "NA-W", "SA" => "SA", "ASIA" => Localizer.T("Asia"), var other => other };
    public string Location => $"{Server} · {RegionLabel}";
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
// Sharing (the member-wide switch) and each character's Shared say what the room sees (null: older backends).
public sealed record RoomMember(string Id, string User, string Role, string DisplayName, Character[] Characters, bool? Sharing = null);
// A planned run of a room: planner and attendees are account ids, startsAt a PocketBase date.
public sealed record PlannedRun(string Id, string Dungeon, string StartsAt, string Note = "", string Planner = "", string[]? Attendees = null);
public sealed record RoomSnapshot(Room Room, RoomMember[] Members, string ServerNow, PlannedRun[]? Runs = null);
public sealed record PublisherSession(string SessionId, long Sequence, bool? ReaderPaused = null);
public sealed record HeartbeatResponse(bool? ReaderPaused = null);
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
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Task<AuthResponse>? signingIn;
    public string Endpoint { get; }
    public string? Token { get; private set; }
    // The pairing's lasting credential. Account tokens run out after seven days; the key
    // signs in again for a fresh one, so the app stays paired until it disconnects.
    public string? DeviceKey { get; private set; }
    // Set once the server refused the pairing itself (a sign-in answered 401): every later
    // request fails at once with 401 instead of signing in again and again.
    public bool PairingEnded { get; private set; }
    // After a 429 every request waits until then without reaching the server.
    public DateTimeOffset BusyUntil { get; private set; }
    // Every sign-in answer (pairing, start-up, the daily renewal, a renewal after a 401)
    // names the account as it is now, for example a display name changed on the dashboard.
    public event Action<AuthResponse>? SignedIn;
    // The name the dashboard lists this PC under: its Windows computer name, at most 40 characters.
    public static string DeviceName => NameOf(Environment.MachineName);
    public static string NameOf(string? machine) { var name = (machine ?? "").Trim(); return name.Length == 0 ? "Windows PC" : name.Length > 40 ? name[..40].TrimEnd() : name; }
    // The app version (for example 0.6.0.12) sent with each reader session, so the server's
    // superuser dashboard can list which versions are connected.
    public string? AppVersion { get; set; }
    // The checks pass a handler that answers like the server; the app always uses the network.
    public PartyApi(string endpoint, string? token = null, string? deviceKey = null, HttpMessageHandler? handler = null)
    {
        Endpoint = ConnectionDetails.NormalizeEndpoint(endpoint);
        client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(Endpoint + "/"); client.Timeout = TimeSpan.FromSeconds(12);
        Token = string.IsNullOrWhiteSpace(token) ? null : token; DeviceKey = string.IsNullOrWhiteSpace(deviceKey) ? null : deviceKey;
    }
    public async Task<AuthResponse> PairAsync(string code)
    {
        var result = await PostAsync<AuthResponse>("api/odyle/pair", new { code, name = DeviceName }, renew: false);
        Token = result.Token; DeviceKey = result.DeviceKey; SignedIn?.Invoke(result); return result;
    }
    public async Task<AuthResponse> RefreshAsync()
    {
        try { var result = await PostAsync<AuthResponse>("api/collections/users/auth-refresh", new { }, renew: false); Token = result.Token; SignedIn?.Invoke(result); return result; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { PairingEnded = true; throw; }
    }
    // A fresh account token: from the device key, or by refreshing the token of an app
    // paired before device keys existed. Requests running at once share one sign-in.
    // A 401 here means the server no longer knows this PC: the pairing is over.
    public async Task<AuthResponse> SignInAsync()
    {
        if (DeviceKey is null) return await RefreshAsync();
        var pending = signingIn ??= PostAsync<AuthResponse>("api/odyle/desktop-sign-in", new { deviceKey = DeviceKey, name = DeviceName }, renew: false);
        try { var result = await pending; Token = result.Token; SignedIn?.Invoke(result); return result; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { PairingEnded = true; throw; }
        finally { if (signingIn == pending) signingIn = null; }
    }
    // Gives an app paired before device keys existed its key, using the still-valid token.
    // A backend without device keys answers 404; the app then keeps refreshing its token.
    public async Task<string?> EnsureDeviceKeyAsync()
    {
        if (DeviceKey is not null) return DeviceKey;
        try { DeviceKey = (await PostAsync<DeviceKeyResponse>("api/odyle/desktop-device", new { name = DeviceName })).DeviceKey; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        return DeviceKey;
    }
    // Disconnecting forgets the key on the server too, so it can never sign in again.
    public async Task UnpairAsync()
    {
        if (DeviceKey is not { } key) return;
        DeviceKey = null; await PostAsync<JsonElement>("api/odyle/unpair", new { deviceKey = key }, renew: false);
    }
    public Task<RoomsResponse> GetRoomsAsync() => GetAsync<RoomsResponse>("api/odyle/rooms");
    // Whether the server keeps NA-West apart (dashboard 0.21.0 and newer list GLOBAL_NAW among the armory's regions).
    // The Windows versions the server's operators hold back (#19); older backends answer none.
    public async Task<Version[]> GetHeldBackAsync() => ReleaseFeed.HeldBack((await GetAsync<WindowsReleaseInfo>("api/odyle/windows-release")).Blocked);
    public async Task<bool> KnowsNaWestAsync() => (await GetAsync<ServerRegions>("api/odyle/armory/servers")).Regions.Any(r => r.Region == "GLOBAL_NAW");
    // The Discord application the server signs in with, for the Discord status; null without Discord sign-in.
    public async Task<string?> GetDiscordApplicationAsync() => DiscordPresence.ApplicationOf((await GetAsync<JsonElement>("api/collections/users/auth-methods")).GetRawText());
    public async Task<Character[]> GetCharactersAsync() => (await GetAsync<RecordList<Character>>("api/collections/characters/records?perPage=500&sort=created")).Items;
    // A paired app names its device key, so a PC removed on the dashboard gets 401 here
    // and ends its pairing like an unknown key at sign-in. Absent fields are left out;
    // backends from before app versions ignore the version.
    public Task<PublisherSession> StartSessionAsync(string? previousSessionId = null)
    {
        var body = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(previousSessionId)) body["previousSessionId"] = previousSessionId;
        if (DeviceKey is { } key) body["deviceKey"] = key;
        if (!string.IsNullOrEmpty(AppVersion)) body["appVersion"] = AppVersion;
        return PostAsync<PublisherSession>("api/odyle/session", body);
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
    public Task<HeartbeatResponse> HeartbeatAsync(string sessionId, string? characterId, string? reader = null, string? game = null) => PostAsync<HeartbeatResponse>("api/odyle/heartbeat", new { sessionId, characterId, reader, game });
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
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())) message = text.GetString()!;
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException) { }
        if (status != HttpStatusCode.TooManyRequests) throw new HttpRequestException(message, null, status);
        var now = DateTimeOffset.UtcNow;
        BusyUntil = now + Retry.After(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null, now);
        throw new ServerBusyException(message, BusyUntil);
    }
    public void Dispose() => client.Dispose();
}
public sealed record WindowsReleaseInfo(string[]? Blocked = null);
public sealed record ServerRegion(string Region);
public sealed record ServerRegions(ServerRegion[] Regions);
