using System.Text.Json;
using Odyle.Core;

// Party readiness (PartyReadiness.cs) against the dashboard's shared cases: readiness-vectors.json
// is a copy of the dashboard's tests/readiness-vectors.json, which its reference rules
// (src/readiness.js) run too; the dashboard's integration suite checks that the copies match.
static class PartyChecks
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static void Run()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "Odyle.Core.Checks", "readiness-vectors.json")));
        var now = DateTimeOffset.Parse(vectors.RootElement.GetProperty("now").GetString()!);
        string Ids(IEnumerable<PartyEntry> list) => string.Join(",", list.Select(e => e.Id));
        string Strings(JsonElement list) => string.Join(",", list.EnumerateArray().Select(e => e.GetString()));
        var cases = 0;
        foreach (var party in vectors.RootElement.GetProperty("parties").EnumerateArray())
        {
            var name = party.GetProperty("name").GetString()!; var expect = party.GetProperty("expect");
            var answer = PartyReadiness.Party(party.GetProperty("entries").Deserialize<PartyEntry[]>(Web)!, now, party.GetProperty("cost").GetInt32());
            Check(Ids(answer.Characters) == Strings(expect.GetProperty("characters")) && Ids(answer.Live) == Strings(expect.GetProperty("live")) && Ids(answer.Ready) == Strings(expect.GetProperty("ready"))
                && Ids(answer.Players) == Strings(expect.GetProperty("players")) && answer.Lowest?.Id == (expect.GetProperty("lowest").ValueKind == JsonValueKind.Null ? null : expect.GetProperty("lowest").GetString())
                && answer.Cubes == expect.GetProperty("cubes").GetInt32() && string.Join(",", answer.Uncounted) == Strings(expect.GetProperty("uncounted")), name);
            cases++;
        }
        foreach (var one in vectors.RootElement.GetProperty("characters").EnumerateArray())
        {
            var name = one.GetProperty("name").GetString()!; var expect = one.GetProperty("expect");
            var got = PartyReadiness.Character(one.GetProperty("character").Deserialize<PartyEntry>(Web)!, now, one.GetProperty("cost").GetInt32());
            Check(got.State.ToString().ToLowerInvariant() == expect.GetProperty("state").GetString() && got.Known == expect.GetProperty("known").GetBoolean() && got.Cubes == expect.GetProperty("cubes").GetInt32() && got.Ready == expect.GetProperty("ready").GetBoolean(), name);
            cases++;
        }
        Check(PartyReadiness.Party([], now, 40) is { Characters.Length: 0, Players.Length: 0, Lowest: null, Cubes: 0, Uncounted.Length: 0 }, "an empty room");
        // A room snapshot lists each member's characters shared in the room, or the member alone.
        var snapshot = JsonSerializer.Deserialize<RoomSnapshot>("""{"room":{"id":"r","name":"Evening","cost":40,"role":"owner","sharing":true},"serverNow":"2026-10-15T12:00:00.000Z","members":[{"id":"m1","user":"u","role":"owner","displayName":"You","sharing":true,"characters":[{"id":"c1","user":"u","characterName":"Ayla","server":"S","region":"GLOBAL_EU","value":90,"confirmed":true,"valid":true,"source":"packet","lastReadingAt":"2026-10-15 11:00:00.000Z","lastSeenAt":"2026-10-15 11:59:58.000Z","shared":true},{"id":"c2","user":"u","characterName":"Hidden","server":"S","region":"GLOBAL_EU","value":500,"confirmed":true,"valid":true,"lastReadingAt":"2026-10-15 11:00:00.000Z","shared":false}]},{"id":"m2","user":"l","role":"viewer","displayName":"Watcher","characters":[]}]}""", Web)!;
        var entries = PartyReadiness.Entries(snapshot);
        Check(entries is [{ Id: "c1", User: "u", DisplayName: "You", Sharing: true }, { Id: "", User: "l", Role: "viewer", CharacterName: "" }], "a snapshot's party: shared characters, else the member");
        Check(PartyReadiness.Party(entries, now, 40) is { Cubes: 2, Lowest.Id: "c1", Uncounted.Length: 0 }, "viewers are never missing from the party");
        // The room's party line.
        Check(RunChip.PartyLine(PartyReadiness.Party(entries, now, 40), "u") == "Party · 2 cubes · lowest you", "your own character can be the lowest");
        Check(RunChip.PartyLine(PartyReadiness.Party(entries, now.AddMinutes(5), 40), "u") == "Party · nobody live", "nobody live");
        var mates = PartyReadiness.Party([entries[0] with { User = "k", DisplayName = "Kaelen", Value = 45 }, new PartyEntry("", "n", "reporter", "Nyx")], now, 40);
        Check(RunChip.PartyLine(mates, "u") == "Party · 1 cube · lowest Kaelen · 1 not live", "the lowest player by name, and who is not live");
        // The next run: one you go to first, then the soonest; a countdown, then "starting now".
        RoomSnapshot WithRuns(params PlannedRun[] runs) => snapshot with { Runs = runs };
        var mine = new PlannedRun("r1", "Fire Temple", "2026-10-15 13:30:00.000Z", "", "u", ["u", "l"]);
        var other = new PlannedRun("r2", "Ice Cave", "2026-10-15 12:30:00.000Z", "", "l", ["l"]);
        var next = RunChip.Pick([WithRuns(other, mine)], "u", now)!;
        Check(next is { Going: true, Attendees: 2, Run.Id: "r1" } && RunChip.Line(next, now) == "Fire Temple · in 1h 30m · 2 going · Evening", "a run you go to comes first, with a countdown");
        Check(RunChip.Pick([WithRuns(other)], "u", now) is { Going: false, Run.Id: "r2" } && RunChip.Pick([WithRuns(other)], "", now) is null, "else the soonest run of your rooms");
        Check(RunChip.Line(next, now.AddMinutes(89.5)) == "Fire Temple · starting now · 2 going · Evening" && RunChip.Pick([WithRuns(mine)], "u", now.AddMinutes(106)) is null, "starting now, gone 15 minutes after the start");
        Check(RunChip.Pick([WithRuns(mine with { StartsAt = "2026-10-16 13:00:00.000Z" })], "u", now) is null, "only the next 24 hours");
        // A run you were going to that was cancelled or moved says so.
        Check(RunChip.Change(next, [WithRuns()], now)!.StartsWith("Fire Temple at ") && RunChip.Change(next, [WithRuns()], now)!.EndsWith(" was cancelled"), "a cancelled run");
        Check(RunChip.Change(next, [WithRuns(mine with { StartsAt = "2026-10-15 14:00:00.000Z" })], now)!.StartsWith("Fire Temple moved to "), "a moved run");
        Check(RunChip.Change(next, [WithRuns(mine)], now) is null && RunChip.Change(next with { Going = false }, [WithRuns()], now) is null, "nothing for an unchanged run or one you were not going to");
        try
        {
            Localizer.Use("cs");
            Check(RunChip.Line(next, now) == "Fire Temple · za 1 h 30 min · jde 2 · Evening" && RunChip.PartyLine(mates, "u") == "Parta · 1 kostka · nejnižší Kaelen · 1 není živě", "the run and party lines in Czech");
        }
        finally { Localizer.Use("en"); }
        Console.WriteLine($"Party readiness, {cases} shared cases with the dashboard, room snapshots, the party line and the next run passed.");
    }
    static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Odyle.Reader"))) return directory.FullName;
        throw new Exception("Party check: the repository root was not found.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Party check failed: " + message); }
}
