namespace Odyle.Core;
// The super compact strip (#29): a small window above the game with the card's value, cubes
// and state dot, and one line of what matters next. The window lives in Odyle.Reader; what it
// shows, its shortcut and where it first opens are decided here, where the checks reach them.
public static class CompactStrip
{
    public const double Width = 196, Height = 46;

    // The strip's line, the first that applies: the server paused readers, the next planned run,
    // the room's party line, else what the card says to do next ("" when it says nothing).
    public static string Line(bool paused, string nextRun, string partyLine, string hint) =>
        paused ? Localizer.T("Paused by the server")
        : nextRun.Length > 0 ? nextRun
        : partyLine.Length > 0 ? partyLine
        : hint;

    // The shortcut that switches the strip on and off from anywhere, chosen in Settings; ""
    // turns it off. A game or another app may already use one, so there is a choice.
    public const string DefaultHotkey = "Ctrl+Alt+O";
    public static readonly string[] Hotkeys = [DefaultHotkey, "Ctrl+Alt+K", "Ctrl+Shift+O", "Ctrl+Alt+F12", ""];

    // RegisterHotKey's modifiers (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_NOREPEAT 0x4000) and
    // virtual key of a shortcut such as "Ctrl+Alt+O", or null when it is not one Odyle offers.
    public static (uint Modifiers, uint Key)? Parse(string hotkey)
    {
        if (!Hotkeys.Contains(hotkey) || hotkey.Length == 0) return null;
        uint modifiers = 0x4000, key = 0;
        foreach (var part in hotkey.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": modifiers |= 2; break;
                case "Alt": modifiers |= 1; break;
                case "Shift": modifiers |= 4; break;
                case [var letter] when letter is >= 'A' and <= 'Z': key = letter; break;
                case ['F', .. var number] when int.TryParse(number, out var f) && f is >= 1 and <= 24: key = (uint)(0x6F + f); break;
                default: return null;
            }
        }
        return key == 0 ? null : (modifiers, key);
    }

    // Where the strip first opens: the top right corner of the screen, clear of its edges.
    public static (double Left, double Top) Corner(ScreenArea area) => (area.Right - Width - 16, area.Top + 16);
}
