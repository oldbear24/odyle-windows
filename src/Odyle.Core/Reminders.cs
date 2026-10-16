namespace Odyle.Core;
// Lead is the reminder time it is due for; Marks are the keys to remember (every reminder time
// the run is within, so a later look never reminds of a larger one after a smaller).
public sealed record DueRun(Room Room, PlannedRun Run, DateTimeOffset StartsAt, string Key, int Lead = 15, string[]? Marks = null);
// Planned-run reminders: a notification at each of the account's reminder times (15 minutes by
// default; up to three, set on the dashboard) before each run the account joined. A run is
// reminded once per start time and reminder time, so moving it reminds again; a run already
// within a smaller time when first seen is reminded once, at the smallest.
public static class Reminders
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(15);
    public static string Key(PlannedRun run, int lead) => run.Id + "|" + run.StartsAt + "|" + lead;
    // Apps before reminder times kept run|start for the 15 minutes.
    static string LegacyKey(PlannedRun run) => run.Id + "|" + run.StartsAt;
    // Runs the account goes to that are within a reminder time and were not reminded at it yet, soonest first.
    public static DueRun[] Due(IEnumerable<RoomSnapshot> snapshots, string accountId, DateTimeOffset now, IReadOnlySet<string> reminded, IReadOnlyCollection<int>? leads = null)
    {
        if (accountId.Length == 0) return [];
        var times = leads is { Count: > 0 } ? leads : [(int)Lead.TotalMinutes];
        return snapshots.SelectMany(s => (s.Runs ?? []).Where(r => r.Attendees?.Contains(accountId) == true)
                .Select(r => (Room: s.Room, Run: r, Start: Readiness.Parse(r.StartsAt))))
            .Where(x => x.Start is { } start && start > now)
            .DistinctBy(x => x.Run.Id).OrderBy(x => x.Start)
            .Select(x =>
            {
                var within = times.Where(lead => x.Start!.Value - now <= TimeSpan.FromMinutes(lead)).ToArray();
                if (within.Length == 0) return null;
                var lead = within.Min();
                if (reminded.Contains(Key(x.Run, lead)) || lead <= Lead.TotalMinutes && reminded.Contains(LegacyKey(x.Run))) return null;
                return new DueRun(x.Room, x.Run, x.Start!.Value, Key(x.Run, lead), lead, within.Select(l => Key(x.Run, l)).ToArray());
            }).OfType<DueRun>().ToArray();
    }
    // Keys of runs that started more than a day ago are forgotten; at most 100 are kept.
    public static string[] Prune(IEnumerable<string> keys, DateTimeOffset now) =>
        keys.Distinct().Where(k => k.Split('|') is not [_, var start, ..] || Readiness.Parse(start) is not { } at || now - at < TimeSpan.FromDays(1)).TakeLast(100).ToArray();
}
