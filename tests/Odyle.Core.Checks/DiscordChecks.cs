using System.Text;
using Odyle.Core;

// The Discord status (DiscordPresence.cs): the server's Discord application, Discord's pipe frames
// and what the status says; and the jump list's --do actions (StartupCommand).
static class DiscordChecks
{
    public static async Task Run()
    {
        const string authMethods = """{"password":{"enabled":false},"oauth2":{"enabled":true,"providers":[{"name":"discord","displayName":"Discord","state":"s","authURL":"https://discord.com/api/oauth2/authorize?client_id=123456789012345678&code_challenge=x&response_type=code&scope=identify+email&state=s&redirect_uri="}]}}""";
        Check(DiscordPresence.ApplicationOf(authMethods) == "123456789012345678", "the Discord application is the client_id the server signs in with");
        Check(DiscordPresence.ApplicationOf(authMethods.Replace("123456789012345678", "test-application")) is null && DiscordPresence.ApplicationOf(authMethods.Replace("\"discord\"", "\"google\"")) is null
            && DiscordPresence.ApplicationOf("""{"oauth2":{"enabled":false,"providers":[]}}""") is null && DiscordPresence.ApplicationOf("not json") is null, "no Discord sign-in, or an id that is no Discord id, gives no application");

        var frame = DiscordPresence.Frame(DiscordPresence.Handshake, DiscordPresence.HandshakeJson("123456789012345678"));
        Check(frame[0] == 0 && BitConverter.ToInt32(frame, 4) == frame.Length - 8 && Encoding.UTF8.GetString(frame, 8, frame.Length - 8) == """{"v":1,"client_id":"123456789012345678"}""", "the handshake frame: opcode, length, JSON");
        using (var stream = new MemoryStream([.. DiscordPresence.Frame(DiscordPresence.Ping, "{\"a\":1}"), .. DiscordPresence.Frame(DiscordPresence.Message, "{}")]))
        {
            var first = await DiscordPresence.ReadFrameAsync(stream, CancellationToken.None); var second = await DiscordPresence.ReadFrameAsync(stream, CancellationToken.None);
            Check(first == (DiscordPresence.Ping, "{\"a\":1}") && second == (DiscordPresence.Message, "{}") && await DiscordPresence.ReadFrameAsync(stream, CancellationToken.None) is null, "frames are read back one by one, then the end");
        }
        using (var huge = new MemoryStream([1, 0, 0, 0, 0, 0, 0, 1]))
        {
            var refused = false; try { await DiscordPresence.ReadFrameAsync(huge, CancellationToken.None); } catch (InvalidDataException) { refused = true; }
            Check(refused, "a frame of megabytes ends the conversation");
        }
        Check(DiscordPresence.Answer(DiscordPresence.Message, """{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}""") == (true, null), "READY after the handshake");
        Check(DiscordPresence.Answer(DiscordPresence.Close, """{"code":4000,"message":"Invalid Client ID"}""") == (false, "Invalid Client ID"), "a closing Discord says why");
        Check(DiscordPresence.Answer(DiscordPresence.Message, """{"cmd":"SET_ACTIVITY","evt":"ERROR","data":{"code":4002,"message":"bad"}}""") == (false, "bad")
            && DiscordPresence.Answer(DiscordPresence.Message, """{"cmd":"SET_ACTIVITY","data":{}}""") == (false, null), "an error is noted, an answer is not");
        var since = DateTimeOffset.Parse("2026-10-21T18:00:00Z");
        using (var set = System.Text.Json.JsonDocument.Parse(DiscordPresence.SetActivityJson(42, new DiscordActivity("Lumina · 820 Odyle", "10 cubes", since), "n")))
        {
            var root = set.RootElement; var activity = root.GetProperty("args").GetProperty("activity");
            Check(root.GetProperty("cmd").GetString() == "SET_ACTIVITY" && root.GetProperty("nonce").GetString() == "n" && root.GetProperty("args").GetProperty("pid").GetInt32() == 42
                && activity.GetProperty("details").GetString() == "Lumina · 820 Odyle" && activity.GetProperty("state").GetString() == "10 cubes" && activity.GetProperty("timestamps").GetProperty("start").GetInt64() == 1792605600000, "SET_ACTIVITY with the elapsed time from the game's start");
        }
        Check(!DiscordPresence.SetActivityJson(42, new DiscordActivity("ab", "cd", null), "n").Contains("timestamps"), "no start, no elapsed time");
        // Pictures and buttons from the server's public address: the game's portrait, Odyle's icon, the armory profile and Odyle.
        Localizer.Use("en");
        var lumina = new Character("c1", "u", "Lumina", "Lumiel", "GLOBAL_EU", 820, true, true, "", Level: 45, ClassName: "Chanter", Portrait: "portrait_ab12cd.png", OfficialId: "AbCdEfGh12_-==", OfficialRegion: "eu", OfficialServerId: 2304);
        var dressed = DiscordPresence.Dress(new DiscordActivity("Lumina · 820 Odyle", "10 cubes", since), "https://odyle.example", lumina);
        Check(dressed is { LargeImage: "https://odyle.example/api/files/characters/c1/portrait_ab12cd.png", LargeText: "Lumina · Chanter · Lv 45 · Lumiel", SmallImage: "https://odyle.example/icon-512.png", SmallText: "Odyle", ArmoryUrl: "https://odyle.example/#/armory/eu/2304/AbCdEfGh12_-==", HomeUrl: "https://odyle.example/" }, "the portrait, Odyle's icon, the armory profile and Odyle: " + dressed);
        var plain = DiscordPresence.Dress(new DiscordActivity("ab", "cd", null), "https://odyle.example", lumina with { Portrait = null, OfficialId = null, Level = 0, ClassName = null });
        Check(plain is { LargeImage: "https://odyle.example/icon-512.png", LargeText: "Lumina · Lumiel · EU", SmallImage: null, ArmoryUrl: "https://odyle.example/#/armory/find/GLOBAL_EU/Lumiel/Lumina" }, "without a portrait Odyle's icon, and the armory searches for the character: " + plain);
        Check(DiscordPresence.Dress(new DiscordActivity("ab", "cd", null), "http://127.0.0.1:8090", lumina) == new DiscordActivity("ab", "cd", null), "a local test server gets no pictures and no buttons");
        // A backend that serves paths (dashboard 0.49.0, #71) gets the profile's own address, which link previews show with the character.
        Check(DiscordPresence.Dress(new DiscordActivity("ab", "cd", null), "https://odyle.example", lumina, paths: true).ArmoryUrl == "https://odyle.example/armory/eu/2304/AbCdEfGh12_-=="
            && DiscordPresence.Dress(new DiscordActivity("ab", "cd", null), "https://odyle.example/", lumina with { OfficialId = null }, paths: true).ArmoryUrl == "https://odyle.example/armory/find/GLOBAL_EU/Lumiel/Lumina", "with paths the armory button opens the profile's own address");
        Check(DiscordPresence.ArmoryPath(lumina with { OfficialId = null, CharacterName = new string('x', 61) }) is null && DiscordPresence.ArmoryPath(lumina with { OfficialId = "../../x" }) == "/armory/find/GLOBAL_EU/Lumiel/Lumina", "only armory pages the dashboard accepts are linked");
        using (var set = System.Text.Json.JsonDocument.Parse(DiscordPresence.SetActivityJson(42, dressed, "n")))
        {
            var activity = set.RootElement.GetProperty("args").GetProperty("activity"); var assets = activity.GetProperty("assets"); var buttons = activity.GetProperty("buttons");
            Check(assets.GetProperty("large_image").GetString() == dressed.LargeImage && assets.GetProperty("large_text").GetString() == dressed.LargeText && assets.GetProperty("small_image").GetString() == dressed.SmallImage
                && buttons.GetArrayLength() == 2 && buttons[0].GetProperty("label").GetString() == "View in the armory" && buttons[0].GetProperty("url").GetString() == dressed.ArmoryUrl && buttons[1].GetProperty("label").GetString() == "Open Odyle", "SET_ACTIVITY carries the pictures and two buttons");
        }
        Check(!DiscordPresence.SetActivityJson(42, new DiscordActivity("ab", "cd", null), "n").Contains("assets") && !DiscordPresence.SetActivityJson(42, new DiscordActivity("ab", "cd", null), "n").Contains("buttons"), "no pictures, no buttons");

        try
        {
            Localizer.Use("en");
            var rules = ClockRules.Default; var now = DateTimeOffset.Parse("2026-10-15T05:00:00Z"); var read = DateTimeOffset.Parse("2026-10-14T22:30:00Z");
            var room = new Room("room", "Raid night", 80, "reporter", true);
            NextRun Run(int minutes, bool going) => new(room, new PlannedRun("r1", "Fire Temple", "", Attendees: ["u"]), now.AddMinutes(minutes), going, 3);
            Check(DiscordPresence.Activity("Lumina", 820, 80, null, null, now, since) == new DiscordActivity("Lumina · 820 Odyle", "10 cubes", since), "the character with its Odyle, then its cubes");
            var settled = OdyleClock.Of(500, 300, read, now, rules, null, 600)!;
            Check(DiscordPresence.Activity("Lumina", 830, 80, settled, null, now, null).State == "10 cubes · full ≈ " + DateTimeOffset.Parse("2026-10-17T19:00:00Z").ToLocalTime().ToString("ddd HH:mm", Localizer.Culture), "a known cap says when the natural pool is full");
            Check(DiscordPresence.Activity("Lumina", 840, 80, OdyleClock.Of(840, 0, read, now, rules, 20, 840), null, now, null).State == "10 cubes · natural pool full", "a full pool says so");
            Check(DiscordPresence.Activity("Lumina", 830, 80, OdyleClock.Of(500, 300, read, now, rules, null, 500), null, now, null).State == "10 cubes", "an open cap names no time");
            Check(DiscordPresence.Activity("Lumina", 820, 80, null, Run(25, true), now, null).State == "Fire Temple · in 25m" && DiscordPresence.Activity("Lumina", 820, 80, null, Run(0, true), now, null).State == "Fire Temple · starting now", "a run you go to within the hour leads");
            Check(DiscordPresence.Activity("Lumina", 820, 80, null, Run(25, false), now, null).State == "10 cubes" && DiscordPresence.Activity("Lumina", 820, 80, null, Run(90, true), now, null).State == "10 cubes", "a run you do not go to, or one later, does not");
            var longName = DiscordPresence.Activity(new string('x', 200), 1, 0, null, null, now, null);
            Check(longName.Details.Length == DiscordPresence.MaxText && longName.Details.EndsWith('…') && longName.State == "0 cubes", "Discord's 128 characters are kept");
            Localizer.Use("cs");
            var czech = DiscordPresence.Activity("Lumina", 1820, 80, OdyleClock.Of(840, 0, read, now, rules, 20, 840), null, now, null);
            Check(czech.Details == "Lumina · " + 1820.ToString("N0", Localizer.Culture) + " Odyle" && czech.State == "22 kostek · přirozená zásoba plná", "the status speaks Czech: " + czech);
        }
        finally { Localizer.Use("en"); }

        // The jump list: "<Odyle.exe>" --do <action>, handed to a running Odyle over the pipe.
        Check(StartupCommand.ActionOf(["--do", "compact"]) == "compact" && StartupCommand.ActionOf(["--DO", "Reader"]) == "reader" && StartupCommand.ActionOf(["--do"]) is null && StartupCommand.ActionOf(["--do", "format-disk"]) is null && StartupCommand.ActionOf(["--tray"]) is null, "the jump list argument names a known action");
        Check(StartupCommand.PipeAction("odyle-do:tasks") == "tasks" && StartupCommand.PipeAction("odyle-do:other") is null && StartupCommand.PipeAction("odyle://pair?link=x") is null, "the pipe tells a jump list action from an Open in Odyle link");
        Console.WriteLine("The server's Discord application, Discord pipe frames and answers, the Discord status lines and the jump list actions passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Discord check failed: " + message); }
}
