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
        Check(Retry.After("120", now) == TimeSpan.FromSeconds(120), Retry.After(" 5 ", now) == TimeSpan.FromSeconds(5), "Retry-After in seconds");
        Check(Retry.After("Wed, 14 Oct 2026 12:01:30 GMT", now) == TimeSpan.FromSeconds(90), "Retry-After as an HTTP date");
        Check(Retry.After("Wed, 14 Oct 2026 11:00:00 GMT", now) == TimeSpan.FromSeconds(1), Retry.After("0", now) == TimeSpan.FromSeconds(1), "a date already past or zero waits a second");
        Check(Retry.After(null, now) == TimeSpan.FromSeconds(60), Retry.After("soon", now) == TimeSpan.FromSeconds(60), Retry.After("-5", now) == TimeSpan.FromSeconds(60), "missing or unusable Retry-After waits a minute");
        Check(Retry.After("86400", now) == TimeSpan.FromHours(1), "a wait is at most an hour");

        // Error bodies of any shape become HttpRequestException with the status code.
        async Task<HttpRequestException> Fails(HttpResponseMessage response)
        {
            using var api = new PartyApi("https://party.example", "token", null, new Stub((_, _) => response));
            try { await api.GetRoomsAsync(); } catch (HttpRequestException error) { return error; }
            throw new Exception("Connection check failed: a failing answer was accepted.");
        }
        Check(await Fails(Answer(HttpStatusCode.BadRequest, """{"message":"Outdated reading.","data":{}}""")) is { StatusCode: HttpStatusCode.BadRequest, Message: "Outdated reading." }, "the server's message is kept");
        // The backend's refusal of an adapter it does not list arrives word for word, so the app can tell it needs an update.
        Check(await Fails(Answer(HttpStatusCode.BadRequest, """{"message":"This packet adapter has not been validated for Global. No reading was saved.","data":{}}""")) is { StatusCode: HttpStatusCode.BadRequest, Message: PartyApi.AdapterRefused }, "the adapter refusal is recognised");
        Check(await Fails(Answer(HttpStatusCode.InternalServerError, """{"code":500,"data":{}}""")) is { StatusCode: HttpStatusCode.InternalServerError, Message: "Server returned 500." }, "JSON without a message");
        Check(await Fails(Answer(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html")) is { StatusCode: HttpStatusCode.BadGateway, Message: "Server returned 502." }, "a body that is not JSON");
        Check(await Fails(Answer(HttpStatusCode.ServiceUnavailable, "")) is { StatusCode: HttpStatusCode.ServiceUnavailable }, "an empty error body");
        Check(await Fails(Answer(HttpStatusCode.Forbidden, """{"message":42}""")) is { StatusCode: HttpStatusCode.Forbidden, Message: "Server returned 403." }, "a message that is not text");
        Check(await Fails(Answer(HttpStatusCode.OK, "null")) is { StatusCode: HttpStatusCode.OK }, "a null success body");
        Check(await Fails(Answer(HttpStatusCode.OK, "")) is { StatusCode: HttpStatusCode.OK }, "an empty success body");
        Check(await Fails(Answer(HttpStatusCode.OK, "<html></html>", "text/html")) is { StatusCode: HttpStatusCode.OK }, "a success body that is not JSON");

        // The regions and game servers a backend lists: from dashboard 0.59.0 each region's servers with their ids,
        // which also says it knows the Global servers' names; older ones list names only, or not even NA-West.
        async Task<ServerRegions> Servers(string answer)
        {
            using var api = new PartyApi("https://party.example", "token", null, new Stub((_, _) => Answer(HttpStatusCode.OK, answer)));
            return await api.GetServersAsync();
        }
        var listing = await Servers("""{"regions":[{"region":"GLOBAL_NAW","elyos":["Siel"],"asmodian":[],"servers":[{"id":1201,"name":"Siel"}]},{"region":"GLOBAL_EU","elyos":["Siel","Ozryn"],"asmodian":[],"servers":[{"id":1301,"name":"Siel"},{"id":1324,"name":"Ozryn"}]}]}""");
        Check(listing.NaWest, listing.GlobalServers, listing.Servers.SequenceEqual(new[] { (1201, "Siel"), (1301, "Siel"), (1324, "Ozryn") }), "a backend's regions and servers with their ids");
        var older = await Servers("""{"regions":[{"region":"GLOBAL_NAW","elyos":["Siel","Poeta"],"asmodian":["Israphel","Agnita"]}]}""");
        Check(older.NaWest, !older.GlobalServers, !older.Servers.Any(), "a backend from before the Global servers' names");
        var oldest = await Servers("{}");
        Check(!oldest.NaWest && !oldest.GlobalServers && !oldest.Servers.Any() && !ServerRegions.None.NaWest && !ServerRegions.None.GlobalServers, "an answer without regions knows neither");

        // 429: requests pause until Retry-After without reaching the server, and nothing renews or rotates.
        var busy = new Stub((_, _) => { var r = Answer(HttpStatusCode.TooManyRequests, """{"message":"Too Many Requests."}"""); r.Headers.TryAddWithoutValidation("Retry-After", "120"); return r; });
        using (var api = new PartyApi("https://party.example", "token", "key", busy))
        {
            var before = DateTimeOffset.UtcNow;
            try { await api.HeartbeatAsync("s", null); throw new Exception("Connection check failed: 429 accepted."); }
            catch (ServerBusyException error) { Check(error.StatusCode == HttpStatusCode.TooManyRequests, error.Until >= before.AddSeconds(119), error.Until <= DateTimeOffset.UtcNow.AddSeconds(121), api.BusyUntil == error.Until, "a 429 pauses for its Retry-After"); }
            try { await api.StartSessionAsync("s"); throw new Exception("Connection check failed: a paused request was sent."); }
            catch (ServerBusyException) { Check(busy.Requests.Count == 1, !api.PairingEnded, "paused requests never reach the server and do not end the pairing"); }
        }

        // 401 → one sign-in with the device key → the request is repeated with the fresh token.
        var expired = true;
        var renewal = new Stub((route, _) =>
        {
            if (route == "/api/odyle/desktop-sign-in") { expired = false; return Answer(HttpStatusCode.OK, Auth); }
            return expired ? Answer(HttpStatusCode.Unauthorized, """{"status":401,"message":"The request requires valid record authorization token."}""") : Answer(HttpStatusCode.OK, """{"rooms":[],"odylePerCube":40}""");
        });
        using (var api = new PartyApi("https://party.example", "old", "key", renewal))
        {
            string? signedInAs = null; api.SignedIn += auth => signedInAs = auth.Record.DisplayName;
            await api.GetRoomsAsync();
            Check(renewal.Requests.Select(r => r.Route).SequenceEqual(["/api/odyle/rooms", "/api/odyle/desktop-sign-in", "/api/odyle/rooms"]), renewal.Requests[2].Token == "fresh", api.Token == "fresh", "a 401 signs in once and repeats the request");
            Check(signedInAs == "Lumina", "every sign-in answer refreshes the account's display name");
            using var signIn = JsonDocument.Parse(renewal.Requests[1].Body);
            Check(signIn.RootElement.GetProperty("deviceKey").GetString() == "key", signIn.RootElement.GetProperty("name").GetString() == PartyApi.DeviceName, "device sign-in names this PC");
        }

        await SignInChecks();
        // A PC removed on the dashboard: its session request answers 401, the sign-in with the
        // forgotten key answers 401 too, and the pairing ends instead of looping.
        var removed = new Stub((route, _) => Answer(HttpStatusCode.Unauthorized, """{"status":401,"message":"This PC is no longer paired with an account."}"""));
        using (var api = new PartyApi("https://party.example", "token", "key", removed))
        {
            try { await api.StartSessionAsync("old-session"); throw new Exception("Connection check failed: a removed PC started a session."); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
            Check(removed.Requests.Select(r => r.Route).SequenceEqual(["/api/odyle/session", "/api/odyle/desktop-sign-in"]), api.PairingEnded, "a refused sign-in ends the pairing after one attempt");
            using var session = JsonDocument.Parse(removed.Requests[0].Body);
            Check(session.RootElement.GetProperty("previousSessionId").GetString() == "old-session", session.RootElement.GetProperty("deviceKey").GetString() == "key", "a session request names the previous session and the device key");
            foreach (var request in new Func<Task>[] { () => api.HeartbeatAsync("s", null), () => api.GetRoomsAsync(), () => api.SignInAsync() })
            {
                try { await request(); throw new Exception("Connection check failed: an ended pairing sent a request."); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
            }
            Check(removed.Requests.Count == 2, "an ended pairing never contacts the server again");
        }
        // A 401 that is not the server's own (a sign-in page or an auth proxy in front of it) is a
        // server that cannot be reached: the pairing is kept and the app tries again.
        foreach (var (foreign, type) in new[] { ("<!doctype html><title>Sign in</title>", "text/html"), ("""{"error":"unauthorized"}""", "application/json"), ("", "text/plain") })
        {
            var proxy = new Stub((_, _) => Answer(HttpStatusCode.Unauthorized, foreign, type));
            using var api = new PartyApi("https://party.example", "token", "key", proxy);
            try { await api.GetRoomsAsync(); throw new Exception("Connection check failed: a foreign 401 was taken as an answer."); }
            catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.BadGateway, !api.PairingEnded, "a 401 from something in front of the server keeps the pairing: " + type); }
            try { await api.SignInAsync(); throw new Exception("Connection check failed: a foreign 401 signed in."); }
            catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.BadGateway, !api.PairingEnded, "a foreign 401 at sign-in keeps the pairing too"); }
        }
        Check(PartyApi.IsServersOwn401("""{"data":{},"message":"The request requires valid record authorization token.","status":401}""") && !PartyApi.IsServersOwn401("""{"message":"x","status":"401"}""") && !PartyApi.IsServersOwn401("""{"status":403,"message":"x"}""") && !PartyApi.IsServersOwn401("[]") && !PartyApi.IsServersOwn401(null), "the server's own 401 is PocketBase's JSON error");
        // The API never redirects, and a redirect is never followed: it would send a body with the device key elsewhere.
        using (var handler = PartyApi.NewHandler()) Check(handler is SocketsHttpHandler { AllowAutoRedirect: false }, "the API clients do not follow redirects");
        // An expired publisher session (403) is reported as such; it is no reason to renew the token.
        var forbidden = new Stub((_, _) => Answer(HttpStatusCode.Forbidden, """{"message":"Publisher session expired. Reconnect."}"""));
        using (var api = new PartyApi("https://party.example", "token", "key", forbidden))
        {
            try { await api.HeartbeatAsync("s", "c"); } catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Forbidden, error.Message == "Publisher session expired. Reconnect.", forbidden.Requests.Count == 1, !api.PairingEnded, "403 is not a lost pairing"); }
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
            Check(pair.RootElement.GetProperty("code").GetString() == "ABCDEFGH23456789", pair.RootElement.GetProperty("name").GetString() == PartyApi.DeviceName, "pairing sends the code and this PC's name");
            Check(pairing.Requests[1].Body == "{}", JsonElement.Parse(pairing.Requests[2].Body).GetProperty("name").GetString() == PartyApi.DeviceName, pairing.Requests[3].Body == """{"deviceKey":"k"}""", "absent session fields are left out; the device request names this PC");
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
        // The reader pause (contract C3): the rooms, session and heartbeat answers carry readerPaused, a
        // backend without the switch leaves it out, and a reading meanwhile answers 503 with its message.
        var paused = new Stub((route, _) => route switch
        {
            "/api/odyle/rooms" => Answer(HttpStatusCode.OK, """{"rooms":[],"odylePerCube":40,"announcement":null,"readerPaused":true,"serverNow":"2026-10-15 20:00:00.000Z"}"""),
            "/api/odyle/session" => Answer(HttpStatusCode.OK, """{"sessionId":"s","sequence":0,"readerPaused":false}"""),
            "/api/odyle/heartbeat" => Answer(HttpStatusCode.OK, """{"serverNow":"2026-10-15 20:00:00.000Z","readerPaused":true}"""),
            _ => Answer(HttpStatusCode.ServiceUnavailable, """{"message":"Packet readers are paused by the server operators.","data":{}}"""),
        });
        using (var api = new PartyApi("https://party.example", "token", "key", paused))
        {
            Check((await api.GetRoomsAsync()).ReaderPaused == true, (await api.StartSessionAsync()).ReaderPaused == false, (await api.HeartbeatAsync("s", null)).ReaderPaused == true, "the rooms, session and heartbeat answers carry the pause");
            // Reader health: the heartbeat says what the reader does and the game build it reads.
            await api.HeartbeatAsync("s", "c", "listening", "Steam build 25800038");
            Check(paused.Requests[^1].Body == """{"sessionId":"s","characterId":"c","reader":"listening","game":"Steam build 25800038"}""", "the heartbeat carries the reader's state and the game build");
            try { await api.ReportAsync(new PacketReading("s", 1, "Main", "Lumiel", "GLOBAL_EU", 390, OdyleDecoder.Version)); throw new Exception("Connection check failed: a paused reading was accepted."); }
            catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.ServiceUnavailable, error.Message == PartyApi.ReadersPaused, !api.PairingEnded, "a reading while readers are paused answers 503 with the pause message"); }
        }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Check(JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[]}""", options)!.ReaderPaused is null, JsonSerializer.Deserialize<PublisherSession>("""{"sessionId":"s","sequence":0}""", options)!.ReaderPaused is null, JsonSerializer.Deserialize<HeartbeatResponse>("""{"serverNow":"x"}""", options)!.ReaderPaused is null, "a backend without the switch says nothing about it");
        Check(PartyApi.NameOf("  DESKTOP-ODYLE  ") == "DESKTOP-ODYLE", PartyApi.NameOf(new string('x', 55)).Length == 40, PartyApi.NameOf("") == "Windows PC", PartyApi.DeviceName.Length is > 0 and <= 40, "device names are trimmed to 40 characters");

        // Dashboard links (#70): a page's own path on a backend whose start page marks odyle-routes, the #/ form on older ones.
        const string marked = """<!doctype html><html><head><meta charset="UTF-8"/><base href="./"><meta name="odyle-routes" content="paths"><title>Odyle</title>""";
        var pages = new Stub((route, _) => route == "/" ? Answer(HttpStatusCode.OK, marked, "text/html") : Answer(HttpStatusCode.NotFound, "{}"));
        using (var api = new PartyApi("https://party.example", "token", "key", pages)) Check(await api.ServesPathsAsync(), pages.Requests[^1].Route == "/", "a backend from dashboard 0.49.0 serves paths");
        using (var api = new PartyApi("https://party.example", "token", "key", new Stub((_, _) => Answer(HttpStatusCode.OK, "<!doctype html><title>Odyle</title>", "text/html"))))
            Check(!await api.ServesPathsAsync(), "an older backend's page has no mark");
        using (var api = new PartyApi("https://party.example", "token", "key", new Stub((_, _) => Answer(HttpStatusCode.BadGateway, marked, "text/html"))))
            Check(!await api.ServesPathsAsync(), "a failing answer is no mark");
        Check(DashboardPages.Url("https://party.example", "/tasks", paths: true) == "https://party.example/tasks", DashboardPages.Url("https://party.example/", "account", paths: true) == "https://party.example/account", "pages open at their own paths");
        Check(DashboardPages.Url("https://party.example", "/tasks", paths: false) == "https://party.example/#/tasks", DashboardPages.Url("https://party.example", "/account", paths: false) == "https://party.example/#/account", "older backends get the #/ form");
        Check(DashboardPages.Url("https://party.example", "", paths: false) == "https://party.example/", DashboardPages.Url("https://party.example", "/", paths: true) == "https://party.example/", "the start page is / either way");
        Check(!DashboardPages.ServesPaths("""<meta name="odyle-routes" content="hash">"""), DashboardPages.ServesPaths("""<META NAME='odyle-routes' CONTENT='paths'>"""), "only the paths mark counts");

        // The log rotates at its limit, keeps one previous file, takes writes from many threads and masks secrets.
        var folder = Directory.CreateTempSubdirectory("odyle-log-check-").FullName;
        try
        {
            var log = new RotatingLog(Path.Combine(folder, "odyle.log"), limit: 4096);
            Parallel.For(0, 200, i => log.Write("INFO", $"line {i:000} " + new string('x', 40)));
            var current = File.ReadAllLines(log.FilePath); var previous = File.ReadAllLines(log.PreviousPath);
            Check(new FileInfo(log.FilePath).Length <= 4096, Directory.GetFiles(folder).Length == 2, current.Concat(previous).All(l => l.Contains(" INFO  line ") && l.EndsWith(new string('x', 40), StringComparison.Ordinal)), "the log rotates into one previous file without torn lines");
            // Another program holding the previous file blocks the rotation: the line is written anyway, and
            // the rotation waits a minute instead of being tried before every line.
            var held = new RotatingLog(Path.Combine(folder, "held.log"), limit: 600);
            while (!File.Exists(held.FilePath) || new FileInfo(held.FilePath).Length < 520) held.Write("INFO", "filler");
            File.WriteAllText(held.PreviousPath, "previous");
            using (new FileStream(held.PreviousPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                held.Write("INFO", "written while the previous file is held " + new string('z', 40));
                Check(File.ReadAllText(held.FilePath).Contains("written while the previous file is held"), new FileInfo(held.FilePath).Length > 600, "a blocked rotation still writes the line");
            }
            held.Write("INFO", "after the previous file was let go");
            Check(File.ReadAllText(held.PreviousPath) == "previous", File.ReadAllText(held.FilePath).Contains("after the previous file was let go"), "a blocked rotation is not tried again at once");
            new RotatingLog(held.FilePath, limit: 600).Write("INFO", "a fresh start rotates");
            Check(File.ReadAllText(held.PreviousPath).Contains("written while the previous file is held"), File.ReadAllLines(held.FilePath).Single().EndsWith("a fresh start rotates", StringComparison.Ordinal), "the rotation works again once the file is free");
            const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJpZCI6InUiLCJ0eXBlIjoiYXV0aCJ9.c2lnbmF0dXJlLXNpZ25hdHVyZQ";
            var redacted = RotatingLog.Redact($"token {jwt} key q8Wm2Lz0Pa9XcV4bN7kR1tY6uI3oE5sD8fG2hJ0kL4zX7cV9 link https://party.example/#odyle-pair=ABCDEFGH23456789 code ABCD-EFGH-2345-6789 Authorization: Bearer abc.def");
            Check(!redacted.Contains("eyJ") && !redacted.Contains("q8Wm2") && !redacted.Contains("ABCDEFGH23456789") && !redacted.Contains("ABCD-EFGH") && !redacted.Contains("abc.def") && redacted.Contains("[token]") && redacted.Contains("[secret]") && redacted.Contains("odyle-pair=[code]"), "tokens, device keys and pairing codes never reach the log");
            Check(RotatingLog.Redact("Odyle 0.5.0.12 · session abcdefghij12345 · DispatcherUnhandledExceptionEventArgs") == "Odyle 0.5.0.12 · session abcdefghij12345 · DispatcherUnhandledExceptionEventArgs", "ordinary text is kept");
            // Atomic writes leave the old file or the new one, never a temporary file.
            var target = Path.Combine(folder, "preferences.json");
            AtomicFile.WriteText(target, "{\"a\":1}"); AtomicFile.WriteText(target, "{\"a\":2}");
            Check(File.ReadAllText(target) == "{\"a\":2}", Directory.GetFiles(folder, "*.tmp").Length == 0, "atomic writes replace the file and leave no temporary file");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Failure bodies, 429 pause with Retry-After, renewal without loops, ended pairings, device names, dashboard links, retry schedule, log rotation and redaction passed.");
    }
    const string Own401 = """{"status":401,"message":"The request requires valid record authorization token."}""";
    // The realtime subscription renews like any request; two requests that meet an expired token at
    // once share one sign-in; and the notification settings go out whole.
    static async Task SignInChecks()
    {
        // SubscribeAsync: a 401 signs in once with the device key and subscribes again with the fresh token.
        var expired = true;
        var realtime = new Stub((route, _) =>
        {
            if (route == "/api/odyle/desktop-sign-in") { expired = false; return Answer(HttpStatusCode.OK, Auth); }
            return expired ? Answer(HttpStatusCode.Unauthorized, Own401) : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using (var api = new PartyApi("https://party.example", "old", "key", realtime))
        {
            await api.SubscribeAsync("client", ["odyle.room.r", "odyle.account.u"]);
            Check(realtime.Requests.Select(r => r.Route).SequenceEqual(["/api/realtime", "/api/odyle/desktop-sign-in", "/api/realtime"]), realtime.Requests[2].Token == "fresh", api.Token == "fresh", "a subscription answered 401 signs in once and subscribes again");
            Check(realtime.Requests[2].Body == """{"clientId":"client","subscriptions":["odyle.room.r","odyle.account.u"]}""", "the subscription names the connection and its topics: " + realtime.Requests[2].Body);
        }
        // Without a device key there is nothing to sign in with: the 401 is the answer.
        var keyless = new Stub((_, _) => Answer(HttpStatusCode.Unauthorized, Own401));
        using (var api = new PartyApi("https://party.example", "old", null, keyless))
        {
            try { await api.SubscribeAsync("client", ["odyle.room.r"]); throw new Exception("Connection check failed: a refused subscription was accepted."); }
            catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Unauthorized, keyless.Requests.Count == 1, !api.PairingEnded, "a subscription without a device key fails with its 401 and signs in nowhere"); }
        }
        // A sign-in the server refuses ends the pairing, and the next subscription reaches nothing.
        var refused = new Stub((_, _) => Answer(HttpStatusCode.Unauthorized, Own401));
        using (var api = new PartyApi("https://party.example", "old", "key", refused))
        {
            try { await api.SubscribeAsync("client", ["odyle.room.r"]); throw new Exception("Connection check failed: a subscription with a refused sign-in was accepted."); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
            try { await api.SubscribeAsync("client", ["odyle.room.r"]); throw new Exception("Connection check failed: an ended pairing subscribed."); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
            Check(refused.Requests.Select(r => r.Route).SequenceEqual(["/api/realtime", "/api/odyle/desktop-sign-in"]), api.PairingEnded, "a refused sign-in ends the pairing, and an ended pairing subscribes to nothing");
        }

        // Two requests meet the expired token at once: the second waits for the first one's sign-in.
        var signIns = 0; var refusals = 0; var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new AsyncStub(async (route, token) =>
        {
            if (route == "/api/odyle/desktop-sign-in") { Interlocked.Increment(ref signIns); await released.Task; return Answer(HttpStatusCode.OK, Auth); }
            if (token == "fresh") return Answer(HttpStatusCode.OK, route == "/api/odyle/rooms" ? """{"rooms":[],"odylePerCube":40}""" : """{"items":[]}""");
            // Once both were refused, the sign-in answers a moment later: by then both asked for it.
            if (Interlocked.Increment(ref refusals) == 2) _ = Task.Delay(300).ContinueWith(_ => released.TrySetResult());
            return Answer(HttpStatusCode.Unauthorized, Own401);
        });
        using (var api = new PartyApi("https://party.example", "old", "key", shared))
        {
            var renewed = new List<string>(); api.SignedIn += auth => { lock (renewed) renewed.Add(auth.Token); };
            var both = Task.WhenAll(api.GetRoomsAsync(), api.GetCharactersAsync());
            if (!await Within(both, TimeSpan.FromSeconds(10))) throw new Exception("Connection check failed: two requests waiting for one sign-in never finished.");
            await both;
            Check(signIns == 1, shared.Requests.Count(r => r.Token == "fresh") == 2, api.Token == "fresh", "two requests refused at once share one sign-in and are both repeated with its token");
            Check(renewed.Count == 2 && renewed.All(t => t == "fresh"), "both callers hear the shared sign-in's answer");
        }

        // The notification settings are saved whole, with the defaults filled in.
        var saved = new Stub((_, _) => Answer(HttpStatusCode.OK, """{"notifications":{"channels":{"tray":false,"browser":true,"push":true},"leadMinutes":[15],"kinds":{"runs":true,"cap":false},"saved":true}}"""));
        using (var api = new PartyApi("https://party.example", "token", "key", saved))
        {
            var answer = await api.SaveNotificationsAsync(NotificationSettings.Default with { Channels = new(Tray: false) });
            Check(saved.Requests.Single().Route == "/api/odyle/notifications", answer is { Saved: true, RunsInTray: false }, "the saved settings come back as the server keeps them");
            Check(saved.Requests[0].Body == """{"channels":{"tray":false,"browser":true,"push":true},"leadMinutes":[15],"kinds":{"runs":true,"cap":false,"tasks":null,"ready":null},"capTicks":1,"quiet":{"from":"","to":"","timeZone":""}}""", "the notification settings go out whole: " + saved.Requests[0].Body);
            saved.Requests.Clear();
            await api.SaveNotificationsAsync(new NotificationSettings(LeadMinutes: [60, 5], Kinds: new(Cap: true, Tasks: false), Quiet: new("22:00", "07:00", "Europe/Prague"), CapTicks: 0));
            using var body = JsonDocument.Parse(saved.Requests.Single().Body); var root = body.RootElement;
            Check(root.GetProperty("leadMinutes").EnumerateArray().Select(e => e.GetInt32()).SequenceEqual([60, 5]), root.GetProperty("kinds").GetProperty("cap").GetBoolean() && !root.GetProperty("kinds").GetProperty("tasks").GetBoolean(), root.GetProperty("capTicks").GetInt32() == 0 && root.GetProperty("quiet").GetProperty("timeZone").GetString() == "Europe/Prague", "chosen leads, kinds, cap ticks and quiet hours are sent as chosen");
        }
    }
    sealed class AsyncStub(Func<string, string?, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Route, string? Token)> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath; var bearer = request.Headers.Authorization?.Parameter;
            Requests.Enqueue((route, bearer));
            return answer(route, bearer);
        }
    }
}
