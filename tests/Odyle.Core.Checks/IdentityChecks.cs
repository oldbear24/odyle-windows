using System.Buffers.Binary;
using System.Text;
using Odyle.Core;

static class IdentityChecks
{
    public static void Run()
    {
        byte[] Payload(ushort server, string name = "TestHero", uint job = 25)
        {
            var text = Encoding.UTF8.GetBytes(name); var bytes = new byte[1 + 6 + text.Length + 6];
            bytes[0] = 42; bytes[5] = 1; bytes[6] = (byte)text.Length; text.CopyTo(bytes, 7);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7 + text.Length), server);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9 + text.Length), job); return bytes;
        }
        var lumiel = GameIdentity.Read(Payload(2304)) ?? throw new Exception("Identity missing");
        Check(lumiel.CharacterName == "TestHero", lumiel.Server == "Lumiel", lumiel.Region == "GLOBAL_EU", "EU Lumiel identity");
        Check(GameIdentity.Read(Payload(1101))?.Region == "GLOBAL_NA" && GameIdentity.Read(Payload(2104))?.Region == "GLOBAL_NA", "NA-East region codes");
        Check(GameIdentity.Read(Payload(1201)) is { Server: "Siel", Region: "GLOBAL_NAW" }, GameIdentity.Read(Payload(2204)) is { Server: "Lumiel", Region: "GLOBAL_NAW" }, "NA-West is its own region, with the same server names");
        Check(GameIdentity.Read(Payload(2404))?.Region == "GLOBAL_SA" && GameIdentity.Read(Payload(2504))?.Region == "GLOBAL_ASIA", "SA/Asia codes");
        Check(GameIdentity.Read(Payload(2360)) is { Server: null, Region: "GLOBAL_EU" }, "unmapped server never guessed");
        // The Global servers as PLAYNC lists them.
        Check(GameIdentity.Read(Payload(1321)) is { Server: "Gauss" } && GameIdentity.Read(Payload(1322)) is { Server: "Lamuatan" } && GameIdentity.Read(Payload(1323)) is { Server: "Nathara" }
            && GameIdentity.Read(Payload(2321)) is { Server: "Agnita" } && GameIdentity.Read(Payload(2322)) is { Server: "Atiel" } && GameIdentity.Read(Payload(2323)) is { Server: "Tassin" }
            && GameIdentity.Read(Payload(1311)) is { Server: "Tahavatha" } && GameIdentity.Read(Payload(1315)) is { Server: "Kasaka" }, "Global server names");
        Check(GameIdentity.Read(Payload(1324)) is { Server: null }, GameIdentity.Read(Payload(2324)) is { Server: null }, "a server past PLAYNC's list is not guessed");
        // The backend names every server it lists, also one this build names otherwise (#93): letters only, each name
        // at one place of its race. A name this build gives another place stays there unless the backend names that
        // place otherwise; the next answer replaces what the last one named, and this build's names are the fallback.
        GameIdentity.Learn([(1324, "Ozryn"), (1124, "Ozryn"), (1125, "Ozryn"), (1326, "Bad Name"), (1321, "Poetra"), (2224, "Lumiel"), (2325, "Vexhal"), (999, "Low"), (1306, "Ariel"), (1104, "Fregion"), (1107, "Kaisinel")]);
        Check(GameIdentity.Read(Payload(1324)) is { Server: "Ozryn", Region: "GLOBAL_EU" } && GameIdentity.ServerOf(1124).Server == "Ozryn" && GameIdentity.ServerOf(1125).Server is null
            && GameIdentity.ServerOf(1326).Server is null && GameIdentity.ServerOf(2325) == ("Vexhal", "GLOBAL_EU"), "servers past this build's lists, as the backend names them");
        Check(GameIdentity.Read(Payload(1321)) is { Server: "Poetra" }, GameIdentity.ServerOf(1221).Server == "Gauss", GameIdentity.ServerOf(1306).Server == "Ariel", "the backend's name wins for the servers it lists; the others keep this build's");
        Check(GameIdentity.ServerOf(2224).Server is null, GameIdentity.ServerOf(2204).Server == "Lumiel", "a name this build gives another place is not taken over by the backend");
        Check(GameIdentity.ServerOf(1104).Server == "Fregion" && GameIdentity.ServerOf(1107).Server == "Kaisinel", "a place the backend renamed frees its old name for the place the backend gives it");
        GameIdentity.Learn([]);
        Check(GameIdentity.ServerOf(1324).Server is null, GameIdentity.ServerOf(2325).Server is null, GameIdentity.ServerOf(1321).Server == "Gauss", "a newer answer replaces the names");
        Check(GameIdentity.Read(Payload(2004)) is { Region: null }, "unmapped region never guessed");
        Check(GameIdentity.Read(Payload(2304, "$tutorial")) is null, "placeholder character rejected");
        // Class ids are a structural check: 5 to 48, where the reference decoder maps 45 to 48 to the Brawler.
        Check(new uint[] { 5, 25, 45, 48 }.All(job => GameIdentity.Read(Payload(2304, job: job))?.Class == job), new uint[] { 0, 4, 49, 1000 }.All(job => GameIdentity.Read(Payload(2304, job: job)) is null), "class range includes the Brawler");
        var bytes = Payload(2304); bytes[7] = 255; Check(GameIdentity.Read(bytes) is null, "invalid UTF8 rejected");
        bytes = Payload(2304); for (var i = 0; i < bytes.Length; i++) Check(GameIdentity.Read(bytes.AsSpan(0, i)) is null, "truncated identity rejected");
        var decoder = new OdyleDecoder(); var observed = new List<GameIdentity>(); decoder.IdentityObserved += observed.Add;
        bytes = Payload(2304); var frame = new byte[3 + bytes.Length]; frame[0] = (byte)(bytes.Length + 6); frame[1] = 0x33; frame[2] = 0x36; bytes.CopyTo(frame, 3);
        decoder.Feed(frame.AsSpan(0, 4)); decoder.Feed(frame.AsSpan(4));
        Check(observed.Single().Server == "Lumiel", "fragmented own identity forwarded");
        Console.WriteLine("Automatic own-character identity, server mapping, region and malformed-input checks passed.");
    }
}
