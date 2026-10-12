using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odyle.Core;
public sealed record User(string Id, string DisplayName);
public sealed record AuthResponse(string Token, User Record);
public sealed record Room(string Id, string Name, int Cost, string Role, bool Sharing);
public sealed record Membership(string Id, string Room, string User, string Role, bool Sharing);
public sealed record JoinResponse(Room Room, Membership Membership);
public sealed record RoomsResponse(Room[] Rooms);
public sealed record Character(string Id, string User, string CharacterName, string Server, string Region, int Value, bool Confirmed, bool Valid, string LastReadingAt)
{
    public string Label => $"{CharacterName} · {Server} ({(Confirmed ? Value.ToString() : "—")} Odyle)";
}
public sealed record RecordList<T>(T[] Items);
public sealed record PublisherSession(string SessionId, long Sequence);
public sealed record ManualReading(string SessionId, long Sequence, string CharacterName, string Server, string Region, int Value, string? CharacterId = null, string Source = "manual", bool Confirmed = true);
public sealed record ReportResponse(Character Character);
public sealed class PartyApi : IDisposable
{
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public PartyApi(string endpoint, string? token = null)
    {
        var uri = new Uri(ConnectionDetails.NormalizeEndpoint(endpoint) + "/");
        client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(12) };
        if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task<AuthResponse> PairAsync(string code)
    {
        var result = await PostAsync<AuthResponse>("api/odyle/pair", new { code });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token); return result;
    }
    public async Task<AuthResponse> RefreshAsync()
    {
        var result = await PostAsync<AuthResponse>("api/collections/users/auth-refresh", new { });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
        return result;
    }
    public async Task<Room[]> GetRoomsAsync() => (await GetAsync<RoomsResponse>("api/odyle/rooms")).Rooms;
    public async Task<Character[]> GetCharactersAsync() => (await GetAsync<RecordList<Character>>("api/collections/characters/records?perPage=500&sort=created")).Items;
    public Task<JoinResponse> JoinAsync(string code) => PostAsync<JoinResponse>("api/odyle/join", new { code });
    public Task<PublisherSession> StartSessionAsync(string? previousSessionId = null) => PostAsync<PublisherSession>("api/odyle/session", new { previousSessionId });
    public Task SetSharingAsync(string roomId, bool enabled) => PostAsync<JsonElement>("api/odyle/sharing", new { roomId, enabled });
    public Task HeartbeatAsync(string sessionId, string? characterId) => PostAsync<JsonElement>("api/odyle/heartbeat", new { sessionId, characterId });
    public Task InvalidateAsync(string characterId, string sessionId) => PostAsync<JsonElement>("api/odyle/invalidate", new { characterId, sessionId });
    public Task LeaveAsync(string roomId) => PostAsync<JsonElement>("api/odyle/leave", new { roomId });
    public async Task<Character> ReportAsync(ManualReading reading)
    {
        if (reading.Value is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(reading), "Odyle must be between 0 and 1,000,000.");
        if (string.IsNullOrWhiteSpace(reading.CharacterName) || string.IsNullOrWhiteSpace(reading.Server)) throw new ArgumentException("Character and server are required.");
        return (await PostAsync<ReportResponse>("api/odyle/report", reading)).Character;
    }
    private async Task<T> GetAsync<T>(string route) { using var response = await client.GetAsync(route); return await ReadAsync<T>(response); }
    private async Task<T> PostAsync<T>(string route, object body) { using var response = await client.PostAsJsonAsync(route, body, Json); return await ReadAsync<T>(response); }
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
