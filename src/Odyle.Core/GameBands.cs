namespace Odyle.Core;
// The party and the force (an alliance of parties) the game put a character in, as its player's
// app read them (GameGroups, POST /api/odyle/game-group): the game's ids, null outside one. Room
// snapshots and realtime news carry it as the character's gameGroup; null outside both.
public sealed record GameGroup(string? Party = null, string? Force = null, string At = "");

// Where a listed character stands among the players the game put together: the party band and
// the force band it is in ("" outside one), and whether you are in them too.
public sealed record Band(string Party = "", string Force = "", bool YourParty = false, bool YourForce = false)
{
    public static readonly Band None = new();
    public bool Any => Party.Length > 0 || Force.Length > 0;
}

// The players the game put together, as the dashboard's room page shows them (partyview.js
// inGame): characters whose players are in one party, or in one force, stand together. A player's
// first listed live character with a group speaks for them, and a band needs two players. A
// party's force is the one any of its players reported (a roster may reach one app first); a
// force whose players all share one party is just that party. A band takes the place of its
// first row; you count as listed before everyone, so your band comes first.
public static class GameBands
{
    // Your group: your live character's, from the rooms' snapshots (they list your characters
    // whether you share them or not); the one seen last when two are live.
    public static GameGroup? Own(IEnumerable<RoomSnapshot> snapshots, string accountId, DateTimeOffset now) =>
        snapshots.SelectMany(s => s.Members).Where(m => m.User == accountId).SelectMany(m => m.Characters)
            .Where(c => c.GameGroup is not null && Readiness.Of(c, now) == Presence.Live)
            .OrderByDescending(c => Readiness.Parse(c.LastSeenAt) ?? DateTimeOffset.MinValue).FirstOrDefault()?.GameGroup;

    // The rows in band order with each one's band, and your own band. of gives a row's player and
    // its group, null unless the character is live; your own rows get none (own speaks for you).
    public static (Band Own, (T Item, Band Band)[] Rows) Arrange<T>(IReadOnlyList<T> rows, Func<T, (string Player, GameGroup? Group)> of, string ownPlayer, GameGroup? own)
    {
        // Entry 0 is you, then the rows.
        var n = rows.Count + 1; var speaking = new HashSet<string> { ownPlayer };
        var ids = new (string Party, string Force)[n]; ids[0] = (own?.Party ?? "", own?.Force ?? "");
        for (var i = 1; i < n; i++)
            ids[i] = of(rows[i - 1]) is ({ } player, { } group) && speaking.Add(player) ? (group.Party ?? "", group.Force ?? "") : ("", "");
        var parties = ids.Where(x => x.Party.Length > 0).CountBy(x => x.Party).ToDictionary();
        var party = ids.Select(x => x.Party.Length > 0 && parties[x.Party] >= 2 ? x.Party : "").ToArray();
        var forceOf = new Dictionary<string, string>();
        for (var i = 0; i < n; i++) if (party[i].Length > 0 && ids[i].Force.Length > 0) forceOf.TryAdd(party[i], ids[i].Force);
        var reported = Enumerable.Range(0, n).Select(i => party[i].Length > 0 ? forceOf.GetValueOrDefault(party[i], "") : ids[i].Force).ToArray();
        int[] Of(string force) => Enumerable.Range(0, n).Where(i => reported[i] == force).ToArray();
        bool IsBand(string force) => force.Length > 0 && Of(force) is { Length: >= 2 } m && !(party[m[0]].Length > 0 && m.All(i => party[i] == party[m[0]]));
        var force = reported.Select(f => IsBand(f) ? f : "").ToArray();
        // A force band holds its parties and its players in no party here; a party band its players.
        var order = new List<int>(); var placed = new bool[n];
        void Take(int i) { if (!placed[i]) { placed[i] = true; order.Add(i); } }
        void Party(string id) { for (var j = 0; j < n; j++) if (party[j] == id) Take(j); }
        for (var i = 0; i < n; i++)
        {
            if (placed[i]) continue;
            if (force[i].Length > 0) foreach (var j in Of(force[i])) { if (party[j].Length > 0) Party(party[j]); else Take(j); }
            else if (party[i].Length > 0) Party(party[i]);
            else Take(i);
        }
        Band BandOf(int i) => new(party[i], force[i], party[i].Length > 0 && party[i] == party[0], force[i].Length > 0 && force[i] == force[0]);
        return (BandOf(0), order.Where(i => i > 0).Select(i => (rows[i - 1], BandOf(i))).ToArray());
    }

    // The arranged rows as the lists frame them: a force's band holds its parties' bands and its
    // players in no party here, a party's band its players; the rest stay single rows. Arrange
    // keeps a band's rows together, so a band is a run of rows.
    public static Nested<T>[] Nest<T>(IReadOnlyList<(T Item, Band Band)> rows) =>
        Runs(rows, b => b.Force, "force", b => b.YourForce, inForce => Runs(inForce, b => b.Party, "party", b => b.YourParty, single => single.Select(r => new Nested<T>(r.Item)).ToArray()));
    private static Nested<T>[] Runs<T>(IReadOnlyList<(T Item, Band Band)> rows, Func<Band, string> key, string kind, Func<Band, bool> yours, Func<(T Item, Band Band)[], Nested<T>[]> inner)
    {
        var nested = new List<Nested<T>>();
        for (int i = 0, end; i < rows.Count; i = end)
        {
            var k = key(rows[i].Band);
            for (end = i + 1; end < rows.Count && key(rows[end].Band) == k; end++) { }
            var run = rows.Skip(i).Take(end - i).ToArray();
            if (k.Length == 0) nested.AddRange(inner(run)); else nested.Add(new(default, kind, yours(rows[i].Band), inner(run)));
        }
        return nested.ToArray();
    }
}
// An entry of a list: one row (Item), or a band (Kind "party" or "force", whether it is yours) of
// Children. Rows counts the rows in it, a player each.
public sealed record Nested<T>(T? Item, string Kind = "", bool Yours = false, Nested<T>[]? Children = null)
{
    public bool IsBand => Kind.Length > 0;
    public int Rows => IsBand ? Children!.Sum(c => c.Rows) : 1;
}
