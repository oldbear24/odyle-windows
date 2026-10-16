using System.Text.Json;
using Odyle.Core;

// The Odyle clock (OdyleClock.cs): the same cases as the dashboard's tests/clock.test.cjs, the
// rules from the rooms answer and the character's own tick and fullest natural pool.
static class ClockChecks
{
    static DateTimeOffset At(string text) => DateTimeOffset.Parse(text);
    public static void Run()
    {
        var rules = ClockRules.Default;
        Check(OdyleClock.Ticks(At("2026-10-14T22:30:00Z"), At("2026-10-15T05:00:00Z"), rules.TickHours).SequenceEqual([At("2026-10-15T01:00:00Z"), At("2026-10-15T04:00:00Z")]), "ticks at the UTC hours");
        Check(OdyleClock.Ticks(At("2026-10-15T04:00:00Z"), At("2026-10-15T07:00:00Z"), rules.TickHours).SequenceEqual([At("2026-10-15T07:00:00Z")]), "a tick at the reading's own hour has already happened");
        Check(OdyleClock.Caps(null, rules).SequenceEqual([560, 840]) && OdyleClock.Caps(560, rules).SequenceEqual([560, 840]) && OdyleClock.Caps(600, rules).SequenceEqual([840]) && OdyleClock.Caps(2000, rules).SequenceEqual([840]), "the caps a character fits under");
        try
        {
            Localizer.Use("en");
            var now = At("2026-10-15T05:00:00Z"); var read = At("2026-10-14T22:30:00Z");
            var projected = OdyleClock.Of(500, 300, read, now, rules, null, 600)!;
            Check(projected is { Base: 530, Total: 830, Cap: 840, Tick: 15, Full: false, Settled: true, Projected: true } && projected.Next == At("2026-10-15T07:00:00Z") && projected.Fills.Single() == new ClockFill(840, At("2026-10-17T19:00:00Z")), "a projection from an older reading");
            Check(OdyleClock.Line(projected, now, false).StartsWith("≈830 now if unspent · +15 in 2h 0m · full ≈ ") && !OdyleClock.Line(projected, now, false).Contains('/'), "the line of a character not read right now");
            Check(OdyleClock.Line(projected, now, true).StartsWith("+15 in 2h 0m · full ≈ "), "a character read right now shows no estimate");
            var open = OdyleClock.Of(500, 300, read, now, rules, null, 500)!;
            Check(open is { Cap: 560, Settled: false } && open.Fills.SequenceEqual([new ClockFill(560, At("2026-10-15T10:00:00Z")), new ClockFill(840, At("2026-10-17T19:00:00Z"))]), "an open cap gives both fill times");
            var reached = OdyleClock.Of(500, 300, At("2026-10-13T00:30:00Z"), now, rules, null, 500)!;
            Check(reached is { Base: 560, Full: true } && OdyleClock.Line(reached, now, true).StartsWith("natural pool at 560: full if its cap is 560 · full ≈ ") && OdyleClock.Line(reached, now, true).EndsWith(" at 840"), "an open cap is not called full");
            var full = OdyleClock.Of(840, 0, read, now, rules, 20, 840)!;
            Check(full is { Full: true, Settled: true, Total: 840, Tick: 20, Projected: false } && OdyleClock.Line(full, now, true) == "natural pool full · each tick of 20 is lost", "a full pool loses each tick of the character's own size");
            Check(OdyleClock.Of(500, 300, read, now, rules with { Paused = true }) is null && OdyleClock.Line(null, now, false) == "", "the operators can hide the clock");
            // A membership answer settles the cap whatever the readings showed; readings above it are pointed out.
            Check(OdyleClock.Caps(null, rules, 560).SequenceEqual([560]) && OdyleClock.Caps(300, rules, 840).SequenceEqual([840]) && OdyleClock.Caps(300, rules, 0).SequenceEqual([560, 840]), "the answered cap wins over the readings");
            var answeredNo = OdyleClock.Of(500, 0, now, now, rules, null, 500, 560);
            Check(answeredNo is { Cap: 560, Settled: true } && answeredNo.Fills.Select(f => f.Cap).SequenceEqual([560]) && !OdyleClock.Line(answeredNo, now, true).Contains(" at 560"), "without the membership the pool is full at 560, said without hedging");
            Check(OdyleClock.Of(500, 0, now, now, rules, null, 500, 840) is { Cap: 840, Settled: true }, "with it a pool that never passed 560 counts up to 840");
            Check(OdyleClock.Mismatch(700, 560) == "Natural pool held 700, more than 560: has the membership started? Answer on the dashboard." && OdyleClock.Mismatch(560, 560) == "" && OdyleClock.Mismatch(700, null) == "" && OdyleClock.Mismatch(300, 840) == "", "readings above the answered cap are pointed out");
            Localizer.Use("cs");
            Check(OdyleClock.Line(full, now, true) == "přirozená zásoba plná · každých 20 propadá", "the clock in Czech");
        }
        finally { Localizer.Use("en"); }
        // The rules and what the readings showed, as the backend sends them; older backends send neither.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var listed = JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[],"odylePerCube":40,"clock":{"tickHours":[2,5],"tick":10,"caps":[500],"paused":true}}""", options)!;
        Check(listed.Clock is { Tick: 10, Paused: true } && listed.Clock.TickHours.SequenceEqual([2, 5]) && listed.Clock.Caps.SequenceEqual([500]), "the rooms answer carries the clock's rules");
        Check(JsonSerializer.Deserialize<RoomsResponse>("""{"rooms":[]}""", options)!.Clock is null, "an older backend sends no rules");
        var character = JsonSerializer.Deserialize<Character>("""{"id":"c","user":"u","characterName":"Lumina","server":"Lumiel","region":"GLOBAL_EU","value":905,"confirmed":true,"valid":true,"lastReadingAt":"2026-10-15 05:00:00.000Z","parts":{"base":15,"bonus":890},"regenTick":15,"naturalMax":600,"naturalMaxAt":"2026-10-15 05:00:00.000Z"}""", options)!;
        Check(character is { RegenTick: 15, NaturalMax: 600, AnsweredCap: null }, "a character carries its tick and fullest natural pool, and no answered cap from older backends");
        Check(JsonSerializer.Deserialize<Character>("""{"id":"c","user":"u","characterName":"Lumina","server":"Lumiel","region":"GLOBAL_EU","value":905,"confirmed":true,"valid":true,"lastReadingAt":"","answeredCap":560}""", options) is { AnsweredCap: 560 }, "a character carries the cap its account's membership answer settled");
        Console.WriteLine("Odyle clock ticks, caps, the membership answer, projection, card line and rules passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Odyle clock check failed: " + message); }
}
