namespace Odyle.Core;

// One entry of a room's party as the dashboard lists it (its src/main.js flattened()): a
// character shared in the room with its player, or a member with none (CharacterName empty).
public sealed record PartyEntry(string Id = "", string User = "", string Role = "", string DisplayName = "", string CharacterName = "", int Value = 0,
    bool Confirmed = false, bool Valid = true, string Source = "", bool Sharing = false, string LastSeenAt = "", string LastReadingAt = "");
public sealed record CharacterReadiness(Presence State, bool Known, int Cubes, bool Ready);
// Players are each counted player's best live character, Lowest the one that sets the cubes,
// Uncounted the members (viewers aside) without a live character.
public sealed record PartyAnswer(PartyEntry[] Characters, PartyEntry[] Live, PartyEntry[] Ready, PartyEntry[] Players, PartyEntry? Lowest, int Cubes, string[] Uncounted);

// Party readiness, a mirror of the dashboard's reference rules (src/readiness.js), checked against
// the same cases (tests/Odyle.Core.Checks/readiness-vectors.json, the dashboard's copy). Odyle
// cannot be pooled: each player brings their best live character, and the party can only do as
// many cubes as its lowest player allows. Stale, offline and unread characters count for nothing.
public static class PartyReadiness
{
    public static Presence StateOf(PartyEntry e, DateTimeOffset now)
    {
        if (!e.Sharing) return Presence.Offline;
        if (!e.Confirmed || e.LastReadingAt.Length == 0) return Presence.Unknown;
        if (Readiness.Parse(e.LastSeenAt) is not { } seen || now - seen >= TimeSpan.FromSeconds(90)) return Presence.Offline;
        if (!e.Valid || e.Source == "manual" || now - seen >= TimeSpan.FromSeconds(45)) return Presence.Stale;
        return Presence.Live;
    }

    public static CharacterReadiness Character(PartyEntry e, DateTimeOffset now, int cost)
    {
        var state = StateOf(e, now); var known = e.Confirmed && e.LastReadingAt.Length > 0;
        return new(state, known, known ? e.Value / cost : 0, state == Presence.Live && e.Value >= cost);
    }

    public static PartyAnswer Party(IReadOnlyList<PartyEntry> entries, DateTimeOffset now, int cost)
    {
        var characters = entries.Where(e => e.CharacterName.Length > 0).ToArray();
        var live = characters.Where(e => StateOf(e, now) == Presence.Live).ToArray();
        var ready = live.Where(e => e.Value >= cost).ToArray();
        // Each player's best live character, in the order players first appear; ties keep the first.
        var best = new List<PartyEntry>();
        foreach (var e in live)
        {
            var at = best.FindIndex(b => b.User == e.User);
            if (at < 0) best.Add(e); else if (e.Value > best[at].Value) best[at] = e;
        }
        PartyEntry? lowest = null;
        foreach (var e in best) if (lowest is null || e.Value < lowest.Value) lowest = e;
        var counted = best.Select(e => e.User).ToHashSet();
        var uncounted = entries.Where(e => e.Role != "viewer").Select(e => e.User).Distinct().Where(user => !counted.Contains(user)).ToArray();
        return new(characters, live, ready, best.ToArray(), lowest, lowest is null ? 0 : lowest.Value / cost, uncounted);
    }

    // A room snapshot's party: each member's characters shared in the room, or the member alone.
    // A snapshot without the flags (older backends) lists only shared characters of others.
    public static PartyEntry[] Entries(RoomSnapshot snapshot) => snapshot.Members.SelectMany(m =>
    {
        var shared = m.Characters.Where(c => c.Shared ?? m.Sharing ?? true).ToArray();
        return shared.Length > 0
            ? shared.Select(c => new PartyEntry(c.Id, m.User, m.Role, m.DisplayName, c.CharacterName, c.Value, c.Confirmed, c.Valid, c.Source, true, c.LastSeenAt, c.LastReadingAt))
            : [new PartyEntry("", m.User, m.Role, m.DisplayName, Sharing: m.Sharing ?? false, Source: "packet")];
    }).ToArray();
}
