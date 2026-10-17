using System.Text.Json;
using Odyle.Core;
PacketChecks.Run();
IdentityChecks.Run();
ChecklistChecks.Run();
LocalizationChecks.Run();
await ConnectionChecks.Run();
DesktopChecks.Run();
SetupChecks.Run();
RealtimeChecks.Run();
ClockChecks.Run();
PartyChecks.Run();
ChangelogChecks.Run();
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
// Release feed: GitHub's latest-release JSON names the packages the updater may fetch. Only
// GitHub download links count, so the duplicate asset pointing elsewhere is dropped.
const string releaseJson = """{"tag_name":"v0.5.0.2","name":"Odyle 0.5.0.2","html_url":"https://github.com/oldbear24/odyle-windows/releases/tag/v0.5.0.2","published_at":"2026-10-13T22:34:09Z","draft":false,"prerelease":false,"assets":[{"name":"odyle-0.5.0.2-win-x64-self-contained.zip","size":71595826,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64-self-contained.zip"},{"name":"odyle-0.5.0.2-win-x64-self-contained.zip.sha256","size":108,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64-self-contained.zip.sha256"},{"name":"odyle-0.5.0.2-win-x64.zip","size":217557,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64.zip"},{"name":"odyle-0.5.0.2-win-x64.zip.sha256","size":93,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64.zip.sha256"},{"name":"odyle-0.5.0.2-win-x64.zip","size":1,"browser_download_url":"http://mirror.example/odyle-0.5.0.2-win-x64.zip"}]}""";
var release = ReleaseFeed.Parse(releaseJson) ?? throw new Exception("Release feed not parsed.");
if (release.Version != new Version(0, 5, 0, 2) || release.Tag != "v0.5.0.2" || release.Url != "https://github.com/oldbear24/odyle-windows/releases/tag/v0.5.0.2" || release.PublishedAt != DateTimeOffset.Parse("2026-10-13T22:34:09Z") || release.Assets.Length != 4) throw new Exception("Release metadata contract failed.");
var small = release.Package("win-x64", false) ?? throw new Exception("Small package not found.");
if (small.Name != "odyle-0.5.0.2-win-x64.zip" || small.Size != 217557 || !small.Url.StartsWith("https://github.com/")) throw new Exception("Small package selection failed.");
if (release.Package("win-x64", true)?.Name != "odyle-0.5.0.2-win-x64-self-contained.zip" || release.Package("win-arm64", false) is not null) throw new Exception("Package kind selection failed.");
if (release.Checksum(small)?.Name != "odyle-0.5.0.2-win-x64.zip.sha256") throw new Exception("Checksum asset lookup failed.");
if (!release.IsNewerThan(new Version(0, 5, 0, 1)) || !release.IsNewerThan(new Version(0, 5, 0)) || release.IsNewerThan(new Version(0, 5, 0, 2)) || release.IsNewerThan(new Version(0, 6, 0, 1))) throw new Exception("Release version comparison failed.");
if (ReleaseFeed.Parse(releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true")) is not null || ReleaseFeed.Parse("""{"tag_name":"nightly","assets":[]}""") is not null || ReleaseFeed.Parse("[]") is not null) throw new Exception("Pre-releases and unversioned tags must never be offered.");
const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
if (ReleaseFeed.ExpectedHash(hash.ToUpperInvariant() + "  odyle-0.5.0.2-win-x64.zip\n", "odyle-0.5.0.2-win-x64.zip") != hash) throw new Exception("Checksum file parsing failed.");
if (ReleaseFeed.ExpectedHash(hash + "  odyle-0.5.0.2-win-x64-self-contained.zip", "odyle-0.5.0.2-win-x64.zip") is not null || ReleaseFeed.ExpectedHash("not-a-hash  odyle-0.5.0.2-win-x64.zip", "odyle-0.5.0.2-win-x64.zip") is not null) throw new Exception("A checksum for another file or a malformed hash was accepted.");
if (!ReleaseFeed.TryParseTag("v0.6.0.15", out var tagged) || tagged != new Version(0, 6, 0, 15) || ReleaseFeed.TryParseTag("v1", out _) || ReleaseFeed.TryParseTag("latest", out _)) throw new Exception("Tag parsing failed.");
if (ReleaseFeed.Describe("odyle-0.5.0.2-win-arm64-self-contained.zip") is not { Runtime: "win-arm64", SelfContained: true } || ReleaseFeed.Describe("odyle-0.5.0.2-win-x64.zip.sha256") is not null) throw new Exception("Package name parsing failed.");
// A release with ARM64 packages and the installer: ARM64 PCs take the ARM64 package of their
// kind, and the setup program is never mistaken for an update package.
string Asset(string name) => $$"""{"name":"{{name}}","size":10,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.6.0.3/{{name}}"}""";
string[] published = ["odyle-0.6.0.3-win-x64-setup.exe", "odyle-0.6.0.3-win-x64-setup.exe.sha256", "odyle-0.6.0.3-win-x64.zip", "odyle-0.6.0.3-win-x64.zip.sha256", "odyle-0.6.0.3-win-x64-self-contained.zip", "odyle-0.6.0.3-win-x64-self-contained.zip.sha256", "odyle-0.6.0.3-win-arm64.zip", "odyle-0.6.0.3-win-arm64.zip.sha256", "odyle-0.6.0.3-win-arm64-self-contained.zip", "odyle-0.6.0.3-win-arm64-self-contained.zip.sha256"];
var full = ReleaseFeed.Parse($$"""{"tag_name":"v0.6.0.3","html_url":"https://github.com/oldbear24/odyle-windows/releases/tag/v0.6.0.3","assets":[{{string.Join(",", published.Select(Asset))}}]}""") ?? throw new Exception("Release with ARM64 packages not parsed.");
if (full.Package("win-arm64", false)?.Name != "odyle-0.6.0.3-win-arm64.zip" || full.Package("win-arm64", true)?.Name != "odyle-0.6.0.3-win-arm64-self-contained.zip" || full.Checksum(full.Package("win-arm64", true)!)?.Name != "odyle-0.6.0.3-win-arm64-self-contained.zip.sha256") throw new Exception("ARM64 package selection failed.");
if (full.Package("win-x64", true)?.Name != "odyle-0.6.0.3-win-x64-self-contained.zip" || full.Package("win-x64", false)?.Name != "odyle-0.6.0.3-win-x64.zip" || ReleaseFeed.Describe("odyle-0.6.0.3-win-x64-setup.exe") is not null || full.Assets.Any(a => ReleaseFeed.Describe(a.Name) is not null && !a.Name.EndsWith(".zip"))) throw new Exception("The installer must never be picked as an update package.");
// Held-back versions (#19): never install one, and move off one this build runs, even to an older
// release; a held-back latest release is never chosen, so a rollback cannot loop.
Version V(string text) => ReleaseFeed.TryParseTag(text, out var parsed) ? parsed : throw new Exception("bad version " + text);
if (ReleaseFeed.Choose(full, V("0.6.0.2"), [], "win-x64", false) != full || ReleaseFeed.Choose(full, V("0.6.0.3"), [], "win-x64", false) is not null || ReleaseFeed.Choose(null, V("0.6.0.2"), [], "win-x64", false) is not null) throw new Exception("Choosing the latest release failed.");
if (ReleaseFeed.Choose(full, V("0.6.0.2"), [V("0.6.0.3")], "win-x64", false) is not null) throw new Exception("A held-back release was chosen.");
if (ReleaseFeed.Choose(full, V("0.7.0"), [V("0.7.0")], "win-x64", false) != full || ReleaseFeed.Choose(full, V("0.7.0"), [], "win-x64", false) is not null) throw new Exception("A held-back build must go back to the latest release, and only then.");
if (ReleaseFeed.Choose(full, V("0.6.0.3"), [V("0.6.0.3")], "win-x64", false) is not null || ReleaseFeed.Choose(full, V("0.7.0"), [V("0.7.0")], "win-x64", true) != full) throw new Exception("Going back needs another release with this machine's package.");
if (!ReleaseFeed.HeldBack(["0.18.0", "v0.18.0", "latest", ""]).SequenceEqual([V("0.18.0")]) || ReleaseFeed.HeldBack(null).Length != 0) throw new Exception("Held-back versions parsing failed.");
if (System.Text.Json.JsonSerializer.Deserialize<WindowsReleaseInfo>("""{"latest":null,"blocked":["0.18.0"]}""", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) is not { Blocked: ["0.18.0"] } || System.Text.Json.JsonSerializer.Deserialize<WindowsReleaseInfo>("""{"latest":null}""") is not { Blocked: null }) throw new Exception("The windows-release answer was not read.");
// The check at start is tried again soon until it reaches GitHub, and what it finds installs at once;
// afterwards the timer looks hourly and checks once a day after GitHub last said this build is current.
var schedule = new UpdateSchedule(); var started = DateTimeOffset.Parse("2026-10-15T08:00:00Z");
if (!schedule.AtStart || !schedule.Due(started) || UpdateSchedule.First != TimeSpan.FromSeconds(5)) throw new Exception("The first update check must run soon after start.");
var retries = new List<TimeSpan>(); for (var i = 0; i < 6; i++) { schedule.Failed(); retries.Add(schedule.Next); }
if (!schedule.AtStart || !retries.Select(r => r.TotalSeconds).SequenceEqual([15, 30, 60, 120, 300, 300])) throw new Exception("A start check without network must be retried soon.");
schedule.Reached(started.AddMinutes(9), upToDate: true);
if (schedule.AtStart || schedule.Next != TimeSpan.FromHours(1) || schedule.Due(started.AddHours(20)) || !schedule.Due(started.AddMinutes(9).AddDays(1))) throw new Exception("After the start check, updates are checked once a day.");
schedule.Failed();
if (schedule.AtStart || schedule.Next != TimeSpan.FromHours(1)) throw new Exception("A later failed check must wait for the hourly tick.");
var found = new UpdateSchedule(); found.Reached(started, upToDate: false);
if (found.AtStart || !found.Due(started.AddHours(1))) throw new Exception("A release found but not installed must be looked for again.");
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
    await RealtimeChecks.RunLive(api, paired.Record.Id, session.SessionId, character.Id);
    if (paired.DeviceKey is { } key)
    {
        // The pairing outlives its token: a request with an expired token signs in with the key and is repeated.
        using var later = new PartyApi(pairing.Endpoint, "expired-token", key);
        await later.GetRoomsAsync();
        if (later.Token is null or "expired-token" || (await later.GetCharactersAsync()).All(c => c.Id != character.Id)) throw new Exception("An expired token did not sign in again with the device key.");
        if ((await later.SignInAsync()).Record.Id != paired.Record.Id) throw new Exception("Device sign-in changed account identity.");
        await later.UnpairAsync();
        using var forgotten = new PartyApi(pairing.Endpoint, null, key);
        try { await forgotten.SignInAsync(); throw new Exception("An unpaired device key still signed in."); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
        Console.WriteLine("Windows lasting pairing check passed.");
    }
}
Console.WriteLine("Connection links, Discord accounts, persistent stale zero, wire contract, release feed, update schedule and URL/value protection passed.");
