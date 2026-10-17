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
        Check(lumiel.CharacterName == "TestHero" && lumiel.Server == "Lumiel" && lumiel.Region == "GLOBAL_EU", "EU Lumiel identity");
        Check(GameIdentity.Read(Payload(1101))?.Region == "GLOBAL_NA" && GameIdentity.Read(Payload(2104))?.Region == "GLOBAL_NA", "NA-East region codes");
        Check(GameIdentity.Read(Payload(1201)) is { Server: "Siel", Region: "GLOBAL_NAW" } && GameIdentity.Read(Payload(2204)) is { Server: "Lumiel", Region: "GLOBAL_NAW" }, "NA-West is its own region, with the same server names");
        Check(GameIdentity.Read(Payload(1201))!.RegionFor(false) == "GLOBAL_NA" && GameIdentity.Read(Payload(1201))!.RegionFor(true) == "GLOBAL_NAW" && GameIdentity.Read(Payload(2304))!.RegionFor(false) == "GLOBAL_EU", "a server without NA-West saves those characters as NA");
        Check(GameIdentity.Read(Payload(2404))?.Region == "GLOBAL_SA" && GameIdentity.Read(Payload(2504))?.Region == "GLOBAL_ASIA", "SA/Asia codes");
        Check(GameIdentity.Read(Payload(2360)) is { Server: null, Region: "GLOBAL_EU" }, "unmapped server never guessed");
        Check(GameIdentity.Read(Payload(2004)) is { Region: null }, "unmapped region never guessed");
        Check(GameIdentity.Read(Payload(2304, "$tutorial")) is null, "placeholder character rejected");
        // Class ids are a structural check: 5 to 48, where the reference decoder maps 45 to 48 to the Brawler.
        Check(new uint[] { 5, 25, 45, 48 }.All(job => GameIdentity.Read(Payload(2304, job: job))?.Class == job) && new uint[] { 0, 4, 49, 1000 }.All(job => GameIdentity.Read(Payload(2304, job: job)) is null), "class range includes the Brawler");
        var bytes = Payload(2304); bytes[7] = 255; Check(GameIdentity.Read(bytes) is null, "invalid UTF8 rejected");
        bytes = Payload(2304); for (var i = 0; i < bytes.Length; i++) Check(GameIdentity.Read(bytes.AsSpan(0, i)) is null, "truncated identity rejected");
        var decoder = new ExperimentalOdyleDecoder(); var observed = new List<GameIdentity>(); decoder.IdentityObserved += observed.Add;
        bytes = Payload(2304); var frame = new byte[3 + bytes.Length]; frame[0] = (byte)(bytes.Length + 6); frame[1] = 0x33; frame[2] = 0x36; bytes.CopyTo(frame, 3);
        decoder.Feed(frame.AsSpan(0, 4)); decoder.Feed(frame.AsSpan(4));
        Check(observed.Single().Server == "Lumiel", "fragmented own identity forwarded");
        Console.WriteLine("Automatic own-character identity, server mapping, region and malformed-input checks passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Identity check failed: " + message); }
}
