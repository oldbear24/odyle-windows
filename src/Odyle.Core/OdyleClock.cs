namespace Odyle.Core;

// The Odyle clock's game rules, kept by the server's operators (GET /api/odyle/rooms `clock`):
// the natural pool gains Tick Odyle at each of the UTC TickHours up to a cap; Paused hides
// every estimate. Older backends send none, and the app uses the defaults.
public sealed record ClockRules(int[] TickHours, int Tick, int[] Caps, bool Paused = false)
{
    public static readonly ClockRules Default = new([1, 4, 7, 10, 13, 16, 19, 22], 15, [560, 840]);
}

// When a cap is reached; At is null when it is more than 120 days away.
public sealed record ClockFill(int Cap, DateTimeOffset? At);

// How close a natural pool is to its cap, for the card's colours: Soon (orange) when it fills
// within two days, Full (red) once it is full or fills within the hour.
public enum CapLevel { None, Soon, Full }

// Where a character's natural pool stands now and where it goes. Projected: estimated from an
// older reading; Settled: the character's cap is known (it held more than the smaller ones).
public sealed record ClockState(int Base, int Total, int Cap, int Tick, DateTimeOffset? Next, ClockFill[] Fills, bool Full, bool Settled, bool Projected);

// The same projection as the dashboard (src/clock.js) and the backend (backend/odyle/clock.go),
// from the last reading of the game's two numbers. An estimate, computed here and never sent:
// a spend on a PC without the reader stays unseen until its next reading.
public static class OdyleClock
{
    // The tick times after from up to and including until, at most limit of them.
    public static List<DateTimeOffset> Ticks(DateTimeOffset from, DateTimeOffset until, int[] hours, int limit = int.MaxValue)
    {
        var start = from.ToUniversalTime(); var at = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        var ticks = new List<DateTimeOffset>();
        for (; at <= until && ticks.Count < limit; at = at.AddHours(1)) if (hours.Contains(at.Hour)) ticks.Add(at);
        return ticks;
    }

    // The caps a character may have, smallest first: the one its account's membership answer
    // settled (answeredCap, set on the dashboard: 840 with it, 560 without), else those its
    // fullest natural pool of the last 30 days fits under; one once its readings settled it.
    public static int[] Caps(int? naturalMax, ClockRules rules, int? answeredCap = null)
    {
        if (answeredCap is > 0 and var answered) return [answered];
        var fit = rules.Caps.Where(cap => cap >= (naturalMax ?? 0)).ToArray();
        return fit.Length > 0 ? fit : rules.Caps.Length > 0 ? [rules.Caps[^1]] : [];
    }

    // Null while the operators pause the clock or without the two numbers and a reading time.
    // While the cap is open the estimate stops at the smallest one.
    public static ClockState? Of(int natural, int items, DateTimeOffset readAt, DateTimeOffset now, ClockRules? rules = null, int? regenTick = null, int? naturalMax = null, int? answeredCap = null)
    {
        rules ??= ClockRules.Default;
        var caps = Caps(naturalMax, rules, answeredCap);
        if (rules.Paused || caps.Length == 0 || rules.TickHours.Length == 0) return null;
        var tick = regenTick is > 0 and var own ? own : rules.Tick; var cap = caps[0];
        if (tick <= 0) return null;
        var passed = Ticks(readAt, now, rules.TickHours, 60 * 8).Count;
        var value = natural >= cap ? natural : Math.Min(cap, natural + passed * tick);
        var next = Ticks(now, now.AddDays(2), rules.TickHours, 1).Select(t => (DateTimeOffset?)t).FirstOrDefault();
        var fills = caps.Where(limit => value < limit).Select(limit =>
        {
            var due = Ticks(now, now.AddDays(120), rules.TickHours, (int)Math.Ceiling((limit - value) / (double)tick));
            return new ClockFill(limit, due.Count > 0 ? due[^1] : null);
        }).ToArray();
        return new ClockState(value, value + items, cap, tick, next, fills, value >= cap, caps.Length == 1, value != natural);
    }

    // Readings that contradict the membership answer: the natural pool held more than the
    // answered cap (the membership started, most likely). The readings stay as read; the card
    // says so under the clock, or "" when they fit.
    public static string Mismatch(int? naturalMax, int? answeredCap) =>
        answeredCap is > 0 and var cap && naturalMax is { } held && held > cap ? Localizer.T("Natural pool held {0:N0}, more than {1:N0}: has the membership started? Answer on the dashboard.", held, cap) : "";

    // The dashboard's capLevel (src/clock.js): while the cap is open it goes by the smallest one,
    // as cap alerts do.
    public static CapLevel Level(ClockState? clock, DateTimeOffset now)
    {
        if (clock is null) return CapLevel.None;
        var at = clock.Full ? now : clock.Fills.FirstOrDefault()?.At;
        if (at is not { } due) return CapLevel.None;
        return due - now <= TimeSpan.FromHours(1) ? CapLevel.Full : due - now <= TimeSpan.FromDays(2) ? CapLevel.Soon : CapLevel.None;
    }

    // What the colour means, ahead of the card's line while the pool is not full yet (a full one
    // says so itself), or "".
    public static string Flag(ClockState? clock, DateTimeOffset now) => clock is null || clock.Full ? "" : Level(clock, now) switch
    {
        CapLevel.Soon => Localizer.T("Nearly full"),
        CapLevel.Full => Localizer.T("Full within the hour"),
        _ => "",
    };

    // One line for the card: the estimate of a character not read right now, then either the
    // next tick and when the natural pool is full, or that it is full and losing each tick.
    public static string Line(ClockState? clock, DateTimeOffset now, bool fresh)
    {
        if (clock is null) return "";
        string When(DateTimeOffset at) => at.ToLocalTime().ToString("ddd HH:mm", Localizer.Culture);
        var parts = new List<string>();
        if (!fresh && clock.Projected) parts.Add(Localizer.T("≈{0:N0} now if unspent", clock.Total));
        if (clock.Full && clock.Settled) parts.Add(Localizer.T("natural pool full · each tick of {0} is lost", clock.Tick));
        else
        {
            if (clock.Full) parts.Add(Localizer.T("natural pool at {0:N0}: full if its cap is {1:N0}", clock.Base, clock.Cap));
            else if (clock.Next is { } next) parts.Add(Localizer.T("+{0} in {1}", clock.Tick, Checklist.Remaining(next - now)));
            var fills = clock.Fills.Where(f => f.At is not null).ToArray();
            if (fills.Length > 0) parts.Add(Localizer.T("full ≈ {0}", string.Join(" / ", fills.Select(f => clock.Settled ? When(f.At!.Value) : Localizer.T("{0} at {1:N0}", When(f.At!.Value), f.Cap)))));
        }
        return string.Join(" · ", parts);
    }
}
