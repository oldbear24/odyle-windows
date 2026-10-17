namespace Odyle.Core;

// A cap alert of the backend's odyle-cap-alerts job, on the account's topic: one of the
// account's characters will have a full natural pool soon (At) or has one (State "full"), as
// the Odyle clock projects it. Settled is false while the cap may be a larger one. The app
// shows it in the tray when the account's notification settings want cap alerts there.
// Everyone going to a run can claim a cube (odyle-dashboard#48): the server tells each attendee
// once per start time as partyReady news.
public sealed record PartyReady(string RunId, string RoomId, string Room, string Dungeon, string StartsAt, int Of)
{
    public (string Title, string Text) Notice() => (Localizer.T("{0}: the party is ready", Dungeon), Localizer.T("All {0} going can claim a cube · {1}", Of, Room));
}
public sealed record CapAlert(string CharacterId, string CharacterName, string State, int Base, int Cap, bool Settled, int Tick, string? At = null)
{
    public (string Title, string Text) Notice()
    {
        if (State == "soon")
        {
            var when = Readiness.Parse(At) is { } at ? at.ToLocalTime().ToString("ddd HH:mm", Localizer.Culture) : "";
            return (Localizer.T("{0} caps soon", CharacterName), Settled ? Localizer.T("Its natural pool is full ≈ {0}; each tick of {1} after that is lost.", when, Tick) : Localizer.T("Its natural pool reaches {0:N0} ≈ {1}, full if that is its cap.", Cap, when));
        }
        return (Localizer.T("{0} is losing Odyle", CharacterName), Settled ? Localizer.T("Its natural pool is full: each tick of {0} is lost until you spend some.", Tick) : Localizer.T("Its natural pool is at {0:N0}: if that is its cap, each tick of {1} is lost.", Cap, Tick));
    }
}
