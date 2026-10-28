using System.Buffers.Binary;

namespace Odyle.Core;
// The party and the force (an alliance of parties) the own character is in, as the rosters the game
// server sends that character say (Global, seen on the owner's client on 2026-10-28). Only the ids
// the game gives the party and the force are read, never who else is in them. A roster counts only
// when it lists the own character by its entity id (the 3336 varint).
public enum GroupChange { Party, PartyEnded, Force, ForceLeft, ForceEnded }
// Key is the party's or force's id as 16 lowercase hex digits (the wire's eight bytes in order);
// Member the character id of the own roster entry (Party, Force) or of the player who left
// (ForceLeft); Owner the entity id a roster listed.
public sealed record GameGroupEvent(GroupChange Change, string Key = "", ulong Member = 0, ulong Owner = 0);
public static class GameGroups
{
    private const int GuidLength = 36;
    // Whether a frame is one of the rosters, which need the own entity id to be read.
    public static bool IsRoster(ReadOnlySpan<byte> frame) => frame.Length >= 2 && (frame[0], frame[1]) is (0x00, 0x92) or (0x02, 0x96);
    // A whole frame, opcode first; null for anything without the layout seen on Global.
    public static GameGroupEvent? Read(ReadOnlySpan<byte> frame, ulong? own)
    {
        if (frame.Length < 2) return null;
        var payload = frame[2..];
        return (frame[0], frame[1]) switch
        {
            (0x00, 0x92) when own is { } id => Roster(payload, id, GroupChange.Party),
            (0x02, 0x96) when own is { } id => Roster(payload, id, GroupChange.Force),
            // 1F 92, the own party ended or was left: one byte (the reason).
            (0x1f, 0x92) when payload.Length == 1 => new(GroupChange.PartyEnded),
            // 22 96, a player left the force: u64 character id, the id of their party (8 bytes), one byte.
            (0x22, 0x96) when payload.Length == 17 => new(GroupChange.ForceLeft, Key(payload[8..16]), BinaryPrimitives.ReadUInt64LittleEndian(payload)),
            // 2E 96, the force ended: its id (8 bytes), one byte.
            (0x2e, 0x96) when payload.Length == 9 => new(GroupChange.ForceEnded, Key(payload[..8])),
            _ => null,
        };
    }
    // 00 92 (party) and 02 96 (force): u8 flags, the group's id (8 bytes), its leader and settings,
    // then the members, each u8 mask, u8 slot, u32 entity id, 4 bytes, the account GUID (u8 36 and
    // 36 characters), u64 character id, the name (u8 length, UTF-8) and more. The member entry with
    // the own entity id is found by that id followed by a GUID; names are never read.
    private static GameGroupEvent? Roster(ReadOnlySpan<byte> p, ulong own, GroupChange change)
    {
        if (p.Length < 9 + 10 + GuidLength + 8 || own is 0 or > uint.MaxValue || !p[1..9].ContainsAnyExcept((byte)0)) return null;
        for (var i = 9; i + 9 + GuidLength + 8 <= p.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(p[i..]) != own || p[i + 8] != GuidLength || !Guid(p.Slice(i + 9, GuidLength))) continue;
            var member = BinaryPrimitives.ReadUInt64LittleEndian(p[(i + 9 + GuidLength)..]);
            return new(change, Key(p[1..9]), member, own);
        }
        return null;
    }
    // 8-4-4-4-12 hex digits.
    private static bool Guid(ReadOnlySpan<byte> s)
    {
        for (var i = 0; i < s.Length; i++)
            if (i is 8 or 13 or 18 or 23 ? s[i] != (byte)'-' : !char.IsAsciiHexDigit((char)s[i])) return false;
        return true;
    }
    private static string Key(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}
// What the rosters said about the named character: its party and force ids, "" outside one. They
// belong to the entity id the roster listed: once the game names another one, they are forgotten.
// Leaving the party (1F 92) leaves the force too.
public sealed class GroupTracker
{
    // The backend's group adapter registry lists this name (backend/odyle/protocols.go).
    public const string Adapter = "global-group-v1";
    private ulong member;
    public string Party { get; private set; } = "";
    public string Force { get; private set; } = "";
    // The entity id the rosters listed, 0 before one.
    public ulong Owner { get; private set; }
    // A change the decoder read; true when the party or the force is another now.
    public bool Observe(GameGroupEvent e)
    {
        var before = (Party, Force);
        switch (e.Change)
        {
            case GroupChange.Party or GroupChange.Force:
                if (e.Owner != Owner) { Party = Force = ""; Owner = e.Owner; }
                if (e.Change == GroupChange.Party) Party = e.Key; else Force = e.Key;
                member = e.Member;
                break;
            case GroupChange.PartyEnded: Party = Force = ""; break;
            case GroupChange.ForceLeft when member != 0 && e.Member == member: Force = ""; break;
            case GroupChange.ForceEnded when e.Key == Force: Force = ""; break;
        }
        return before != (Party, Force);
    }
    // The game named the character with this entity id: what another one's rosters said is not its.
    public void Named(ulong entity) { if (Owner != 0 && Owner != entity) Reset(); }
    public void Reset() { Party = Force = ""; Owner = member = 0; }
}
