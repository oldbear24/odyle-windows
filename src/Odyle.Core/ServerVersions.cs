using System.Globalization;

namespace Odyle.Core;
// The dashboard version a server runs, as its pair, sign-in, session and heartbeat answers name it (serverVersion,
// dashboard 1.0.0 and newer). This app needs dashboard 1.0.0 or later. An older server, which names no version,
// keeps working: the app only says that it runs an older Odyle, and never refuses it.
public static class ServerVersions
{
    public static readonly Version Minimum = new(1, 0, 0);
    // "1.2.3" (also "v1.2.3", "1.2", "1.2.3-rc.1" or "1.2.3+build"); null for anything else, a missing version included.
    public static Version? Parse(string? text)
    {
        var core = (text ?? "").Trim();
        if (core.Length is 0 or > 64) return null;
        if (core[0] is 'v' or 'V') core = core[1..];
        var end = core.IndexOfAny(['-', '+']);
        if (end >= 0) core = core[..end];
        var parts = core.Split('.');
        if (parts.Length is < 2 or > 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new Version(numbers[0], numbers[1], numbers[2]);
    }
    // Whether the server runs a dashboard older than Minimum: one that names no readable version is taken as older.
    public static bool Outdated(string? text) => Parse(text) is not { } version || version < Minimum;
}
