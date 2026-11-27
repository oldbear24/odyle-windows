using System.Net;
using System.Text;
using System.Text.Json;
using Odyle.Core;

// Server answers the app relies on every second are normalised after reading (#83): at least one Odyle
// per cube, clock rules without missing lists, rooms with an id, realtime characters with an id, and
// no answer larger than PartyApi.ResponseLimit.
static class AnswerChecks
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static async Task Run()
    {
        // "clock":{} once made every render a NullReferenceException.
        var empty = JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[],"clock":{}}""", Web)!.Normalized();
        Check(empty.Clock is { } rules && rules.TickHours.SequenceEqual(ClockRules.Default.TickHours) && rules.Caps.SequenceEqual(ClockRules.Default.Caps) && rules.Tick == 0, "missing clock lists take the defaults");
        Check(OdyleClock.Of(100, 0, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, empty.Clock) is null, "a clock without a tick shows no estimate instead of failing");
        // Items' limit (dashboard 1.8.0): 2000 from a backend that leaves it out or sends none that fits.
        int? Bonus(string clock) => JsonSerializer.Deserialize<RoomsResponse>("{\"rooms\":[],\"clock\":" + clock + "}", Web)!.Normalized().Clock!.BonusCap;
        Check(Bonus("""{"bonusCap":2500}""") == 2500, Bonus("{}") == 2000, Bonus("""{"bonusCap":0}""") == 2000, Bonus("""{"bonusCap":null}""") == 2000, "the items' limit is read, else 2000");
        var odd = new ClockRules([25, 4, -1, 1, 4], 15, [840, 0, 560, -3]).Normalized();
        Check(odd.TickHours.SequenceEqual([1, 4]), odd.Caps.SequenceEqual([560, 840]), "hours outside the day and caps below one are dropped, the rest sorted");
        Check(OdyleClock.Of(100, 0, DateTimeOffset.UtcNow.AddHours(-30), DateTimeOffset.UtcNow, odd) is { Cap: 560 }, "normalised rules give an estimate");
        foreach (var (json, cost) in new[] { ("""{"rooms":[],"odylePerCube":0}""", 1), ("""{"rooms":[],"odylePerCube":-5}""", 1), ("""{"rooms":[],"odylePerCube":40}""", 40), ("""{"rooms":[]}""", 40) })
            Check(JsonSerializer.Deserialize<RoomsResponse>(json, Web)!.Normalized().OdylePerCube == cost, "Odyle per cube is at least one: " + json);
        var rooms = JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[null,{"name":"No id"},{"id":"r","cost":40,"role":"owner","sharing":true}],"maps":[null]}""", Web)!.Normalized();
        Check(rooms.Rooms is [{ Id: "r", Name: "", Role: "owner" }], rooms.Maps is [], rooms.Clock is null, "rooms without an id are dropped; an answer without a clock keeps none (the defaults apply)");
        Check(JsonSerializer.Deserialize<RoomsResponse>("""{"odylePerCube":40}""", Web)!.Normalized().Rooms.Length == 0, "an answer without rooms lists none");
        // The rooms request returns normalised answers; an oversized answer fails like any unreadable one.
        var body = """{"odylePerCube":0,"clock":{"tick":15}}""";
        using var api = new PartyApi("https://party.example", "token", null, new Stub(_ => body));
        var listed = await api.GetRoomsAsync();
        Check(listed is { Rooms.Length: 0, OdylePerCube: 1, Clock.Tick: 15 }, listed.Clock!.TickHours.SequenceEqual(ClockRules.Default.TickHours), "the rooms answer is normalised when read");
        body = "{\"rooms\":[],\"padding\":\"" + new string('x', PartyApi.ResponseLimit) + "\"}";
        try { await api.GetRoomsAsync(); throw new Exception("Answer check failed: an answer over the limit was read."); }
        catch (HttpRequestException) { }
        // Realtime characters that are null or have no id are dropped.
        using var news = JsonDocument.Parse("""{"characters":[null,{"characterName":"Nobody"},{"id":"","characterName":"Blank"},{"id":"c","user":"u","characterName":"Lumina","server":"Lumiel","region":"GLOBAL_EU","value":5,"confirmed":true,"valid":true,"lastReadingAt":""}]}""");
        var parsed = RealtimeNews.Parse(news.RootElement);
        Check(parsed.Characters is [{ Id: "c", Value: 5 }], "realtime characters without an id are dropped");
        Check(PartyNews.WithCharacters([], parsed.Characters, out var complete).Length == 0, !complete, "the rest still apply");
        // The game's two numbers of a character's latest change in its history (for a restart's hand-over that carries the
        // total only): only when the change left the total asked about and they add up to it.
        var route = ""; var change = """{"changes":[{"value":395,"at":"2026-11-27 16:00:00.000Z","adapter":"global-reference-v2","kind":"regen","delta":15,"parts":{"base":305,"bonus":90},"change":15}],"page":1,"perPage":1,"total":3}""";
        using var history = new PartyApi("https://party.example", "token", null, new Stub(request => { route = request.RequestUri!.PathAndQuery; return change; }));
        Check(await history.GetLatestPartsAsync("c1", 395) == new OdyleParts(305, 90), route == "/api/odyle/history/c1/changes?perPage=1", "the latest change's two numbers are read");
        Check(await history.GetLatestPartsAsync("c1", 400) is null, "not when that change left another value");
        foreach (var other in new[] { """{"changes":[]}""", "{}", """{"changes":[{"value":395,"parts":null}]}""", """{"changes":[{"value":395}]}""", """{"changes":[{"value":395,"parts":{"base":300,"bonus":90}}]}""", """{"changes":[{"value":395,"parts":{"base":-5,"bonus":400}}]}""" })
        {
            change = other;
            Check(await history.GetLatestPartsAsync("c1", 395) is null, "none from " + other);
        }
        Console.WriteLine("Server answer normalisation passed.");
    }
    sealed class Stub(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request), Encoding.UTF8, "application/json") });
    }
}
