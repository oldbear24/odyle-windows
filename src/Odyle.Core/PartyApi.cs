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
public sealed record RoomsResponse(Room[] Rooms, int OdylePerCube = 40);
public sealed record Character(string Id, string User, string CharacterName, string Server, string Region, int Value, bool Confirmed, bool Valid, string LastReadingAt, string LastSeenAt = "", string Source = "")
{
    public string Label => $"{CharacterName} · {Server} ({(Confirmed ? Value.ToString("N0") : "—")} Odyle)";
    public string RegionLabel => Region.Replace("GLOBAL_", "") switch { "EU" => "EU", "NA" => "NA", "SA" => "SA", "ASIA" => "Asia", var other => other };
    public string Location => $"{Server} · {RegionLabel}";
}
public sealed record RecordList<T>(T[] Items);
// A room as its members see it: others' characters only when linked to this room.
public sealed record RoomMember(string Id, string User, string Role, string DisplayName, Character[] Characters);
public sealed record RoomSnapshot(Room Room, RoomMember[] Members, string ServerNow);
public sealed record PublisherSession(string SessionId, long Sequence);
public sealed record PacketReading(string SessionId, long Sequence, string CharacterName, string Server, string Region, int Value, string ParserVersion, string? CharacterId = null, string Source = "packet", bool Confirmed = true);
public sealed record ReportResponse(Character Character);
public sealed class PartyApi : IDisposable
{
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Task<AuthResponse>? signingIn;
    public string Endpoint { get; }
    public string? Token { get; private set; }
    // The pairing's lasting credential. Account tokens run out after seven days; the key
    // signs in again for a fresh one, so the app stays paired until it disconnects.
    public string? DeviceKey { get; private set; }
    public PartyApi(string endpoint, string? token = null, string? deviceKey = null)
    {
        Endpoint = ConnectionDetails.NormalizeEndpoint(endpoint);
        client = new HttpClient { BaseAddress = new Uri(Endpoint + "/"), Timeout = TimeSpan.FromSeconds(12) };
        Token = string.IsNullOrWhiteSpace(token) ? null : token; DeviceKey = string.IsNullOrWhiteSpace(deviceKey) ? null : deviceKey;
    }
    public async Task<AuthResponse> PairAsync(string code)
    {
        var result = await PostAsync<AuthResponse>("api/odyle/pair", new { code }, renew: false);
        Token = result.Token; DeviceKey = result.DeviceKey; return result;
    }
    public async Task<AuthResponse> RefreshAsync()
    {
        var result = await PostAsync<AuthResponse>("api/collections/users/auth-refresh", new { }, renew: false);
        Token = result.Token; return result;
    }
    // A fresh account token: from the device key, or by refreshing the token of an app
    // paired before device keys existed. Requests running at once share one sign-in.
    public async Task<AuthResponse> SignInAsync()
    {
        if (DeviceKey is null) return await RefreshAsync();
        var pending = signingIn ??= PostAsync<AuthResponse>("api/odyle/desktop-sign-in", new { deviceKey = DeviceKey }, renew: false);
        try { var result = await pending; Token = result.Token; return result; }
        finally { if (signingIn == pending) signingIn = null; }
    }
    // Gives an app paired before device keys existed its key, using the still-valid token.
    // A backend without device keys answers 404; the app then keeps refreshing its token.
    public async Task<string?> EnsureDeviceKeyAsync()
    {
        if (DeviceKey is not null) return DeviceKey;
        try { DeviceKey = (await PostAsync<DeviceKeyResponse>("api/odyle/desktop-device", new { })).DeviceKey; }
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
    public async Task<Character[]> GetCharactersAsync() => (await GetAsync<RecordList<Character>>("api/collections/characters/records?perPage=500&sort=created")).Items;
    public Task<PublisherSession> StartSessionAsync(string? previousSessionId = null) => PostAsync<PublisherSession>("api/odyle/session", new { previousSessionId });
    public Task<RoomSnapshot> GetSnapshotAsync(string roomId) => GetAsync<RoomSnapshot>("api/odyle/snapshot/" + Uri.EscapeDataString(roomId));
    // The account's private daily and weekly checklist; a change answers with the whole list again.
    public Task<TasksResponse> GetTasksAsync() => GetAsync<TasksResponse>("api/odyle/tasks");
    public Task<TasksResponse> SetTaskDoneAsync(string id, bool done) => PostAsync<TasksResponse>("api/odyle/task", new { id, done });
    public Task HeartbeatAsync(string sessionId, string? characterId) => PostAsync<JsonElement>("api/odyle/heartbeat", new { sessionId, characterId });
    public Task InvalidateAsync(string characterId, string sessionId) => PostAsync<JsonElement>("api/odyle/invalidate", new { characterId, sessionId });
    public Task LeaveAsync(string roomId) => PostAsync<JsonElement>("api/odyle/leave", new { roomId });
    public async Task<Character> SaveCharacterAsync(string characterName, string server, string region)
    {
        if (string.IsNullOrWhiteSpace(characterName) || characterName.Trim().Length > 40) throw new ArgumentException("Enter a character name (up to 40 characters).");
        if (string.IsNullOrWhiteSpace(server) || server.Trim().Length > 40) throw new ArgumentException("Enter a server (up to 40 characters).");
        if (region is not ("GLOBAL_EU" or "GLOBAL_NA" or "GLOBAL_SA" or "GLOBAL_ASIA")) throw new ArgumentException("Select a Global region.");
        return (await PostAsync<ReportResponse>("api/odyle/character", new { characterName = characterName.Trim(), server = server.Trim(), region })).Character;
    }
    public async Task<Character> ReportAsync(PacketReading reading)
    {
        if (reading.Source != "packet" || string.IsNullOrWhiteSpace(reading.ParserVersion)) throw new ArgumentException("A packet adapter name is required.");
        if (reading.Value is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(reading), "Odyle must be between 0 and 1,000,000.");
        if (string.IsNullOrWhiteSpace(reading.CharacterName) || string.IsNullOrWhiteSpace(reading.Server)) throw new ArgumentException("Character and server are required.");
        return (await PostAsync<ReportResponse>("api/odyle/report", reading)).Character;
    }
    private Task<T> GetAsync<T>(string route) => SendAsync<T>(HttpMethod.Get, route, null, renew: true);
    private Task<T> PostAsync<T>(string route, object body, bool renew = true) => SendAsync<T>(HttpMethod.Post, route, body, renew);
    // A 401 means the account token expired or was replaced; a paired app signs in with
    // its device key and repeats the request once. The server refused it before acting on it.
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, bool renew)
    {
        using (var response = await SendOnceAsync(method, route, body))
            if (response.StatusCode != HttpStatusCode.Unauthorized || !renew || DeviceKey is null) return await ReadAsync<T>(response);
        await SignInAsync();
        using var again = await SendOnceAsync(method, route, body);
        return await ReadAsync<T>(again);
    }
    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string route, object? body)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (Token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await client.SendAsync(request);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string message = "Server returned " + (int)response.StatusCode + ".";
            try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); message = doc.RootElement.GetProperty("message").GetString() ?? message; } catch (JsonException) { }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidOperationException("The server returned an empty response.");
    }
    public void Dispose() => client.Dispose();
}
