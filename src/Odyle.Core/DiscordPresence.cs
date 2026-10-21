using System.Text;
using System.Text.Json;

namespace Odyle.Core;

// What Discord shows in the player's status while they play (Settings → Window, off by default):
// the character with its Odyle, then the run they go to within the hour, or their cubes and when the
// natural pool is full. Since: when the game's connection was found, for Discord's elapsed time.
public sealed record DiscordActivity(string Details, string State, DateTimeOffset? Since);

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
    private static string Fit(string text) => text.Length > MaxText ? text[..(MaxText - 1)] + "…" : text.Length < 2 ? text.PadRight(2) : text;

    public static string HandshakeJson(string application) => JsonSerializer.Serialize(new { v = 1, client_id = application });
    public static string SetActivityJson(int pid, DiscordActivity activity, string nonce) => JsonSerializer.Serialize(new
    {
        cmd = "SET_ACTIVITY", nonce,
        args = new
        {
            pid,
            activity = activity.Since is { } since
                ? (object)new { details = activity.Details, state = activity.State, timestamps = new { start = since.ToUnixTimeMilliseconds() } }
                : new { details = activity.Details, state = activity.State },
        },
    });

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
