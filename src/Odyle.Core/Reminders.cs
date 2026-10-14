namespace Odyle.Core;
public sealed record DueRun(Room Room, PlannedRun Run, DateTimeOffset StartsAt, string Key);
// Planned-run reminders: one notification 15 minutes before each run the account joined
// on the dashboard. A run is reminded once per start time, so moving it reminds again.
public static class Reminders
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(15);
    public static string Key(PlannedRun run) => run.Id + "|" + run.StartsAt;
    // Runs the account goes to that start within the next 15 minutes and were not reminded yet, soonest first.
    public static DueRun[] Due(IEnumerable<RoomSnapshot> snapshots, string accountId, DateTimeOffset now, IReadOnlySet<string> reminded) => accountId.Length == 0 ? [] :
        snapshots.SelectMany(s => (s.Runs ?? []).Where(r => r.Attendees?.Contains(accountId) == true && !reminded.Contains(Key(r)))
                .Select(r => (Room: s.Room, Run: r, Start: Readiness.Parse(r.StartsAt))))
            .Where(x => x.Start is { } start && start > now && start - now <= Lead)
            .DistinctBy(x => x.Run.Id).OrderBy(x => x.Start)
            .Select(x => new DueRun(x.Room, x.Run, x.Start!.Value, Key(x.Run))).ToArray();
    // Keys of runs that started more than a day ago are forgotten; at most 100 are kept.
    public static string[] Prune(IEnumerable<string> keys, DateTimeOffset now) =>
        keys.Distinct().Where(k => Readiness.Parse(k[(k.IndexOf('|') + 1)..]) is not { } start || now - start < TimeSpan.FromDays(1)).TakeLast(100).ToArray();
}
