using System.Globalization;

namespace Odyle.Core;
public enum Presence { Live, Stale, Offline, Unknown }
// Same rule as the dashboard: a reading is live while its reader reports presence,
// stale 45 seconds after that or once invalidated, and offline after 90 seconds.
public static class Readiness
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45), OfflineAfter = TimeSpan.FromSeconds(90);
    public static DateTimeOffset? Parse(string? stamp) =>
        DateTimeOffset.TryParse((stamp ?? "").Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    public static Presence Of(Character character, DateTimeOffset now) =>
        !character.Confirmed || Parse(character.LastReadingAt) is null ? Presence.Unknown : Seen(character.LastSeenAt, character.Valid, character.Source, now);
    // A read character's presence from when its reader last reported it and whether its reading still counts
    // (PartyReadiness uses the same thresholds).
    public static Presence Seen(string? lastSeenAt, bool valid, string? source, DateTimeOffset now)
    {
        if (Parse(lastSeenAt) is not { } seen || now - seen >= OfflineAfter) return Presence.Offline;
        if (!valid || source == "manual" || now - seen >= StaleAfter) return Presence.Stale;
        return Presence.Live;
    }
}
