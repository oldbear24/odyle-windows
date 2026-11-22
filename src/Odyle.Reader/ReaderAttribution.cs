using System;
using System.Windows.Threading;
using Odyle.Core;

namespace Odyle.Reader;
// Which character owns the reading the reader decoded last (W#90: moved out of MainWindow, which renders
// it and sends it). The character always comes from the game; the user cannot pick one.
//
// Latest: the reading decoded last, shown on the card at once; LatestAt when it came.
// GameKey: identity the game last named (UnknownOwner after a switch it has not named yet), GameName its
// name and GameId its id in the game, 0 when unknown. DetectedKey: that identity once saved to the
// account. OwnerKey: identity that owns Latest, "" while undecided.
// NameDue: a snapshot came and the game has not named the character since. The game sends one whenever
// it loads a character into the world (login, switch, teleport, channel change) and names the character
// only after the loading screen, 6 to 10 seconds later. NamedHasReading: the named character's Odyle was
// read this run. IdentityDue: a game connection resynced, and the lost bytes may have held a character
// switch; the next reading waits, like a snapshot, for the game to name its character. OdyleMissingDue: a
// snapshot without Odyle waits, like any snapshot, for the game to name its character; OdyleMissingKey is
// then that character, until its Odyle is read.
// TwoClients: two game clients run on this PC, so their characters' messages would mix and one
// character's Odyle could be saved under the other; nothing is read or named until one closes.
// ConnectionRead: this game connection gave an Odyle reading or a full snapshot without Odyle (a character
// under level 22); a reader that listens for long without either is what a game patch looks like.
//
// The publish delay (the window's timer) runs while a reading waits for its owner; when it runs out the
// window calls AttributePending.
internal sealed class ReaderAttribution
{
    internal const string UnknownOwner = "?";
    private readonly DispatcherTimer delay;
    private readonly Action? reset;
    // reset runs whenever the attribution starts over (the window resets what follows the named character).
    internal ReaderAttribution(DispatcherTimer publishDelay, Action? reset = null) { delay = publishDelay; this.reset = reset; }
    internal PacketCandidate? Latest;
    internal DateTimeOffset LatestAt, PendingSince, NamedAt;
    internal string GameKey = "", GameName = "", DetectedKey = "", OwnerKey = "", OdyleMissingKey = "";
    internal ulong GameId;
    internal bool NameDue, NamedHasReading, OdyleMissingDue, IdentityDue, TwoClients, ConnectionRead;
    // The identity the game last named and the capture that read it.
    internal (PacketCapture Capture, GameIdentity Identity)? Named;

