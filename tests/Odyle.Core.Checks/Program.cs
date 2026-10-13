using System.Text.Json;
using Odyle.Core;
PacketChecks.Run();
IdentityChecks.Run();
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var auth = JsonSerializer.Deserialize<AuthResponse>("""{"token":"sample","record":{"id":"discord-user","displayName":"Player"}}""", options)!;
if (auth.Record.Id != "discord-user" || auth.Record.DisplayName != "Player") throw new Exception("Account pairing contract failed.");
var c = JsonSerializer.Deserialize<Character>("""{"id":"c","user":"discord-user","characterName":"Main","server":"EU","region":"GLOBAL_EU","value":0,"confirmed":true,"valid":false,"lastReadingAt":"2026-10-12 12:00:00.000Z"}""", options)!;
if (c.Value != 0 || !c.Confirmed || c.Valid || !c.Label.Contains("0 Odyle")) throw new Exception("Stored stale zero contract failed.");
using var message = JsonDocument.Parse(JsonSerializer.Serialize(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", 0, "test-fixture-v1"), options));
if (message.RootElement.GetProperty("value").GetInt32() != 0 || message.RootElement.GetProperty("source").GetString() != "packet") throw new Exception("Zero reading wire contract failed.");
try { using var rejected = new PartyApi("http://example.com"); throw new Exception("Insecure remote URL accepted."); } catch (ArgumentException) { }
using var local = new PartyApi("http://127.0.0.1:8090");
const string pairingCode = "ABCDEFGH23456789";
var connection = ConnectionDetails.Parse("https://party.example/pocketbase/#odyle-pair=" + pairingCode, "http://127.0.0.1:8090");
if (connection.Endpoint != "https://party.example/pocketbase" || connection.Code != pairingCode) throw new Exception("Connection link did not select its backend.");
var legacy = ConnectionDetails.Parse("abcd efgh 2345 6789", "https://party.example/");
if (legacy.Endpoint != "https://party.example" || legacy.Code != pairingCode) throw new Exception("Code-only pairing failed.");
foreach (var unsafeLink in new[] { "http://party.example/#odyle-pair=" + pairingCode, "https://user:secret@party.example/#odyle-pair=" + pairingCode, "https://party.example/?token=x#odyle-pair=" + pairingCode, "https://party.example/#odyle-pair=ROOMINVITE12", "file:///C:/app#odyle-pair=" + pairingCode })
{
    try { ConnectionDetails.Parse(unsafeLink, "http://127.0.0.1:8090"); throw new Exception("Unsafe or invalid connection link accepted."); } catch (ArgumentException) { }
}
try { await local.ReportAsync(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", -1, "test-fixture-v1")); throw new Exception("Negative value accepted."); } catch (ArgumentOutOfRangeException) { }
try { await local.ReportAsync(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", 100, " ")); throw new Exception("Blank adapter accepted."); } catch (ArgumentException) { }
try { await local.ReportAsync(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", 100, "test-fixture-v1", Source: "manual")); throw new Exception("Manual source accepted."); } catch (ArgumentException) { }
if (ExperimentalOdyleDecoder.Version != "global-reference-v1") throw new Exception("Adapter name must match the backend registry.");
if (args.Length is 1 or 2)
{
    var pairing = ConnectionDetails.Parse(args[^1], args.Length == 2 ? args[0] : "http://127.0.0.1:8090");
    using var api = new PartyApi(pairing.Endpoint); var paired = await api.PairAsync(pairing.Code);
    if ((await api.RefreshAsync()).Record.Id != paired.Record.Id) throw new Exception("Pairing changed account identity.");
    var session = await api.StartSessionAsync();
    var saved = await api.SaveCharacterAsync(" Main ", " EU ", "GLOBAL_EU");
    if (saved.CharacterName != "Main" || saved.Server != "EU" || saved.User != paired.Record.Id) throw new Exception("Saving character identity failed.");
    var character = await api.ReportAsync(new PacketReading(session.SessionId, 1, "Main", "EU", "GLOBAL_EU", 0, "test-fixture-v1"));
    if (saved.Id != character.Id) throw new Exception("First game reading duplicated the saved character.");
    await api.HeartbeatAsync(session.SessionId, character.Id); await api.InvalidateAsync(character.Id, session.SessionId);
    if (!(await api.GetCharactersAsync()).Any(c => c.Id == character.Id && c.Value == 0 && c.Confirmed && !c.Valid)) throw new Exception("Character persistence failed.");
    await api.GetRoomsAsync(); Console.WriteLine("Windows account pairing and persistent character API check passed.");
}
Console.WriteLine("Connection links, Discord accounts, persistent stale zero, wire contract and URL/value protection passed.");
