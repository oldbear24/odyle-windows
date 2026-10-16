namespace Odyle.Core;
// What an Odyle reading changed, sent with it: the game's three-hourly regeneration raises the
// natural pool (base) alone; a spend lowers the total; anything else that raises it (an item,
// a reward) is an item. A snapshot (login, channel change) names no change, and an update with
// no reading before it on this live streak is only an update. Delta is the change in the total.
public static class OdyleChange
{
    public const string Snapshot = "snapshot", Update = "update", Regen = "regen", Spend = "spend", Item = "item";
    public static (string Kind, int? Delta) Of(CandidateSource source, int total, int? @base, int? previousTotal, int? previousBase)
    {
        if (source == CandidateSource.Snapshot) return (Snapshot, null);
        if (previousTotal is not { } before) return (Update, null);
        var delta = total - before;
        if (delta < 0) return (Spend, delta);
        if (delta > 0 && @base is { } now && previousBase is { } then && now - then == delta) return (Regen, delta);
        return (delta > 0 ? Item : Update, delta);
    }
}
