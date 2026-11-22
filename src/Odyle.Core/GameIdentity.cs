using System.Buffers.Binary;
using System.Text;

namespace Odyle.Core;

// Class is the wire class id, logged to confirm new classes; it is never sent anywhere.
public sealed record GameIdentity(ulong Id, string CharacterName, ushort ServerId, string? Server, string? Region, uint Class = 0)
{
    // The region a server takes: one that does not list NA-West yet saves those characters as NA, as before.
    public string? RegionFor(bool serverKnowsNaWest) => Region == "GLOBAL_NAW" && !serverKnowsNaWest ? "GLOBAL_NA" : Region;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // The Global servers by race in the order of their ids, as PLAYNC's official list names them
    // (aion2.plaync.com/en-us/api/gameinfo/servers, checked on 16 Nov 2026): every region numbers
    // its servers in this order. A server id past the lists is named only by the connected
    // backend (Learn), which reads PLAYNC's list every night; it is never guessed.
    private static readonly string[] Elyos = "Siel,Nezekan,Vaizel,Kaisinel,Yustiel,Ariel,Fregion,Meslamtaeda,Hithanya,Nania,Tahavatha,Luteros,Phernos,Daminu,Kasaka,Bakarma,Tsenka,Kochi,Ishtar,Tiamat,Gauss,Lamuatan,Nathara".Split(',');
    private static readonly string[] Asmodian = "Israphel,Zikel,Triniel,Lumiel,Marchutan,Azphel,Ereshkigal,Beritra,Nemon,Hadala,Ludra,Ulgorn,Munin,Odar,Zemurru,Kromede,Quai,Baba,Fafnir,Indnath,Agnita,Atiel,Tassin".Split(',');
    // Before 0.34.0 seven Global servers had a third-party table's names: Gauss (1x21) was saved as
    // Poeta, Tassin (2x23) as Agnita. A backend that does not list Tassin yet keeps its characters
    // under those names (one that does not list each region's servers), so they are saved there as before.
    private static readonly Dictionary<string, string> Legacy = new() { ["Tahavatha"] = "Tahabata", ["Kasaka"] = "Kasika", ["Gauss"] = "Poeta", ["Lamuatan"] = "Verteron", ["Agnita"] = "Ishalgen", ["Atiel"] = "Altgard", ["Tassin"] = "Agnita" };

    // The server a backend saves the character under: its Global name, or the older name where
    // the backend does not know the Global servers yet.
    public string? ServerFor(bool globalServers) => globalServers ? Server : LegacyName(Server);
    // Whether a backend would keep the server under another name before it knows the Global servers.
    public bool RenamedServer => Server != LegacyName(Server);
    public static string? LegacyName(string? server) => server is not null && Legacy.TryGetValue(server, out var old) ? old : server;
    // Servers past the lists, as the connected backend names them; replaced by each Learn.
    private static volatile IReadOnlyDictionary<ushort, string> learned = new Dictionary<ushort, string>();
    // Takes the backend's names of the servers the lists do not name: letters only, each name once.
    public static void Learn(IEnumerable<(int Id, string Name)> servers)
    {
        var known = Elyos.Concat(Asmodian).ToHashSet(StringComparer.Ordinal); var next = new Dictionary<ushort, string>(); var place = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, name) in servers)
        {
            if (id is < 1000 or >= 3000 || StaticName((ushort)id) is not null || known.Contains(name) || name.Length is < 2 or > 20 || !name.All(char.IsAsciiLetter)) continue;
            // One name is one place in its race's list, in every region.
            var at = id / 1000 * 100 + id % 100;
            if (place.TryGetValue(name, out var other) && other != at) continue;
            place[name] = at; next[(ushort)id] = name;
        }
        learned = next;
    }

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
        // a server from before NA-West takes GLOBAL_NA for both (GameIdentity.RegionFor).
        var region = (server / 100 % 10) switch { 1 => "GLOBAL_NA", 2 => "GLOBAL_NAW", 3 => "GLOBAL_EU", 4 => "GLOBAL_SA", 5 => "GLOBAL_ASIA", _ => null };
        return (StaticName(server) ?? learned.GetValueOrDefault(server), region);
    }
    private static string? StaticName(ushort server)
    {
        var index = server % 100 - 1;
        var names = server / 1000 == 1 ? Elyos : Asmodian;
        return index >= 0 && index < names.Length ? names[index] : null;
    }
}
