namespace Odyle.Core;

// The card's answers for play: each room's party line, and the next planned run with a Going
// toggle (the only thing the app changes about runs; planning stays on the dashboard).
public sealed record NextRun(Room Room, PlannedRun Run, DateTimeOffset StartsAt, bool Going, int Attendees);

public static class RunChip
{
    static readonly TimeSpan Started = TimeSpan.FromMinutes(15), Ahead = TimeSpan.FromDays(1);

    // The soonest run of the account's rooms from 15 minutes ago to a day ahead, ones it goes to first.
    public static NextRun? Pick(IEnumerable<RoomSnapshot> snapshots, string accountId, DateTimeOffset now) => accountId.Length == 0 ? null :
        snapshots.SelectMany(s => (s.Runs ?? []).Select(r => (Snapshot: s, Run: r, Start: Readiness.Parse(r.StartsAt))))
            .Where(x => x.Start is { } start && start > now - Started && start - now <= Ahead)
            .Select(x => new NextRun(x.Snapshot.Room, x.Run, x.Start!.Value, x.Run.Attendees?.Contains(accountId) == true, x.Run.Attendees?.Length ?? 0))
            .OrderBy(r => r.Going ? 0 : 1).ThenBy(r => r.StartsAt).FirstOrDefault();

    // "Fire Temple · in 1h 20m · 3 going · Evening", or "starting now" from a minute before the start.
    public static string Line(NextRun run, DateTimeOffset now)
    {
        var left = run.StartsAt - now;
        var when = left <= TimeSpan.FromMinutes(1) ? Localizer.T("starting now") : Localizer.T("in {0}", Checklist.Remaining(left));
        return $"{run.Run.Dungeon} · {when} · {Localizer.T("{0} going", run.Attendees)} · {run.Room.Name}";
    }

    // What became of a run the account was going to and the card showed: cancelled, or moved.
    public static string? Change(NextRun? before, IEnumerable<RoomSnapshot> snapshots, DateTimeOffset now)
    {
        if (before is not { Going: true } || before.StartsAt <= now - Started) return null;
        var still = snapshots.SelectMany(s => s.Runs ?? []).FirstOrDefault(r => r.Id == before.Run.Id);
        if (still is null) return Localizer.T("{0} at {1:HH:mm} was cancelled", before.Run.Dungeon, before.StartsAt.ToLocalTime().DateTime);
        return Readiness.Parse(still.StartsAt) is { } start && start != before.StartsAt ? Localizer.T("{0} moved to {1:ddd HH:mm}", before.Run.Dungeon, start.ToLocalTime().DateTime) : null;
    }

    // A room's party in one line: "Party · 2 cubes · lowest Kaelen · 1 not live".
    public static string PartyLine(PartyAnswer party, string accountId)
    {
        if (party.Lowest is not { } lowest) return Localizer.T("Party · nobody live");
        var name = lowest.User == accountId ? Localizer.T("you") : lowest.DisplayName;
        var line = Localizer.T("Party · {0} · lowest {1}", Localizer.Tn(party.Cubes, "{0} cube", "{0} cubes"), name);
        return party.Uncounted.Length > 0 ? line + " · " + Localizer.T("{0} not live", party.Uncounted.Length) : line;
    }
}
