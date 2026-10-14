namespace Odyle.Core;
// The Windows Run entry that starts Odyle in the tray when the user signs in to Windows:
// HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Odyle". Settings and the
// installer's "Start with Windows" task write the same command.
public static class StartupCommand
{
    public const string ValueName = "Odyle", TrayArgument = "--tray";
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
