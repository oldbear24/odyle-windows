namespace Odyle.Core;

// A daily or weekly reset coming with tasks still open on the account's checklist: its key (kept
// so a restart does not repeat it), cadence, time and the open tasks.
public sealed record ResetNotice(string Key, string Cadence, DateTimeOffset ResetAt, UserTask[] Open);
// Two hours before each daily and weekly reset, the tasks of that cadence still open, in the tray
// (odyle-dashboard#48, "Tasks still open before a reset" under Account & data → Notifications).
// Never for ticked tasks, and nothing when every task is done; one notice per reset.
public static class ResetReminders
{
    public static readonly TimeSpan Ahead = TimeSpan.FromHours(2);
    // Whether a reset is close enough that the checklist is worth fetching (ahead plus a margin).
    public static bool Near(DateTimeOffset now, TaskResets? resets) =>
        Checklist.NextReset("daily", now, resets) - now <= Ahead + TimeSpan.FromMinutes(10) || Checklist.NextReset("weekly", now, resets) - now <= Ahead + TimeSpan.FromMinutes(10);
    public static ResetNotice[] Due(IEnumerable<UserTask> tasks, TaskResets? resets, DateTimeOffset now, ISet<string> reminded)
    {
        var list = tasks.ToArray(); var due = new List<ResetNotice>();
        foreach (var cadence in new[] { "daily", "weekly" })
        {
            var reset = Checklist.NextReset(cadence, now, resets);
            if (reset - now > Ahead) continue;
            var key = $"reset|{reset.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}|{cadence}";
            if (reminded.Contains(key)) continue;
            var open = list.Where(t => t.Cadence == cadence && !Checklist.IsDone(t, now, resets)).ToArray();
            if (open.Length > 0) due.Add(new ResetNotice(key, cadence, reset, open));
        }
        return due.ToArray();
    }
    // The open tasks in one line, the same title once with a count: "Sanctuary ×2, Field boss".
    public static string Summary(IEnumerable<UserTask> open) =>
        string.Join(", ", open.GroupBy(t => t.Title.Trim(), StringComparer.CurrentCultureIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
}
