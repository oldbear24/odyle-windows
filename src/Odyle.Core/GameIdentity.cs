using System.Buffers.Binary;
using System.Text;

namespace Odyle.Core;

public sealed record GameIdentity(ulong Id, string CharacterName, ushort ServerId, string? Server, string? Region)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // Game server names and their numeric order are protocol data, not a
    // borrowed decoder implementation. See docs/packet-reader.md for sources.
    private static readonly string[] Elyos = "Siel,Nezekan,Vaizel,Kaisinel,Yustiel,Ariel,Fregion,Meslamtaeda,Hithanya,Nania,Tahabata,Luteros,Phernos,Daminu,Kasika,Bakarma,Tsenka,Kochi,Ishtar,Tiamat,Poeta,Verteron,Nathara,Talisra,Zumion,Nahid,Asahr,Caelid,Laveis,Perion,Dramata,Reda,Auldor,Vakron,Narun,Gartua,Chloris,Ione,Teina,Dymones,Bargott,Atheron,Ruthilis,Siliator,Idris,Satia,Estian,Rahu,Rhanman,Hebran".Split(',');
    private static readonly string[] Asmodian = "Israphel,Zikel,Triniel,Lumiel,Marchutan,Azphel,Ereshkigal,Beritra,Nemon,Hadala,Ludra,Ulgorn,Munin,Odar,Zemurru,Kromede,Quai,Baba,Fafnir,Indnath,Ishalgen,Altgard,Agnita,Atiel,Valdemar,Lagta,Gerod,Urd,Ecco,Giselle,Kashapa,Stof,Berk,Nuakum,Grisilla,Santras,Reuben,Hugo,Kraki,Hystan,Rathman,Sigebert,Nazmun,Gelcos,Paton,Pelleir,Elvida,Ketu,Pydeon,Notun".Split(',');

    // Own-character 3336 payload: varint id, u32 mask, u8 mask,
    // u8 UTF-8 byte count, name, u16 server, u32 class.
    public static GameIdentity? Read(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        if (!ExperimentalOdyleDecoder.TryVarint(payload, ref offset, out var id) || id == 0 || payload.Length - offset < 6) return null;
        offset += 4;
        if ((payload[offset++] & 1) == 0) return null;
        var count = payload[offset++];
        if (count is < 1 or > 36 || payload.Length - offset < count + 6) return null;
        string name;
        try { name = Utf8.GetString(payload.Slice(offset, count)); } catch (DecoderFallbackException) { return null; }
        if (name.Length > 40 || !name.Any(char.IsLetter) || name.Any(c => !char.IsLetterOrDigit(c))) return null;
        offset += count;
        var server = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        var job = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 2)..]);
        if (job is < 5 or > 40 || server is < 1000 or >= 3000) return null;
        var region = (server / 100 % 10) switch { 1 or 2 => "GLOBAL_NA", 3 => "GLOBAL_EU", 4 => "GLOBAL_SA", 5 => "GLOBAL_ASIA", _ => null };
        var index = server % 100 - 1;
        var names = server / 1000 == 1 ? Elyos : Asmodian;
        var label = index >= 0 && index < names.Length ? names[index] : null;
        return new(id, name, server, label, region);
    }
}
