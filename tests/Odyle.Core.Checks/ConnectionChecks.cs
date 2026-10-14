using System.Net;
using System.Text;
using System.Text.Json;
using Odyle.Core;

// The wire contract of PartyApi against a stub server: failures always carry their status
// code, a 429 pauses requests without rotating anything, a 401 renews once and a refused
// renewal ends the pairing instead of looping. Also the retry schedule, the log and
// atomic file writes.
static class ConnectionChecks
{
    sealed class Stub(Func<string, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(string Route, string Body, string? Token)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            var route = request.RequestUri!.AbsolutePath; Requests.Add((route, body, request.Headers.Authorization?.Parameter));
            return answer(route, body);
        }
    }
    static HttpResponseMessage Answer(HttpStatusCode status, string body, string type = "application/json") => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    const string Auth = """{"token":"fresh","record":{"id":"u","displayName":"Lumina"}}""";
    public static async Task Run()
    {
        // Retry schedule while the server is down at start, and Retry-After.
        Check(new[] { 0, 1, 2, 3, 4, 9 }.Select(Retry.Backoff).SequenceEqual(new[] { 15, 30, 60, 120, 300, 300 }.Select(s => TimeSpan.FromSeconds(s))), "backoff 15 s, 30 s, 1 min, 2 min, then every 5 min");
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        Check(Retry.After("120", now) == TimeSpan.FromSeconds(120) && Retry.After(" 5 ", now) == TimeSpan.FromSeconds(5), "Retry-After in seconds");
        Check(Retry.After("Wed, 14 Oct 2026 12:01:30 GMT", now) == TimeSpan.FromSeconds(90), "Retry-After as an HTTP date");
        Check(Retry.After("Wed, 14 Oct 2026 11:00:00 GMT", now) == TimeSpan.FromSeconds(1) && Retry.After("0", now) == TimeSpan.FromSeconds(1), "a date already past or zero waits a second");
        Check(Retry.After(null, now) == TimeSpan.FromSeconds(60) && Retry.After("soon", now) == TimeSpan.FromSeconds(60) && Retry.After("-5", now) == TimeSpan.FromSeconds(60), "missing or unusable Retry-After waits a minute");
        Check(Retry.After("86400", now) == TimeSpan.FromHours(1), "a wait is at most an hour");

        // Error bodies of any shape become HttpRequestException with the status code.
        async Task<HttpRequestException> Fails(HttpResponseMessage response)
        {
            using var api = new PartyApi("https://party.example", "token", null, new Stub((_, _) => response));
            try { await api.GetRoomsAsync(); } catch (HttpRequestException error) { return error; }
            throw new Exception("Connection check failed: a failing answer was accepted.");
        }
        Check(await Fails(Answer(HttpStatusCode.BadRequest, """{"message":"Outdated reading.","data":{}}""")) is { StatusCode: HttpStatusCode.BadRequest, Message: "Outdated reading." }, "the server's message is kept");
        Check(await Fails(Answer(HttpStatusCode.InternalServerError, """{"code":500,"data":{}}""")) is { StatusCode: HttpStatusCode.InternalServerError, Message: "Server returned 500." }, "JSON without a message");
        Check(await Fails(Answer(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html")) is { StatusCode: HttpStatusCode.BadGateway, Message: "Server returned 502." }, "a body that is not JSON");
        Check(await Fails(Answer(HttpStatusCode.ServiceUnavailable, "")) is { StatusCode: HttpStatusCode.ServiceUnavailable }, "an empty error body");
        Check(await Fails(Answer(HttpStatusCode.Forbidden, """{"message":42}""")) is { StatusCode: HttpStatusCode.Forbidden, Message: "Server returned 403." }, "a message that is not text");
        Check(await Fails(Answer(HttpStatusCode.OK, "null")) is { StatusCode: HttpStatusCode.OK }, "a null success body");
        Check(await Fails(Answer(HttpStatusCode.OK, "")) is { StatusCode: HttpStatusCode.OK }, "an empty success body");
        Check(await Fails(Answer(HttpStatusCode.OK, "<html></html>", "text/html")) is { StatusCode: HttpStatusCode.OK }, "a success body that is not JSON");

        // 429: requests pause until Retry-After without reaching the server, and nothing renews or rotates.
        var busy = new Stub((_, _) => { var r = Answer(HttpStatusCode.TooManyRequests, """{"message":"Too Many Requests."}"""); r.Headers.TryAddWithoutValidation("Retry-After", "120"); return r; });
        using (var api = new PartyApi("https://party.example", "token", "key", busy))
        {
            var before = DateTimeOffset.UtcNow;
            try { await api.HeartbeatAsync("s", null); throw new Exception("Connection check failed: 429 accepted."); }
            catch (ServerBusyException error) { Check(error.StatusCode == HttpStatusCode.TooManyRequests && error.Until >= before.AddSeconds(119) && error.Until <= DateTimeOffset.UtcNow.AddSeconds(121) && api.BusyUntil == error.Until, "a 429 pauses for its Retry-After"); }
            try { await api.StartSessionAsync("s"); throw new Exception("Connection check failed: a paused request was sent."); }
            catch (ServerBusyException) { Check(busy.Requests.Count == 1 && !api.PairingEnded, "paused requests never reach the server and do not end the pairing"); }
        }

        // 401 → one sign-in with the device key → the request is repeated with the fresh token.
        var expired = true;
        var renewal = new Stub((route, _) =>
        {
            if (route == "/api/odyle/desktop-sign-in") { expired = false; return Answer(HttpStatusCode.OK, Auth); }
            return expired ? Answer(HttpStatusCode.Unauthorized, """{"message":"The request requires valid record authorization token."}""") : Answer(HttpStatusCode.OK, """{"rooms":[],"odylePerCube":40}""");
        });
        using (var api = new PartyApi("https://party.example", "old", "key", renewal))
        {
            string? signedInAs = null; api.SignedIn += auth => signedInAs = auth.Record.DisplayName;
            await api.GetRoomsAsync();
            Check(renewal.Requests.Select(r => r.Route).SequenceEqual(["/api/odyle/rooms", "/api/odyle/desktop-sign-in", "/api/odyle/rooms"]) && renewal.Requests[2].Token == "fresh" && api.Token == "fresh", "a 401 signs in once and repeats the request");
            Check(signedInAs == "Lumina", "every sign-in answer refreshes the account's display name");
            using var signIn = JsonDocument.Parse(renewal.Requests[1].Body);
            Check(signIn.RootElement.GetProperty("deviceKey").GetString() == "key" && signIn.RootElement.GetProperty("name").GetString() == PartyApi.DeviceName, "device sign-in names this PC");
        }

        // A PC removed on the dashboard: its session request answers 401, the sign-in with the
        // forgotten key answers 401 too, and the pairing ends instead of looping.
        var removed = new Stub((route, _) => Answer(HttpStatusCode.Unauthorized, """{"message":"This PC is no longer paired with an account."}"""));
        using (var api = new PartyApi("https://party.example", "token", "key", removed))
        {
            try { await api.StartSessionAsync("old-session"); throw new Exception("Connection check failed: a removed PC started a session."); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
            Check(removed.Requests.Select(r => r.Route).SequenceEqual(["/api/odyle/session", "/api/odyle/desktop-sign-in"]) && api.PairingEnded, "a refused sign-in ends the pairing after one attempt");
            using var session = JsonDocument.Parse(removed.Requests[0].Body);
            Check(session.RootElement.GetProperty("previousSessionId").GetString() == "old-session" && session.RootElement.GetProperty("deviceKey").GetString() == "key", "a session request names the previous session and the device key");
            foreach (var request in new Func<Task>[] { () => api.HeartbeatAsync("s", null), () => api.GetRoomsAsync(), () => api.SignInAsync() })
            {
                try { await request(); throw new Exception("Connection check failed: an ended pairing sent a request."); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
            }
            Check(removed.Requests.Count == 2, "an ended pairing never contacts the server again");
        }
        // An expired publisher session (403) is reported as such; it is no reason to renew the token.
        var forbidden = new Stub((_, _) => Answer(HttpStatusCode.Forbidden, """{"message":"Publisher session expired. Reconnect."}"""));
        using (var api = new PartyApi("https://party.example", "token", "key", forbidden))
        {
            try { await api.HeartbeatAsync("s", "c"); } catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Forbidden && error.Message == "Publisher session expired. Reconnect." && forbidden.Requests.Count == 1 && !api.PairingEnded, "403 is not a lost pairing"); }
        }
        // Pairing and the device key request name this PC; a session without key or previous session sends neither.
        var pairing = new Stub((route, _) => route switch
        {
            "/api/odyle/pair" => Answer(HttpStatusCode.OK, """{"token":"t","record":{"id":"u","displayName":"Lumina"}}"""),
            "/api/odyle/desktop-device" => Answer(HttpStatusCode.OK, """{"deviceKey":"k"}"""),
            _ => Answer(HttpStatusCode.OK, """{"sessionId":"s","sequence":0}"""),
        });
        using (var api = new PartyApi("https://party.example", null, null, pairing))
        {
            await api.PairAsync("ABCDEFGH23456789"); await api.StartSessionAsync(); await api.EnsureDeviceKeyAsync(); await api.StartSessionAsync();
            using var pair = JsonDocument.Parse(pairing.Requests[0].Body);
            Check(pair.RootElement.GetProperty("code").GetString() == "ABCDEFGH23456789" && pair.RootElement.GetProperty("name").GetString() == PartyApi.DeviceName, "pairing sends the code and this PC's name");
            Check(pairing.Requests[1].Body == "{}" && JsonDocument.Parse(pairing.Requests[2].Body).RootElement.GetProperty("name").GetString() == PartyApi.DeviceName && pairing.Requests[3].Body == """{"deviceKey":"k"}""", "absent session fields are left out; the device request names this PC");
        }
        // The app names its version with each session; the rooms answer carries the server's announcement.
        var versioned = new Stub((route, _) => route == "/api/odyle/rooms" ? Answer(HttpStatusCode.OK, """{"rooms":[],"odylePerCube":40,"announcement":{"text":"Maintenance at 10:00 UTC","level":"warning"}}""") : Answer(HttpStatusCode.OK, """{"sessionId":"s","sequence":0}"""));
        using (var api = new PartyApi("https://party.example", "token", null, versioned) { AppVersion = "0.6.0.12" })
        {
            await api.StartSessionAsync(); var listed = await api.GetRoomsAsync();
            using var session = JsonDocument.Parse(versioned.Requests[0].Body);
            Check(session.RootElement.GetProperty("appVersion").GetString() == "0.6.0.12", "a session request names the app version");
            Check(listed.Announcement is { Text: "Maintenance at 10:00 UTC", IsWarning: true }, "the rooms answer carries the server's announcement");
        }
        Check(JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Announcement is null, "a backend without announcements answers none");
        Check(PartyApi.NameOf("  DESKTOP-ODYLE  ") == "DESKTOP-ODYLE" && PartyApi.NameOf(new string('x', 55)).Length == 40 && PartyApi.NameOf("") == "Windows PC" && PartyApi.DeviceName.Length is > 0 and <= 40, "device names are trimmed to 40 characters");

        // The log rotates at its limit, keeps one previous file, takes writes from many threads and masks secrets.
        var folder = Directory.CreateTempSubdirectory("odyle-log-check-").FullName;
        try
        {
            var log = new RotatingLog(Path.Combine(folder, "odyle.log"), limit: 4096);
            Parallel.For(0, 200, i => log.Write("INFO", $"line {i:000} " + new string('x', 40)));
            var current = File.ReadAllLines(log.FilePath); var previous = File.ReadAllLines(log.PreviousPath);
            Check(new FileInfo(log.FilePath).Length <= 4096 && Directory.GetFiles(folder).Length == 2 && current.Concat(previous).All(l => l.Contains(" INFO  line ") && l.EndsWith(new string('x', 40))), "the log rotates into one previous file without torn lines");
            const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJpZCI6InUiLCJ0eXBlIjoiYXV0aCJ9.c2lnbmF0dXJlLXNpZ25hdHVyZQ";
            var redacted = RotatingLog.Redact($"token {jwt} key q8Wm2Lz0Pa9XcV4bN7kR1tY6uI3oE5sD8fG2hJ0kL4zX7cV9 link https://party.example/#odyle-pair=ABCDEFGH23456789 code ABCD-EFGH-2345-6789 Authorization: Bearer abc.def");
            Check(!redacted.Contains("eyJ") && !redacted.Contains("q8Wm2") && !redacted.Contains("ABCDEFGH23456789") && !redacted.Contains("ABCD-EFGH") && !redacted.Contains("abc.def") && redacted.Contains("[token]") && redacted.Contains("[secret]") && redacted.Contains("odyle-pair=[code]"), "tokens, device keys and pairing codes never reach the log");
            Check(RotatingLog.Redact("Odyle 0.5.0.12 · session abcdefghij12345 · DispatcherUnhandledExceptionEventArgs") == "Odyle 0.5.0.12 · session abcdefghij12345 · DispatcherUnhandledExceptionEventArgs", "ordinary text is kept");
            // Atomic writes leave the old file or the new one, never a temporary file.
            var target = Path.Combine(folder, "preferences.json");
            AtomicFile.WriteText(target, "{\"a\":1}"); AtomicFile.WriteText(target, "{\"a\":2}");
            Check(File.ReadAllText(target) == "{\"a\":2}" && !Directory.GetFiles(folder, "*.tmp").Any(), "atomic writes replace the file and leave no temporary file");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Failure bodies, 429 pause with Retry-After, renewal without loops, ended pairings, device names, retry schedule, log rotation and redaction passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Connection check failed: " + message); }
}
