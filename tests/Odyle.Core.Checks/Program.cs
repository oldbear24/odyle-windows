using System.Text.Json;
using Odyle.Core;
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var auth = JsonSerializer.Deserialize<AuthResponse>("""{"token":"sample","record":{"id":"discord-user","displayName":"Player"}}""", options)!;
if (auth.Record.Id != "discord-user" || auth.Record.DisplayName != "Player") throw new Exception("Account pairing contract failed.");
var c = JsonSerializer.Deserialize<Character>("""{"id":"c","user":"discord-user","characterName":"Main","server":"EU","region":"GLOBAL_EU","value":0,"confirmed":true,"valid":false,"lastReadingAt":"2026-10-12 12:00:00.000Z"}""", options)!;
if (c.Value != 0 || !c.Confirmed || c.Valid || !c.Label.Contains("0 Odyle")) throw new Exception("Stored stale zero contract failed.");
using var message = JsonDocument.Parse(JsonSerializer.Serialize(new ManualReading("s", 1, "Main", "EU", "GLOBAL_EU", 0), options));
if (message.RootElement.GetProperty("value").GetInt32() != 0 || message.RootElement.GetProperty("source").GetString() != "manual") throw new Exception("Zero reading wire contract failed.");
try { using var rejected = new PartyApi("http://example.com"); throw new Exception("Insecure remote URL accepted."); } catch (ArgumentException) { }
using var local = new PartyApi("http://127.0.0.1:8090");
try { await local.ReportAsync(new ManualReading("s", 1, "Main", "EU", "GLOBAL_EU", -1)); throw new Exception("Negative value accepted."); } catch (ArgumentOutOfRangeException) { }
if (args.Length == 2)
{
    using var api = new PartyApi(args[0]); var paired = await api.PairAsync(args[1]);
    if ((await api.RefreshAsync()).Record.Id != paired.Record.Id) throw new Exception("Pairing changed account identity.");
    var session = await api.StartSessionAsync();
    var character = await api.ReportAsync(new ManualReading(session.SessionId, 1, "Main", "EU", "GLOBAL_EU", 0));
    await api.HeartbeatAsync(session.SessionId, character.Id); await api.InvalidateAsync(character.Id, session.SessionId);
    if (!(await api.GetCharactersAsync()).Any(c => c.Id == character.Id && c.Value == 0 && c.Confirmed && !c.Valid)) throw new Exception("Character persistence failed.");
    await api.GetRoomsAsync(); Console.WriteLine("Windows account pairing and persistent character API check passed.");
}
Console.WriteLine("Discord account parsing, persistent stale zero, wire contract and URL/value protection passed.");
