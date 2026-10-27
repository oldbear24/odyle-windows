using System.Text;
using System.Text.Json;

namespace Odyle.Core;

// What Discord shows in the player's status while they play (Settings → Window, off by default):
// the character with its Odyle, then the run they go to within the hour, or their cubes and when the
// natural pool is full. Since: when the game's connection was found, for Discord's elapsed time.
// Pictures and links come from the server's public address (Dress): the character's portrait from the
// game (else Odyle's icon) and Odyle's icon beside it, and buttons to its armory profile and to Odyle.
public sealed record DiscordActivity(string Details, string State, DateTimeOffset? Since, string? LargeImage = null, string? LargeText = null,
    string? SmallImage = null, string? SmallText = null, string? ArmoryUrl = null, string? HomeUrl = null);

// Discord's local RPC (the discord-ipc-N pipe of the Discord app on this PC): frames of an opcode
// and a length, both 32-bit little-endian, then UTF-8 JSON. A handshake names the Discord
// application, SET_ACTIVITY sets the status, and closing the pipe clears it.
public static class DiscordPresence
{
    public const int Handshake = 0, Message = 1, Close = 2, Ping = 3, Pong = 4;
    // Discord drops texts shorter than 2 characters and longer than 128.
    public const int MaxText = 128;
    // A run you go to that starts within this long leads the status.
    public static readonly TimeSpan RunAhead = TimeSpan.FromHours(1);
    private const int MaxFrame = 64 * 1024;

