using System.Globalization;

namespace Odyle.Core;
public enum Presence { Live, Stale, Offline, Unknown }
// Same rule as the dashboard: a reading is live while its reader reports presence,
// stale 45 seconds after that or once invalidated, and offline after 90 seconds.
public static class Readiness
{
    public static DateTimeOffset? Parse(string? stamp) =>
        DateTimeOffset.TryParse((stamp ?? "").Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    public static Presence Of(Character character, DateTimeOffset now)
    {
        if (!character.Confirmed || Parse(character.LastReadingAt) is null) return Presence.Unknown;
        if (Parse(character.LastSeenAt) is not { } seen || now - seen >= TimeSpan.FromSeconds(90)) return Presence.Offline;
        if (!character.Valid || character.Source == "manual" || now - seen >= TimeSpan.FromSeconds(45)) return Presence.Stale;
        return Presence.Live;
    }
}