    internal static string KeyOf(GameIdentity identity) => identity.CharacterName + "|" + identity.ServerId;
    // The reading's owner is decided and saved to the account: it may be shown under that character's name.
    internal bool OwnerSaved => OwnerKey.Length > 0 && OwnerKey == DetectedKey;
    // The owner is also the character the game names now (what a restart may hand over).
    internal bool OwnerSettled => OwnerSaved && OwnerKey == GameKey;
    // The character the game named is the one saved to the account (what its location, party and PvP wait for).
    internal bool NamedSaved => GameId != 0 && DetectedKey == GameKey;
    internal void Reset()
    {
        Named = null; GameKey = ""; GameName = ""; DetectedKey = ""; OwnerKey = ""; GameId = 0; NameDue = false; NamedHasReading = false; OdyleMissingDue = false; OdyleMissingKey = ""; IdentityDue = false;
        reset?.Invoke();
    }
    // Latest belongs to the character the game named: its updates follow it from now on.
    internal void Attribute(string key) { OwnerKey = key; NamedHasReading = true; if (OdyleMissingKey == key) OdyleMissingKey = ""; }
    // A decoded reading. False for the same frame arriving again on a second capture interface within milliseconds
    // once its owner is known: only its time moves on.
    internal bool Candidate(PacketCandidate observation, DateTimeOffset now)
    {
        ConnectionRead = true;
        var duplicate = Latest is not null && Latest.Total == observation.Total && now - LatestAt < TimeSpan.FromSeconds(2);
        if (duplicate && OwnerKey.Length > 0) { LatestAt = now; return false; }
        Latest = observation; LatestAt = now;
        // A snapshot is sent at login, at a character switch and at every teleport or channel change, and the game
        // names its character only after the loading screen. Hold every snapshot until the game names its owner so
        // it can never be saved to the previous character. Updates keep the owner of their snapshot; one read
        // without any snapshot waits for an owner the same way. After a resync the lost bytes may have held a
        // switch, so the next reading waits the same way.
        if ((observation.Source == CandidateSource.Snapshot || IdentityDue) && !duplicate) { IdentityDue = false; OwnerKey = ""; NameDue = true; OdyleMissingDue = false; PendingSince = LatestAt; delay.Stop(); delay.Start(); }
        else if (OwnerKey.Length == 0 && !delay.IsEnabled) { PendingSince = LatestAt; delay.Start(); }
        return true;
    }
    // A complete snapshot without an Odyle record: the character the game loads has no Odyle yet. The game sends a
    // login's identity and snapshot either way round: true when it named a character a moment ago, which is then
    // the snapshot's (OdyleMissingKey). Any other snapshot waits for the game to name its character; a reading
    // still waiting for a name belonged to the character the game left.
    internal bool SnapshotWithoutOdyle(DateTimeOffset now)
    {
        ConnectionRead = true;
        if (Latest is null && GameKey.Length > 0 && GameKey != UnknownOwner && now - NamedAt < TimeSpan.FromSeconds(10)) { OdyleMissingDue = false; OdyleMissingKey = GameKey; return true; }
        OdyleMissingDue = true;
        if (Latest is not null && OwnerKey.Length == 0) { Latest = null; delay.Stop(); }
        return false;
    }
    // A game connection resynced: the bytes it lost may have held a switch to another character (its snapshot and
    // identity), so the next reading waits for the game to name its character, and an unnamed snapshot no longer
    // goes to the named one after 30 seconds. The reading on the card stays where it is.
    internal void Resync() { IdentityDue = true; NamedHasReading = false; }
    // A second game client appeared: false when two already ran.
    internal bool TwoClientsStarted()
    {
        if (TwoClients) return false;
        TwoClients = true; Latest = null; delay.Stop(); Reset();
        return true;
    }
    // One of two game clients closed: start over like a new connection; the next identity names the character.
    internal bool OneClientLeft()
    {
        if (!TwoClients) return false;
        TwoClients = false; Latest = null; delay.Stop(); Reset();
        return true;
    }
    // A new game connection: a reading the game never attributed belonged to the connection before.
    internal bool DropUnowned()
    {
        if (OwnerKey.Length > 0 || Latest is not { Source: CandidateSource.Update }) return false;
        Latest = null; delay.Stop();
        return true;
    }
    // The publish delay ran out with a reading that has no owner. Without any named character the reading keeps
    // waiting for the identity message.
    internal void AttributePending(DateTimeOffset now)
    {
        if (Latest is null || OwnerKey.Length > 0 || GameKey.Length == 0 || GameKey == UnknownOwner) return;
        if (NameDue)
        {
            // After a snapshot the identity the game sends next names the owner, however long the loading screen
            // takes: the character named before it may be the one just left. One still unnamed after 30 seconds came
            // without a world load, and only goes to the named character if that character's Odyle was already read
            // this run.
            var waiting = now - PendingSince < TimeSpan.FromSeconds(30);
            if (waiting) delay.Start();
            if (waiting || !NamedHasReading) return;
            NameDue = false;
        }
        Attribute(GameKey);
    }
    // The game switched characters on this connection: until it names the new one, no reading may be attributed to
    // either character. False when the game named that character already (two capture interfaces can see the same
    // connection, and each reports the switch).
    internal bool Switched(ulong character)
    {
        if (character == GameId) return false;
        GameKey = UnknownOwner; GameName = ""; DetectedKey = ""; GameId = 0; NamedHasReading = false; OdyleMissingKey = ""; Named = null;
        if (OwnerKey.Length > 0) { Latest = null; OwnerKey = ""; }
        return true;
    }
    // The game named its character. True when it plays a different character than the one named before: a
    // reading the previous one owns must not move over to it.
    internal bool Name(string key, string name)
    {
        if (key == GameKey) return false;
        GameKey = key; GameName = name; DetectedKey = ""; NamedHasReading = false; OdyleMissingKey = "";
        if (OwnerKey.Length > 0) { Latest = null; OwnerKey = ""; }
        return true;
    }
    // Notes the identity the game named. True when a snapshot without Odyle that waited for the name belongs to
    // this character (unless its Odyle was read already: the reading is kept).
    internal bool NoteIdentity(PacketCapture capture, GameIdentity identity, string key, DateTimeOffset now)
    {
        GameId = identity.Id; NameDue = false; IdentityDue = false; NamedAt = now; Named = (capture, identity);
        if (!OdyleMissingDue) return false;
        OdyleMissingDue = false;
        if (Latest is not null) return false;
        OdyleMissingKey = key;
        return true;
    }
    // A reading still waiting for its owner belongs to the character just named.
    internal void Claim(string key)
    {
        if (Latest is null || OwnerKey.Length > 0) return;
        Attribute(key); delay.Stop();
    }
    // The first start after an update or language restart takes over the character the previous build was reading.
    // False when the game loaded a character since the restart: a snapshot read meanwhile came with a world load and
    // waits for the game to name its character like any other. A live update read meanwhile is newer than the
    // handed-over value and keeps its place.
    internal bool TakeOver(Handover handed, DateTimeOffset now)
    {
        if (NameDue || IdentityDue || GameKey.Length > 0) return false;
        GameKey = DetectedKey = handed.CharacterKey; GameName = handed.CharacterName;
        Attribute(handed.CharacterKey);
        if (Latest is null) { Latest = new PacketCandidate(handed.Total, handed.ParserVersion, handed.Source); LatestAt = now; }
        return true;
    }
}
