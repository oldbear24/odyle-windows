using System.Text.Json;
using Odyle.Core;

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var input = """{"token":"sample","record":{"id":"abc","room":"r","role":"reporter","displayName":"Player","characterName":"Player","server":"EU","region":"GLOBAL_EU","value":0,"confirmed":false,"sharing":false,"sequence":0},"sessionId":"s","room":{"id":"r","name":"Party","cost":40}}""";
var auth = JsonSerializer.Deserialize<AuthResponse>(input, options)!;
if (auth.Record.Value != 0 || auth.Record.Confirmed || auth.Room?.Cost != 40 || auth.SessionId != "s") throw new Exception("PocketBase response contract failed.");
var json = JsonSerializer.Serialize(new ManualReading("s", 1, "Player", "EU", "GLOBAL_EU", 0), options);
using var message = JsonDocument.Parse(json);
if (message.RootElement.GetProperty("value").GetInt32() != 0 || message.RootElement.GetProperty("source").GetString() != "manual" || !message.RootElement.GetProperty("confirmed").GetBoolean()) throw new Exception("Zero reading wire contract failed.");
try { using var rejected = new PartyApi("http://example.com"); throw new Exception("Insecure remote URL accepted."); } catch (ArgumentException) { }
using var local = new PartyApi("http://127.0.0.1:8090");
try { await local.ReportAsync(new ManualReading("s", 1, "Player", "EU", "GLOBAL_EU", -1)); throw new Exception("Negative value accepted."); } catch (ArgumentOutOfRangeException) { }
if (args.Length == 2)
{
    using var api = new PartyApi(args[0]);
    var joined = await api.JoinAsync(args[1], "Windows contract check");
    var session = await api.StartSessionAsync();
    await api.SetSharingAsync(true);
    await api.ReportAsync(new ManualReading(session.SessionId, 1, "WindowsCheck", "EU", "GLOBAL_EU", 0));
    await api.HeartbeatAsync(); await api.InvalidateAsync(); await api.LeaveAsync();
    Console.WriteLine("Windows client end-to-end API check passed.");
}
Console.WriteLine("PocketBase auth parsing, zero serialization, URL protection and value validation passed.");
