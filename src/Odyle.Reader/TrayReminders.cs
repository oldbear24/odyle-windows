using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// The account's notification settings and the reminders they ask for in the tray (W#90: moved out of MainWindow,
// which fetches what they are about). What was reminded is kept in preferences.json, so a restart does not repeat it.
internal sealed class TrayReminders(Func<Preferences> preferences, Action<Preferences> save, Action<string, string> notify)
{
    // The account's notification settings (the dashboard's Account & data → Notifications).
    internal NotificationSettings Settings = NotificationSettings.Default;
    // The settings from the rooms answer. A backend without them keeps the old local switch; with them, an old "off"
    // moves to the account once (only while the account never saved any), and Settings links to the dashboard from
    // then on. current: the paired server's request client, null while not connected.
    internal async Task FollowAsync(NotificationSettings? listed, PartyApi? current)
    {
        if (listed is null) { Settings = NotificationSettings.Default with { Channels = new(Tray: preferences().RemindRuns) }; return; }
        Settings = listed;
        if (preferences().RemindRunsMoved || current is null) return;
        if (!listed.Saved && !preferences().RemindRuns)
        {
            try { Settings = await current.SaveNotificationsAsync(listed with { Channels = listed.Where with { Tray = false } }); Log.Info("Moved the local choice to show no run reminders to the account's notification settings."); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { Log.Warn("Could not move the run reminder choice to the account yet: " + error.Message); return; }
        }
        save(preferences() with { RemindRunsMoved = true });
    }
    // Planned runs the account joined on the dashboard: a notification at each of the account's reminder times before
    // each start, with the dungeon, room, local time and note, unless the tray is off for them or it is quiet hours. A
    // run moved to another time reminds again. now: the server's time.
    internal void Runs(RoomSnapshot[] snapshots, string accountId, DateTimeOffset now)
    {
        if (!Settings.RunsInTray || accountId.Length == 0) return;
        var reminded = new HashSet<string>(preferences().RemindedRuns ?? []);
        var due = Reminders.Due(snapshots, accountId, now, reminded, Settings.Leads);
        if (due.Length == 0) return;
        var quiet = Settings.QuietAt(now);
        foreach (var run in due)
        {
            foreach (var key in run.Marks ?? [run.Key]) reminded.Add(key);
            if (quiet) { Log.Info($"Skipped the reminder of the planned run {run.Run.Id} in the account's quiet hours."); continue; }
            var minutes = Math.Max(1, (long)Math.Ceiling((run.StartsAt - now).TotalMinutes));
            var details = T("{0} · starts at {1:t}", run.Room.Name, run.StartsAt.ToLocalTime().DateTime) + (run.Run.Note.Length > 0 ? "\n" + run.Run.Note : "");
            notify(Tn(minutes, "{1} starts in {0} minute", "{1} starts in {0} minutes", run.Run.Dungeon), details);
            Log.Info($"Reminded of the planned run {run.Run.Id} ({run.Run.Dungeon}) at {run.StartsAt:u}, {run.Lead} minutes ahead.");
        }
        save(preferences() with { RemindedRuns = Reminders.Prune(reminded, now) });
    }
    // Two hours before each daily and weekly reset, the tasks of that cadence still open, unless it is quiet hours.
    // now: the server's time.
    internal void Resets(UserTask[] tasks, TaskResets resets, DateTimeOffset now)
    {
        var reminded = new HashSet<string>(preferences().RemindedResets ?? []);
        var due = ResetReminders.Due(tasks, resets, now, reminded);
        if (due.Length == 0) return;
        var quiet = Settings.QuietAt(now);
        foreach (var notice in due)
        {
            reminded.Add(notice.Key);
            if (quiet) { Log.Info($"Skipped the {notice.Cadence} reset reminder in the account's quiet hours."); continue; }
            var left = Checklist.Remaining(notice.ResetAt - now);
            var title = notice.Cadence == "weekly" ? Tn(notice.Open.Length, "Weekly reset in {1} · {0} task open", "Weekly reset in {1} · {0} tasks open", left) : Tn(notice.Open.Length, "Daily reset in {1} · {0} task open", "Daily reset in {1} · {0} tasks open", left);
            notify(title, T("Still open: {0}", ResetReminders.Summary(notice.Open)));
            Log.Info($"Reminded of {notice.Open.Length} open {notice.Cadence} tasks before the reset at {notice.ResetAt:u}.");
        }
        save(preferences() with { RemindedResets = Reminders.Prune(reminded, now) });
    }
}
