using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odyle.Core;

public sealed record Member(string Id, string Room, string Role, string DisplayName, string CharacterName, string Server, string Region, int Value, bool Confirmed, bool Sharing, long Sequence);
public sealed record Room(string Id, string Name, int Cost);
public sealed record AuthResponse(string Token, Member Record, Room? Room, string? SessionId);
public sealed record PublisherSession(string SessionId, long Sequence);
public sealed record ManualReading(string SessionId, long Sequence, string CharacterName, string Server, string Region, int Value, string Source = "manual", bool Confirmed = true);
public sealed class PartyApi : IDisposable
{
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public PartyApi(string endpoint, string? token = null)
    {
        var uri = new Uri(endpoint.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Use HTTPS for hosted servers. HTTP is allowed only on localhost.");
        client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(12) };
        if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task<AuthResponse> JoinAsync(string code, string displayName)
    {
        var result = await PostAsync<AuthResponse>("api/odyle/join", new { code, displayName });
        if (result.Record.Role == "viewer")
        {
            // Revoke the accidentally created viewer credential; readers require publisher access.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
            await PostAsync<JsonElement>("api/odyle/leave", new { });
            throw new InvalidOperationException("This is a viewer invite. Ask your leader for a player invite.");
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
        return result;
    }
    public Task<AuthResponse> RefreshAsync() => PostAsync<AuthResponse>("api/collections/members/auth-refresh", new { });
    public Task<PublisherSession> StartSessionAsync() => PostAsync<PublisherSession>("api/odyle/session", new { });
    public Task SetSharingAsync(bool enabled) => PostAsync<JsonElement>("api/odyle/sharing", new { enabled });
    public Task HeartbeatAsync() => PostAsync<JsonElement>("api/odyle/heartbeat", new { });
    public Task InvalidateAsync() => PostAsync<JsonElement>("api/odyle/invalidate", new { });
    public Task LeaveAsync() => PostAsync<JsonElement>("api/odyle/leave", new { });
    public async Task ReportAsync(ManualReading reading)
    {
        if (reading.Value is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(reading), "Odyle must be between 0 and 1,000,000.");
        if (string.IsNullOrWhiteSpace(reading.CharacterName) || string.IsNullOrWhiteSpace(reading.Server)) throw new ArgumentException("Character and server are required.");
        await PostAsync<JsonElement>("api/odyle/report", reading);
    }
    private async Task<T> PostAsync<T>(string route, object body)
    {
        using var response = await client.PostAsJsonAsync(route, body, Json);
        if (!response.IsSuccessStatusCode)
        {
            string message = "Server returned " + (int)response.StatusCode + ".";
            try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); message = doc.RootElement.GetProperty("message").GetString() ?? message; }
            catch (JsonException) { }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidOperationException("The server returned an empty response.");
    }
    public void Dispose() => client.Dispose();
}
