using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static Odyle.Core.Localizer;

namespace Odyle.Core;
// One row of the operating system's TCP connection table, never a packet: the remote port,
// whether a game process owns it, the owning process's name and whether it runs over IPv6.
public sealed record TcpConnection(bool Game, string Process, ushort RemotePort, bool IPv6);
// The game's own connection is IPv4 to port 13328, owned by the AION2 process. When the game
// runs for a minute without such a connection, the connection table often says why.
public static class GameConnection
{
    public const ushort Port = 13328;
    // The game client's process (AION2; AION-2 in some builds).
    public static bool IsGameProcess(string name) => name.Contains("AION2", StringComparison.OrdinalIgnoreCase) || name.Contains("AION-2", StringComparison.OrdinalIgnoreCase);
    // A dual-mode socket connected to an IPv4 server is listed in the IPv6 table with an
    // IPv4-mapped address (::ffff:a.b.c.d), but its packets are IPv4: the IPv4 address, or null
    // for a real IPv6 address.
    public static IPAddress? MappedIPv4(ReadOnlySpan<byte> address)
    {
        if (address.Length != 16) return null;
        var ip = new IPAddress(address);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : null;
    }
    // Why a running game shows no IPv4 connection to the game port, or null when the table does
    // not say: the game connected over IPv6, another program (an accelerator, a proxy or a renamed
    // client) holds the connection to the game port, or the game is connected on other ports only.
    // Ports 80 and 443 are the launcher's and the game's web requests and say nothing.
    public static string? Explain(IEnumerable<TcpConnection> connections)
    {
        var list = connections.ToArray();
        if (list.Any(c => c.Game && c.IPv6 && c.RemotePort == Port)) return T("AION2 connects to the game server over IPv6, which the reader cannot read yet.");
        if (list.FirstOrDefault(c => !c.Game && c.RemotePort == Port) is { } other) return T("The game connection runs through {0}, not AION2 · an accelerator or proxy hides it from the reader.", other.Process);
        var ports = list.Where(c => c.Game && c.RemotePort is not (Port or 80 or 443)).Select(c => c.RemotePort).Distinct().Order().Take(3).ToArray();
        return ports.Length > 0 ? T("AION2 is connected, but not on the game port 13328 (ports {0}) · the reader only reads 13328.", string.Join(", ", ports)) : null;
    }
}
// Settings → Copy diagnostics: a short plain-text report for a bug report, put on the clipboard
// and nowhere else. The log never holds tokens, device keys or pairing codes (RotatingLog.Redact);
// the report also masks the account id, this PC's name and the Windows user folder.
public static class Diagnostics
{
    private static readonly (Regex Pattern, string Mask)[] Personal = [
        (new(@"\baccount [a-z0-9]{15}\b", RegexOptions.CultureInvariant), "account [account]"),
        (new(@"\bdevice ""[^""\r\n]*""", RegexOptions.CultureInvariant), "device \"[this PC]\""),
        (new(@"(?i)\b([a-z]:\\users\\)[^\\\r\n""']+(?=\\)", RegexOptions.CultureInvariant), "$1[user]")];
    public static string Redact(string text)
    {
        text = RotatingLog.Redact(text);
        foreach (var (pattern, mask) in Personal) text = pattern.Replace(text, mask);
        return text;
    }
    public static string Report(DateTimeOffset now, IEnumerable<(string Label, string Value)> facts, IEnumerable<string> readerStates, IReadOnlyCollection<string> logTail)
    {
        var text = new StringBuilder($"Odyle diagnostics · {now:yyyy-MM-dd HH:mm:ss zzz}").AppendLine();
        foreach (var (label, value) in facts) text.Append(label).Append(": ").AppendLine(value);
        var states = readerStates.ToArray();
        text.AppendLine("Last reader states:");
        foreach (var state in states.Length == 0 ? ["none this run"] : states) text.Append("  ").AppendLine(state);
        text.AppendLine($"Log, last {logTail.Count} lines:");
        foreach (var line in logTail) text.Append("  ").AppendLine(line);
        return Redact(text.ToString());
    }
}
