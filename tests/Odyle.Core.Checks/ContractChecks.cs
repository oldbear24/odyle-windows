using System.Text.Json;
using Odyle.Core;

// The wire contracts with the backend: pairing and room answers, presence, readings, connection and
// Open in Odyle links, and the values and URLs the API refuses before sending anything.
static class ContractChecks
{
    public static async Task Run()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var auth = JsonSerializer.Deserialize<AuthResponse>("""{"token":"sample","record":{"id":"discord-user","displayName":"Player"}}""", options)!;
        if (auth.Record.Id != "discord-user" || auth.Record.DisplayName != "Player" || auth.DeviceKey is not null) throw new Exception("Account pairing contract failed.");
        if (JsonSerializer.Deserialize<AuthResponse>("""{"token":"sample","record":{"id":"discord-user","displayName":"Player"},"deviceKey":"key"}""", options)!.DeviceKey != "key") throw new Exception("Pairing must keep the device key.");
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
        // Open in Odyle: odyle://pair?link=<the connection link>, checked like a pasted link; nothing else.
        var opened = ConnectionDetails.FromAppLink("odyle://pair?link=" + Uri.EscapeDataString("https://party.example/pocketbase/#odyle-pair=" + pairingCode));
        if (opened.Endpoint != "https://party.example/pocketbase" || opened.Code != pairingCode) throw new Exception("Open in Odyle link did not select its backend.");
        if (StartupCommand.PairLinkOf(new[] { "--pair", "odyle://pair?link=x" }) != "odyle://pair?link=x" || StartupCommand.PairLinkOf(new[] { "--tray" }) is not null) throw new Exception("--pair argument not read.");
        foreach (var refused in new[] { "odyle://pair?link=" + Uri.EscapeDataString("http://party.example/#odyle-pair=" + pairingCode), "odyle://other?link=" + Uri.EscapeDataString("https://party.example/#odyle-pair=" + pairingCode), "https://party.example/#odyle-pair=" + pairingCode, "odyle://pair", "odyle://pair?link=" + pairingCode, "odyle://pair/extra?link=" + Uri.EscapeDataString("https://party.example/#odyle-pair=" + pairingCode) })
        {
            try { ConnectionDetails.FromAppLink(refused); throw new Exception("Unsafe or invalid Open in Odyle link accepted: " + refused); } catch (ArgumentException) { }
        }
        try { await local.ReportAsync(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", -1, "test-fixture-v1")); throw new Exception("Negative value accepted."); } catch (ArgumentOutOfRangeException) { }
        try { await local.ReportAsync(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", 100, " ")); throw new Exception("Blank adapter accepted."); } catch (ArgumentException) { }
        try { await local.ReportAsync(new PacketReading("s", 1, "Main", "EU", "GLOBAL_EU", 100, "test-fixture-v1", Source: "manual")); throw new Exception("Manual source accepted."); } catch (ArgumentException) { }
        // The backend lists global-reference-v2 (and v1 for apps not yet updated) in backend/odyle/protocols.go.
        if (ExperimentalOdyleDecoder.Version != "global-reference-v2" || ExperimentalOdyleDecoder.OdyleResource != 60000001) throw new Exception("Adapter name must match the backend registry.");
        Console.WriteLine("Connection links, Discord accounts, persistent stale zero, wire contract and URL/value protection passed.");
    }
}
