using System.Buffers.Binary;
using System.Text;

namespace Odyle.Core;

// Class is the wire class id, logged to confirm new classes; it is never sent anywhere.
public sealed record GameIdentity(ulong Id, string CharacterName, ushort ServerId, string? Server, string? Region, uint Class = 0)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // The Global servers by race in the order of their ids, as PLAYNC's official list names them
    // (aion2.plaync.com/en-us/api/gameinfo/servers, checked on 16 Nov 2026): every region numbers
    // its servers in this order. The connected backend, which reads PLAYNC's list every night, names
    // every server it lists (Learn); these names are the fallback for the ones it does not, and a
    // server id neither names is never guessed.
    private static readonly string[] Elyos = "Siel,Nezekan,Vaizel,Kaisinel,Yustiel,Ariel,Fregion,Meslamtaeda,Hithanya,Nania,Tahavatha,Luteros,Phernos,Daminu,Kasaka,Bakarma,Tsenka,Kochi,Ishtar,Tiamat,Gauss,Lamuatan,Nathara".Split(',');
    private static readonly string[] Asmodian = "Israphel,Zikel,Triniel,Lumiel,Marchutan,Azphel,Ereshkigal,Beritra,Nemon,Hadala,Ludra,Ulgorn,Munin,Odar,Zemurru,Kromede,Quai,Baba,Fafnir,Indnath,Agnita,Atiel,Tassin".Split(',');
    // The servers as the connected backend names them; replaced by each Learn.
    private static volatile IReadOnlyDictionary<ushort, string> learned = new Dictionary<ushort, string>();
    // Takes the backend's name of every server it lists with an id: letters only, each name at one
    // place of its race's list (the same in every region). A built-in name stays at its own place
    // unless the backend names that place otherwise, so two servers of one region never share a name.
    public static void Learn(IEnumerable<(int Id, string Name)> servers)
    {
        var next = new Dictionary<ushort, string>(); var place = new Dictionary<string, int>(StringComparer.Ordinal); var named = new HashSet<int>();
        foreach (var (id, name) in servers)
        {
            if (id is < 1000 or >= 3000 || name is null || name.Length is < 2 or > 20 || !name.All(char.IsAsciiLetter)) continue;
            var at = PlaceOf(id);
            if (place.TryGetValue(name, out var other) && other != at) continue;
            place[name] = at; named.Add(at); next[(ushort)id] = name;
        }
        foreach (var (id, name) in next.ToArray())
            if (BuiltInPlace(name) is { } own && own != PlaceOf(id) && !named.Contains(own)) next.Remove(id);
        learned = next;
    }
    // A server's place in its race's list: 101 for the first Elyos server, 201 for the first Asmodian.
    private static int PlaceOf(int id) => id / 1000 * 100 + id % 100;
    private static int? BuiltInPlace(string name) =>
        Array.IndexOf(Elyos, name) is >= 0 and var elyos ? 101 + elyos : Array.IndexOf(Asmodian, name) is >= 0 and var asmodian ? 201 + asmodian : null;

    // Own-character 3336 payload: varint id, u32 mask, u8 mask,
    // u8 UTF-8 byte count, name, u16 server, u32 class. Class ids 5 to 48 are accepted: the
    // reference decoder maps 45 to 48 to the Brawler.
    public static GameIdentity? Read(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        if (!OdyleDecoder.TryVarint(payload, ref offset, out var id) || id == 0 || payload.Length - offset < 6) return null;
        offset += 4;
        if ((payload[offset++] & 1) == 0) return null;
        var count = payload[offset++];
        if (count is < 1 or > 36 || payload.Length - offset < count + 6) return null;
        if (!TryName(payload.Slice(offset, count), out var name)) return null;
        offset += count;
        var server = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        var job = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 2)..]);
        if (job is < 5 or > 48 || server is < 1000 or >= 3000) return null;
        var (label, region) = ServerOf(server);
        return new(id, name, server, label, region, job);
    }
    // A character name as the game sends it: strict UTF-8, letters and digits only, at least one letter.
    public static bool TryName(ReadOnlySpan<byte> bytes, out string name)
    {
        name = "";
        if (bytes.Length is < 1 or > 36) return false;
        string text;
        try { text = Utf8.GetString(bytes); } catch (DecoderFallbackException) { return false; }
        if (!text.Any(char.IsLetter) || text.Any(c => !char.IsLetterOrDigit(c))) return false;
        name = text; return true;
    }
    // The name and region of a Global server id (1000 to 2999), null where this build does not know them.
    public static (string? Server, string? Region) ServerOf(ushort server)
    {
        if (server is < 1000 or >= 3000) return (null, null);
        // NA-East (1xx) and NA-West (2xx) have servers of the same names, so they are separate regions (#4);
        var region = (server / 100 % 10) switch { 1 => "GLOBAL_NA", 2 => "GLOBAL_NAW", 3 => "GLOBAL_EU", 4 => "GLOBAL_SA", 5 => "GLOBAL_ASIA", _ => null };
        return (learned.GetValueOrDefault(server) ?? StaticName(server), region);
    }
    private static string? StaticName(ushort server)
    {
        var index = server % 100 - 1;
        var names = server / 1000 == 1 ? Elyos : Asmodian;
        return index >= 0 && index < names.Length ? names[index] : null;
    }
}
