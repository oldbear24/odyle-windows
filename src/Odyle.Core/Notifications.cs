namespace Odyle.Core;

// The account's notification settings, kept on the dashboard (Account & data → Notifications)
// and followed by every browser and Windows app: GET /api/odyle/rooms `notifications`, and news
// on the account's topic when they change. Saved is false while the account keeps the
// defaults; older backends send none (null).
public sealed record NotificationChannels(bool Tray = true, bool Browser = true, bool Push = true);
// Tasks: the tasks still open two hours before a reset (odyle-dashboard#48); left out by older
// backends and saved settings from before it, which means on.
// Ready: everyone going to a run can claim a cube (odyle-dashboard#48); left out means on.
public sealed record NotificationKinds(bool Runs = true, bool Cap = false, bool? Tasks = null, bool? Ready = null);
// From and To are "HH:mm" (both empty: none) in the IANA TimeZone; a span over midnight wraps.
public sealed record QuietHours(string From = "", string To = "", string TimeZone = "");
// CapTicks: how many regeneration ticks before the cap a cap alert comes (0: when full; 1 when left out).
public sealed record NotificationSettings(NotificationChannels? Channels = null, int[]? LeadMinutes = null, NotificationKinds? Kinds = null, QuietHours? Quiet = null, bool Saved = false, int? CapTicks = null)
{
    public static readonly NotificationSettings Default = new();
    public NotificationChannels Where => Channels ?? new();
    public NotificationKinds What => Kinds ?? new();
    public int[] Leads => LeadMinutes is { Length: > 0 } leads ? leads : [15];
    // Planned-run reminders show in this app's tray.
    public bool RunsInTray => Where.Tray && What.Runs;
    // Cap alerts show in this app's tray.
    public bool CapInTray => Where.Tray && What.Cap;
    // Open tasks before a reset show in this app's tray.
    public bool TasksInTray => Where.Tray && (What.Tasks ?? true);
    // Party-ready notices show in this app's tray (the server already skipped quiet hours).
    public bool ReadyInTray => Where.Tray && (What.Ready ?? true);

    // Whether a moment falls in the quiet hours; an unknown time zone has none.
    public bool QuietAt(DateTimeOffset at)
    {
        if (Quiet is not { From.Length: 5, To.Length: 5 } quiet || !TimeOnly.TryParseExact(quiet.From, "HH:mm", out var from) || !TimeOnly.TryParseExact(quiet.To, "HH:mm", out var to)) return false;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(quiet.TimeZone); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return false; }
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        return from < to ? local >= from && local < to : local >= from || local < to;
    }
}
