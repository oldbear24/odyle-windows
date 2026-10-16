using System.Text.RegularExpressions;

namespace Odyle.Core;
// The game build Steam installed, for the log, the heartbeat and the patch-day trace: Steam's
// library list (steamapps\libraryfolders.vdf) and AION 2's app manifest in one of those
// libraries (steamapps\appmanifest_3393110.acf, its "buildid"). Steam's own files only; the
// game's process and files are never opened. A game installed outside Steam has no build here.
public static partial class SteamManifest
{
    public const int AionAppId = 3393110;
    public static string ManifestName => $"appmanifest_{AionAppId}.acf";
    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")] private static partial Regex LibraryPath();
    [GeneratedRegex("\"buildid\"\\s+\"(\\d{1,12})\"")] private static partial Regex BuildIdText();
    // The library folders the file lists, with VDF's escaped backslashes undone.
    public static string[] Libraries(string vdf) => [.. LibraryPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace("\\\\", "\\")).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    public static string? BuildId(string acf) => BuildIdText().Match(acf) is { Success: true } m ? m.Groups[1].Value : null;
    // How the log, the heartbeat and the trace name a build.
    public static string Label(string buildId) => "Steam build " + buildId;
}
