using System.Globalization;
using System.Text.Json;
using Odyle.Core;

// Mirrors the dashboard's tests/tasks.test.cjs so both clients agree on when a task
// is done and when the lists clear.
static class ChecklistChecks
{
    public static void Run()
    {
        static DateTimeOffset At(string stamp) => DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        // Launch defaults: 07:00 UTC daily, Wednesday weekly (2026-10-14 is a Wednesday).
        Check(Checklist.PeriodStart("daily", At("2026-10-13T06:59:59Z")) == At("2026-10-12T07:00:00Z"), "before the daily reset the period began yesterday");
        Check(Checklist.PeriodStart("daily", At("2026-10-13T07:00:00Z")) == At("2026-10-13T07:00:00Z"), "the daily reset starts a new period");
        Check(Checklist.NextReset("daily", At("2026-10-13T12:00:00Z")) == At("2026-10-14T07:00:00Z"), "next daily reset");
        Check(Checklist.PeriodStart("weekly", At("2026-10-14T06:59:59Z")) == At("2026-10-07T07:00:00Z"), "before the weekly reset the period began last Wednesday");
        Check(Checklist.PeriodStart("weekly", At("2026-10-14T07:00:00Z")) == At("2026-10-14T07:00:00Z"), "the weekly reset starts a new period");
        Check(Checklist.PeriodStart("weekly", At("2026-10-20T23:00:00Z")) == At("2026-10-14T07:00:00Z"), "late in the week the period still began on Wednesday");
        Check(Checklist.NextReset("weekly", At("2026-10-20T23:00:00Z")) == At("2026-10-21T07:00:00Z"), "next weekly reset");
        // Server settings move both resets; unusable values fall back to the defaults.
        var resets = new TaskResets(0, "monday");
        Check(Checklist.PeriodStart("daily", At("2026-10-13T23:30:00Z"), resets) == At("2026-10-13T00:00:00Z") && Checklist.NextReset("daily", At("2026-10-13T23:30:00Z"), resets) == At("2026-10-14T00:00:00Z"), "midnight daily reset");
        Check(Checklist.PeriodStart("weekly", At("2026-10-18T23:30:00Z"), resets) == At("2026-10-12T00:00:00Z"), "Monday weekly reset");
        Check(Checklist.PeriodStart("weekly", At("2026-10-14T07:00:00Z"), new TaskResets(99, "someday")) == At("2026-10-14T07:00:00Z"), "unusable settings fall back to the defaults");
        Check(Checklist.PeriodStart("daily", At("2026-10-14T06:00:00Z"), null) == At("2026-10-13T07:00:00Z"), "missing settings use the defaults");
        Check(Checklist.DayName(resets) == "Monday" && Checklist.DayName(null) == "Wednesday" && Checklist.ResetHour(null) == 7, "reset names");
        // Done means checked after the current period began; the server never unchecks.
        var ticked = new UserTask("t", "u", "Daily dungeon", "daily", "2026-10-13 18:00:00.000Z");
        Check(Checklist.IsDone(ticked, At("2026-10-14T06:59:00Z")) && !Checklist.IsDone(ticked, At("2026-10-14T07:00:00Z")), "a daily task unchecks itself at the daily reset");
        var weekly = ticked with { Cadence = "weekly" };
        Check(Checklist.IsDone(weekly, At("2026-10-14T06:59:00Z")) && !Checklist.IsDone(weekly, At("2026-10-14T07:00:00Z")), "a weekly task unchecks itself at the weekly reset");
        Check(Checklist.IsDone(weekly with { DoneAt = "2026-10-14 07:00:00.000Z" }, At("2026-10-20T23:00:00Z")), "checked at the reset counts for the whole week");
        Check(!Checklist.IsDone(ticked with { DoneAt = "" }, At("2026-10-13T10:00:00Z")), "an unchecked task is not done");
        Check(Checklist.Remaining(TimeSpan.FromSeconds(30)) == "under a minute" && Checklist.Remaining(TimeSpan.FromMinutes(4)) == "4m" && Checklist.Remaining(TimeSpan.FromMinutes(5 * 60 + 12)) == "5h 12m" && Checklist.Remaining(TimeSpan.FromHours(2 * 24 + 3)) == "2d 3h", "remaining time wording");
        // The checklist as the backend sends it: PocketBase record dates, extra fields and an ISO server time.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var listed = JsonSerializer.Deserialize<TasksResponse>("""{"tasks":[{"id":"t1","user":"u","title":"Daily dungeon","cadence":"daily","doneAt":"2026-10-13 18:00:00.000Z","created":"2026-10-13 09:00:00.000Z","updated":"2026-10-13 18:00:00.000Z","collectionName":"user_tasks"},{"id":"t2","user":"u","title":"Weekly raid","cadence":"weekly","doneAt":"","created":"2026-10-13 09:00:00.000Z","updated":"2026-10-13 09:00:00.000Z"}],"resets":{"dailyResetHour":7,"weeklyResetDay":"wednesday"},"serverNow":"2026-10-13T18:00:01.000Z"}""", options)!;
        var serverNow = Readiness.Parse(listed.ServerNow)!.Value;
        Check(listed.Tasks.Length == 2 && listed.Resets is { DailyResetHour: 7, WeeklyResetDay: "wednesday" }, "checklist contract");
        Check(Checklist.IsDone(listed.Tasks[0], serverNow, listed.Resets) && !Checklist.IsDone(listed.Tasks[1], serverNow, listed.Resets), "done follows doneAt and the server time");
        // A list per character (#38): the app shows the account's own list and the card's character's.
        var scoped = JsonSerializer.Deserialize<TasksResponse>("""{"tasks":[{"id":"a","user":"u","title":"Arena","cadence":"daily","character":""},{"id":"b","user":"u","title":"Nightmare","cadence":"weekly","character":"alt","characterName":"Altie"},{"id":"c","user":"u","title":"Main only","cadence":"daily","character":"main","characterName":"Mainy"}],"characters":[],"templates":[],"limit":40}""", options)!;
        Check(scoped.Tasks[1] is { Character: "alt", CharacterName: "Altie" } && listed.Tasks[0].Character is null, "a task carries its character's list, and older backends send none");
        Check(Checklist.Shown(scoped.Tasks, "alt").Select(t => t.Id).SequenceEqual(["a", "b"]) && Checklist.Shown(scoped.Tasks, null).Select(t => t.Id).SequenceEqual(["a"]) && Checklist.Shown(listed.Tasks, "alt").Length == 2, "the account's list and the card's character's, never the other characters'");
        Console.WriteLine("Daily and weekly checklist periods, reset settings, done state and wire contract passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Checklist check failed: " + message); }
}
