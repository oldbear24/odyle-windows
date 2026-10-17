namespace Odyle.Core;
// The Windows Run entry that starts Odyle in the tray when the user signs in to Windows:
// HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Odyle". Settings and the
// installer's "Start with Windows" task write the same command.
public static class StartupCommand
{
    public const string ValueName = "Odyle", TrayArgument = "--tray";
    // The uninstaller runs "<Odyle.exe>" --unpair when asked to disconnect this PC: no window opens.
    public const string UnpairArgument = "--unpair";
    // "<Odyle.exe>" --decoder-trace: the patch-day trace (DecoderTrace), started by hand after a game patch.
    public const string TraceArgument = "--decoder-trace";
    // "<Odyle.exe>" --build-info <file>: the publish script asks a fresh package what it is (BuildInfo) and
    // refuses to release one that would not update itself. Nothing else starts.
    public const string BuildInfoArgument = "--build-info";
    // "<Odyle.exe>" --pair "odyle://pair?link=…": an Open in Odyle link from the dashboard (ConnectionDetails.FromAppLink).
    public const string PairArgument = "--pair";
    public static string For(string executable) => $"\"{executable}\" {TrayArgument}";
    // The executable a Run value starts: the quoted path, or the first word without quotes.
    public static string? PathOf(string? command)
    {
        var text = (command ?? "").Trim();
        if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); return end > 1 ? text[1..end] : null; }
        var space = text.IndexOf(' ');
        return text.Length == 0 ? null : space < 0 ? text : text[..space];
    }
    public static bool InTray(IEnumerable<string> args) => args.Any(a => string.Equals(a, TrayArgument, StringComparison.OrdinalIgnoreCase));
    public static bool Unpairs(IEnumerable<string> args) => args.Any(a => string.Equals(a, UnpairArgument, StringComparison.OrdinalIgnoreCase));
    public static bool Traces(IEnumerable<string> args) => args.Any(a => string.Equals(a, TraceArgument, StringComparison.OrdinalIgnoreCase));
    // The file --build-info writes to, or null without the argument (or without a file after it).
    public static string? BuildInfoPath(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++) if (string.Equals(args[i], BuildInfoArgument, StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0) return args[i + 1];
        return null;
    }
    // The link after --pair, or null without one.
    public static string? PairLinkOf(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++) if (string.Equals(args[i], PairArgument, StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0) return args[i + 1];
        return null;
    }
    // What a build is: its version, whether it updates itself, its package kind.
    public static string BuildInfo(Version version, bool release, bool selfContained, string runtime) =>
        System.Text.Json.JsonSerializer.Serialize(new { version = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}", release, selfContained, runtime });
    // A start in the tray stays there while the app is paired (even while the server is
    // still being retried); unpaired, the window and settings open as on a normal start.
    public static bool ShowsWindow(bool tray, bool paired) => !tray || !paired;
}
// A screen's working area in WPF units (device-independent pixels).
public readonly record struct ScreenArea(double Left, double Top, double Right, double Bottom);
public static class Placement
{
    // A remembered position counts while the window's caption would be on a connected
    // screen: the point 40 units right of and 12 below its top-left corner.
    public static bool OnScreen(double left, double top, IEnumerable<ScreenArea> areas) => areas.Any(a => left + 40 >= a.Left && left + 40 <= a.Right && top + 12 >= a.Top && top + 12 <= a.Bottom);
    // Moves a window that reaches past its screen's working area back inside; the
    // top-left corner wins when the window is larger than the screen.
    public static (double Left, double Top) Clamp(double left, double top, double width, double height, ScreenArea area) =>
        (Math.Max(area.Left, Math.Min(left, area.Right - width)), Math.Max(area.Top, Math.Min(top, area.Bottom - height)));
}
