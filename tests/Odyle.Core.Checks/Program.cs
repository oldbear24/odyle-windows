using System.Text.Json;
using Odyle.Core;
PacketChecks.Run();
IdentityChecks.Run();
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var auth = JsonSerializer.Deserialize<AuthResponse>("""{"token":"sample","record":{"id":"discord-user","displayName":"Player"}}""", options)!;
if (auth.Record.Id != "discord-user" || auth.Record.DisplayName != "Player") throw new Exception("Account pairing contract failed.");
var c = JsonSerializer.Deserialize<Character>("""{"id":"c","user":"discord-user","characterName":"Main","server":"EU","region":"GLOBAL_EU","value":0,"confirmed":true,"valid":false,"lastReadingAt":"2026-10-12 12:00:00.000Z"}""", options)!;
if (c.Value != 0 || !c.Confirmed || c.Valid || !c.Label.Contains("0 Odyle")) throw new Exception("Stored stale zero contract failed.");
// Room snapshot as the backend sends it: PocketBase record dates, extra fields, ISO server time.
var snapshot = JsonSerializer.Deserialize<RoomSnapshot>("""{"room":{"id":"r","name":"Evening","owner":"u","cost":40,"collectionName":"party_rooms"},"serverNow":"2026-10-12T12:00:00.000Z","members":[{"id":"m","user":"l","room":"r","role":"reporter","displayName":"Lumina","sharing":true,"sharedCharacters":["c"],"characters":[{"id":"c","user":"l","characterName":"Lumina","server":"Lumiel","region":"GLOBAL_EU","value":180,"confirmed":true,"valid":true,"source":"packet","shared":true,"lastReadingAt":"2026-10-12 11:00:00.000Z","lastSeenAt":"2026-10-12 11:59:50.000Z","parserVersion":"global-reference-v1"}]}]}""", options)!;
var mate = snapshot.Members.Single().Characters.Single(); var serverNow = Readiness.Parse(snapshot.ServerNow)!.Value;
if (snapshot.Room.Cost != 40 || snapshot.Members[0].DisplayName != "Lumina" || mate.Value != 180) throw new Exception("Room snapshot contract failed.");
// Presence follows the reader's heartbeat, not the age of the value: same thresholds as the dashboard.
if (Readiness.Of(mate, serverNow) != Presence.Live || Readiness.Of(mate, serverNow.AddSeconds(40)) != Presence.Stale || Readiness.Of(mate, serverNow.AddSeconds(80)) != Presence.Offline) throw new Exception("Presence thresholds failed.");
if (Readiness.Of(mate with { Valid = false }, serverNow) != Presence.Stale || Readiness.Of(mate with { Confirmed = false }, serverNow) != Presence.Unknown || Readiness.Of(mate with { LastSeenAt = "" }, serverNow) != Presence.Offline) throw new Exception("Invalidated, unknown or absent readings were shown as live.");
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