    // The Discord application the server signs in with: the client_id of the authorize URL in
    // PocketBase's public auth-methods answer, a Discord snowflake (17 to 20 digits), or null when
    // the server has no Discord sign-in.
    public static string? ApplicationOf(string authMethods)
    {
        try
        {
            using var document = JsonDocument.Parse(authMethods);
            if (!document.RootElement.TryGetProperty("oauth2", out var oauth) || !oauth.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array) return null;
            foreach (var provider in providers.EnumerateArray())
            {
                if (!provider.TryGetProperty("name", out var name) || name.GetString() != "discord") continue;
                var url = provider.TryGetProperty("authURL", out var a) ? a.GetString() : provider.TryGetProperty("authUrl", out var b) ? b.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
                foreach (var pair in uri.Query.TrimStart('?').Split('&'))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == "client_id") { var id = Uri.UnescapeDataString(parts[1]); return id.Length is >= 17 and <= 20 && id.All(char.IsAsciiDigit) ? id : null; }
                }
                return null;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    public static DiscordActivity Activity(string character, int value, int cost, ClockState? clock, NextRun? run, DateTimeOffset now, DateTimeOffset? since)
    {
        var details = Localizer.T("{0} · {1:N0} Odyle", character, value);
        string state;
        if (run is { Going: true } && run.StartsAt - now <= RunAhead)
        {
            var left = run.StartsAt - now;
            state = $"{run.Run.Dungeon} · {(left <= TimeSpan.FromMinutes(1) ? Localizer.T("starting now") : Localizer.T("in {0}", Checklist.Remaining(left)))}";
        }
        else
        {
            state = Localizer.Tn(cost > 0 ? value / cost : 0, "{0} cube", "{0} cubes");
            if (clock is { Full: true, Settled: true }) state += " · " + Localizer.T("natural pool full");
            else if (clock is { Settled: true } && clock.Fills.FirstOrDefault(f => f.At is not null)?.At is { } full)
                state += " · " + Localizer.T("full ≈ {0}", full.ToLocalTime().ToString("ddd HH:mm", Localizer.Culture));
        }
        return new DiscordActivity(Fit(details), Fit(state), since);
    }
    // The pictures and buttons, all on the server's public HTTPS address so Discord can fetch and open
    // them (a local test server gets none): the portrait the server keeps from the game, Odyle's icon,
    // the character's armory profile (PLAYNC's, else the armory's search for it) and Odyle itself.
    public static DiscordActivity Dress(DiscordActivity activity, string endpoint, Character character)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var server) || server.Scheme != "https") return activity;
        var home = server.AbsoluteUri.TrimEnd('/');
        var icon = home + "/icon-512.png";
        var portrait = character.Portrait is { Length: > 0 } file ? $"{home}/api/files/characters/{Uri.EscapeDataString(character.Id)}/{Uri.EscapeDataString(file)}" : null;
        if (portrait is { Length: > MaxImage }) portrait = null;
        var about = character.ClassLine.Length > 0 ? $"{character.CharacterName} · {character.ClassLine} · {character.Server}" : $"{character.CharacterName} · {character.ServerLine}";
        return activity with
        {
            LargeImage = portrait ?? icon, LargeText = Fit(about),
            SmallImage = portrait is null ? null : icon, SmallText = portrait is null ? null : "Odyle",
            ArmoryUrl = ArmoryPath(character) is { } path ? home + "/#" + path : null, HomeUrl = home + "/",
        };
    }
    // The armory page of a character, as the dashboard's src/armory.js builds and accepts it.
    private static readonly System.Text.RegularExpressions.Regex ArmoryRoute = new(@"^/armory(?:/find/GLOBAL_(?:NAW?|EU|SA|ASIA)/[A-Za-z]{2,20}/[^/]{1,60}|/(?:nae|naw|eu|la|as)/[12]\d{3}/[A-Za-z0-9_-]{8,90}={0,2})$");
    public static string? ArmoryPath(Character c)
    {
        var path = c is { OfficialId.Length: > 0, OfficialRegion.Length: > 0, OfficialServerId: > 0 } ? $"/armory/{c.OfficialRegion}/{c.OfficialServerId}/{c.OfficialId}" : "";
        if (!ArmoryRoute.IsMatch(path)) path = $"/armory/find/{c.Region}/{c.Server}/{Uri.EscapeDataString(c.CharacterName)}";
        return ArmoryRoute.IsMatch(path) ? path : null;
    }
    // Discord takes image addresses of at most 256 characters, button labels of at most 32 and links of at most 512.
    private const int MaxImage = 256, MaxLabel = 32, MaxLink = 512;
    private static string Fit(string text) => text.Length > MaxText ? text[..(MaxText - 1)] + "…" : text.Length < 2 ? text.PadRight(2) : text;

    public static string HandshakeJson(string application) => JsonSerializer.Serialize(new { v = 1, client_id = application });
    public static string SetActivityJson(int pid, DiscordActivity activity, string nonce)
    {
        var shown = new Dictionary<string, object> { ["details"] = activity.Details, ["state"] = activity.State };
        if (activity.Since is { } since) shown["timestamps"] = new { start = since.ToUnixTimeMilliseconds() };
        var assets = new Dictionary<string, string>();
        if (activity.LargeImage is { } large) { assets["large_image"] = large; if (activity.LargeText is { } about) assets["large_text"] = about; }
        if (activity.SmallImage is { } small) { assets["small_image"] = small; if (activity.SmallText is { } label) assets["small_text"] = label; }
        if (assets.Count > 0) shown["assets"] = assets;
        var buttons = new[] { (Label: Localizer.T("View in the armory"), Url: activity.ArmoryUrl), (Label: Localizer.T("Open Odyle"), Url: activity.HomeUrl) }
            .Where(b => b.Url is { Length: > 0 and <= MaxLink } && b.Label.Length <= MaxLabel).Select(b => new { label = b.Label, url = b.Url }).ToArray();
        if (buttons.Length > 0) shown["buttons"] = buttons;
        return JsonSerializer.Serialize(new { cmd = "SET_ACTIVITY", nonce, args = new { pid, activity = shown } });
    }

    public static byte[] Frame(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json); var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode); BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }
    // The next frame, or null once the pipe ends; a frame longer than 64 KB ends the conversation.
    public static async Task<(int Opcode, string Json)?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[8];
        try { await stream.ReadExactlyAsync(header, token); } catch (EndOfStreamException) { return null; }
        var opcode = BitConverter.ToInt32(header, 0); var length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > MaxFrame) throw new InvalidDataException($"Discord sent a frame of {length} bytes.");
        var body = new byte[length];
        try { await stream.ReadExactlyAsync(body, token); } catch (EndOfStreamException) { return null; }
        return (opcode, Encoding.UTF8.GetString(body));
    }
    // What a frame from Discord says: READY after the handshake, an error, or nothing to note.
    public static (bool Ready, string? Error) Answer(int opcode, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (opcode == Close) return (false, Text("message") ?? "closed");
            if (Text("evt") == "READY") return (true, null);
            if (Text("evt") == "ERROR") return (false, root.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error");
            return (false, null);
        }
        catch (JsonException) { return (false, null); }
    }
}
