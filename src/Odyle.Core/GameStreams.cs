using static Odyle.Core.Localizer;

namespace Odyle.Core;
public enum CaptureStatus { Waiting, Listening, Midstream, Problem }
// One TCP connection of the game as this PC receives it: Local is this PC, Remote the game server.
public sealed record TcpFlow(string Local, ushort LocalPort, string Remote, ushort RemotePort);
// The game's connections seen on one capture interface: reassembly and decoding per
// connection. Feed answers how many payload bytes reached a decoder, and only those
// count as game traffic: a connection blocked after a lost segment or a decoding error
// no longer keeps a value live, so 20 seconds without decodable bytes mark it stale.
public sealed class GameStreams(KnownOwners owners, Action<ExperimentalOdyleDecoder> attach, Action<CaptureStatus, string> status)
{
    private static readonly TimeSpan GapLimit = TimeSpan.FromSeconds(3);
    private sealed class Stream(bool aligned, KnownOwners owners)
    {
        public readonly TcpReceiveStream Tcp = new();
        public readonly ExperimentalOdyleDecoder Decoder = new(aligned, owners);
        public bool Blocked;
    }
    private readonly Dictionary<TcpFlow, Stream> streams = new();
    public int Count => streams.Count;
    public bool IsBlocked(TcpFlow flow) => streams.TryGetValue(flow, out var stream) && stream.Blocked;
    // One TCP segment of a game connection; flags is the TCP flag byte (FIN 1, SYN 2, RST 4).
    public int Feed(TcpFlow flow, uint sequence, byte flags, ReadOnlySpan<byte> payload)
    {
        // FIN/RST ends the stream. A closing connection's last packets must not open a new
        // one, or a relog would look like a midstream start.
        if ((flags & 5) != 0) { streams.Remove(flow); return 0; }
        if (!streams.TryGetValue(flow, out var stream))
        {
            var aligned = (flags & 2) != 0;
            if (!aligned && payload.Length == 0) return 0;
            streams[flow] = stream = new Stream(aligned, owners); attach(stream.Decoder);
            // Joining an established connection means the startup snapshot and identity already
            // passed; live updates still carry the total. A new connection replaces that warning.
            if (aligned) status(CaptureStatus.Listening, T("Listening to AION2 · waiting for the Odyle snapshot."));
            else status(CaptureStatus.Midstream, T("Started after login · the value appears at the next Odyle change; relog or change channel once so the game names your character."));
        }
        if (stream.Blocked) return 0;
        var decoded = 0;
        try { foreach (var chunk in stream.Tcp.Feed(sequence, payload)) { stream.Decoder.Feed(chunk); decoded += chunk.Length; } }
        catch (InvalidDataException)
        {
            // Only the damaged connection is blocked, until it closes; framing is never
            // restarted midstream. The game's other connections keep being read.
            Block(stream, T("Unsupported or incomplete packets · reconnect the game before retrying."));
            return 0;
        }
        return decoded;
    }
    // Every two seconds: connections the game no longer has are dropped, and one whose
    // missing segment did not arrive within three seconds is blocked like a damaged one.
    public void Maintain(IEnumerable<TcpFlow> live, DateTimeOffset now)
    {
        var current = live as ICollection<TcpFlow> ?? live.ToArray();
        foreach (var gone in streams.Keys.Where(flow => !current.Contains(flow)).ToArray()) streams.Remove(gone);
        foreach (var stream in streams.Values)
            if (!stream.Blocked && stream.Tcp.GapSince is { } gap && now - gap > GapLimit) Block(stream, T("Game packets were lost on the way · relog or change channel to read again."));
    }
    private void Block(Stream stream, string message) { stream.Blocked = true; stream.Tcp.Reset(); stream.Decoder.Reset(); status(CaptureStatus.Problem, message); }
}
