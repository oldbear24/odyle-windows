namespace Odyle.Core;
// Compact mode (#29): the main window cut down to what matters while playing, your Odyle and
// your party's, in tight rows that stay easy to read, in the same place. Which characters it
// lists and its shortcuts are decided here, where the checks reach them.
public static class CompactStrip
{
    // The window's width in compact mode (the card is 280).
    public const double Width = 220;
    // At most this many other characters; the rest are counted in one line.
    public const int MateLimit = 10;

    // An other member's character as the compact window lists it: its Odyle, the cubes it
    // can open in its room, whether it is live (else stale) and whose it is.
    public sealed record Mate(string Name, int Value, int Cubes, bool Live, string Player);

    // Other members' characters shared in your rooms that are live or recently stale, each once
    // however many rooms show it: live ones first, then the most Odyle. Like the card's party
    // list, offline characters and your own are left out.
    public static (Mate[] Mates, int More) Mates(IEnumerable<RoomSnapshot> snapshots, string accountId, DateTimeOffset now, int perCube, int limit = MateLimit)
    {
        var seen = new HashSet<string>(); var found = new List<Mate>();
        foreach (var snapshot in snapshots)
        {
            var cost = snapshot.Room.Cost > 0 ? snapshot.Room.Cost : Math.Max(1, perCube);
            foreach (var member in snapshot.Members.Where(m => m.User != accountId))
                foreach (var c in member.Characters.Where(c => c.Shared ?? true))
                {
                    var state = Readiness.Of(c, now);
                    if (state is Presence.Live or Presence.Stale && seen.Add(c.Id.Length > 0 ? c.Id : member.User + "|" + c.CharacterName))
                        found.Add(new(c.CharacterName, c.Value, c.Value / cost, state == Presence.Live, member.DisplayName));
                }
        }
        var ordered = found.OrderByDescending(m => m.Live).ThenByDescending(m => m.Value).ToArray();
        return (ordered.Take(limit).ToArray(), Math.Max(0, ordered.Length - limit));
    }

    // The shortcut that switches compact mode on and off from anywhere, chosen in Settings; ""
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
}
