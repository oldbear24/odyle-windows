namespace Odyle.Core;
// One task of the account's private daily or weekly checklist, as GET /api/odyle/tasks
// lists it. DoneAt is the last time the account checked it, "" while unchecked.
public sealed record UserTask(string Id, string User, string Title, string Cadence, string DoneAt = "", string Created = "", string Updated = "");
// Reset times of the Global service, edited by superusers on the backend.
public sealed record TaskResets(int DailyResetHour = 7, string WeeklyResetDay = "wednesday");
public sealed record TasksResponse(UserTask[] Tasks, TaskResets? Resets = null, string ServerNow = "");
// Same rule as the dashboard's src/tasks.js: a task is done while it was checked after
// the current period began, so the lists clear by themselves at reset and nothing is
// deleted. Daily periods begin at the reset hour UTC, weekly ones on the reset weekday
// at that hour. The server only stores the time a task was checked.
public static class Checklist
{
    public static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    public static readonly TaskResets Defaults = new();
    public static int ResetHour(TaskResets? resets) => resets is { DailyResetHour: >= 0 and < 24 } ? resets.DailyResetHour : Defaults.DailyResetHour;
    private static int ResetWeekday(TaskResets? resets) { var index = Array.IndexOf(Days, resets?.WeeklyResetDay); return index < 0 ? Array.IndexOf(Days, Defaults.WeeklyResetDay) : index; }
    public static string DayName(TaskResets? resets) { var name = Days[ResetWeekday(resets)]; return char.ToUpperInvariant(name[0]) + name[1..]; }
    // Start of the daily or weekly period that contains `now`.
    public static DateTimeOffset PeriodStart(string cadence, DateTimeOffset now, TaskResets? resets = null)
    {
        var at = now.ToUniversalTime();
        var start = new DateTimeOffset(at.Year, at.Month, at.Day, ResetHour(resets), 0, 0, TimeSpan.Zero);
        if (start > at) start = start.AddDays(-1);
        if (cadence == "weekly") start = start.AddDays(-(((int)start.DayOfWeek - ResetWeekday(resets) + 7) % 7));
        return start;
    }
    public static DateTimeOffset NextReset(string cadence, DateTimeOffset now, TaskResets? resets = null) => PeriodStart(cadence, now, resets).AddDays(cadence == "weekly" ? 7 : 1);
    public static bool IsDone(UserTask task, DateTimeOffset now, TaskResets? resets = null) => Readiness.Parse(task.DoneAt) is { } done && done >= PeriodStart(task.Cadence, now, resets);
    // "2d 3h", "5h 12m", "4m" or "under a minute".
    public static string Remaining(TimeSpan span)
    {
        var minutes = Math.Max(0, (long)Math.Floor(span.TotalMinutes)); var hours = minutes / 60 % 24; var days = minutes / 1440;
        return days > 0 ? Localizer.T("{0}d {1}h", days, hours) : hours > 0 ? Localizer.T("{0}h {1}m", hours, minutes % 60) : minutes > 0 ? Localizer.T("{0}m", minutes) : Localizer.T("under a minute");
    }
}
