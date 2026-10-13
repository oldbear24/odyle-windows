using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odyle.Core;
public sealed record User(string Id, string DisplayName);
public sealed record AuthResponse(string Token, User Record);
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
    public Task<RoomsResponse> GetRoomsAsync() => GetAsync<RoomsResponse>("api/odyle/rooms");
    public async Task<Character[]> GetCharactersAsync() => (await GetAsync<RecordList<Character>>("api/collections/characters/records?perPage=500&sort=created")).Items;
    public Task<PublisherSession> StartSessionAsync(string? previousSessionId = null) => PostAsync<PublisherSession>("api/odyle/session", new { previousSessionId });
    public Task<RoomSnapshot> GetSnapshotAsync(string roomId) => GetAsync<RoomSnapshot>("api/odyle/snapshot/" + Uri.EscapeDataString(roomId));
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
