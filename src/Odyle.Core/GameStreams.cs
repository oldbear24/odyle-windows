using static Odyle.Core.Localizer;

namespace Odyle.Core;
// Resyncing: a connection lost bytes and waits for the next heartbeat; Resumed: it is read again.
public enum CaptureStatus { Waiting, Listening, Midstream, Resumed, Problem, Resyncing }
// One TCP connection of the game as this PC receives it: Local is this PC, Remote the game server.
public sealed record TcpFlow(string Local, ushort LocalPort, string Remote, ushort RemotePort);
// The game's connections seen on one capture interface: reassembly and decoding per
// connection. Feed answers how many payload bytes reached a decoder, and only those
// count as game traffic, so 20 seconds without decodable bytes mark a value stale.
// A lost segment that does not arrive within three seconds, a conflicting retransmission
// or bytes that do not frame resync the connection: reassembly starts again at the next
// segment and the decoder waits for the next protocol heartbeat, keeping the character it
// knows. The lost bytes may have held a character switch, so the status says Resyncing and
// the app holds the next reading until the game names its character. More than five resyncs in a minute block the connection until it closes, so a
// changed protocol shows up as a problem instead of a loop.
public sealed class GameStreams(Action<ExperimentalOdyleDecoder> attach, Action<CaptureStatus, string> status)
{
    private static readonly TimeSpan GapLimit = TimeSpan.FromSeconds(3), ResyncWindow = TimeSpan.FromMinutes(1);
    private const int ResyncLimit = 5;
    private sealed class Stream(bool aligned)
    {
        public readonly TcpReceiveStream Tcp = new();
        public readonly ExperimentalOdyleDecoder Decoder = new(aligned);
        public readonly Queue<DateTimeOffset> Resyncs = new();
        public bool Blocked, Resyncing;
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
            streams[flow] = stream = new Stream(aligned); attach(stream.Decoder);
            // Joining an established connection means the startup snapshot and identity already
            // passed; live updates still carry the total, and the game repeats the identity every
            // few minutes. A new connection replaces that warning.
            if (aligned) status(CaptureStatus.Listening, T("Listening to AION2 · waiting for the Odyle snapshot."));
            else status(CaptureStatus.Midstream, T("Started after login · the value appears at the next Odyle change; the game names your character within a few minutes, or at once after a relog or channel change."));
        }
        if (stream.Blocked) return 0;
        var decoded = 0;
        try { foreach (var chunk in stream.Tcp.Feed(sequence, payload)) { stream.Decoder.Feed(chunk); decoded += chunk.Length; } }
        catch (InvalidDataException)
        {
            // Only the damaged connection resyncs; the game's other connections keep being read.
            Resync(stream, DateTimeOffset.UtcNow, T("Unreadable game packets · resyncing with the game…"));
            return decoded;
        }
        if (stream.Resyncing && stream.Decoder.Synchronized) { stream.Resyncing = false; status(CaptureStatus.Resumed, T("Back in sync with the game · the value updates at the next Odyle change.")); }
        return decoded;
    }
    // Every two seconds: connections the game no longer has are dropped, and one whose
    // missing segment did not arrive within three seconds resyncs at the next segment.
    public void Maintain(IEnumerable<TcpFlow> live, DateTimeOffset now)
    {
        var current = live as ICollection<TcpFlow> ?? live.ToArray();
        foreach (var gone in streams.Keys.Where(flow => !current.Contains(flow)).ToArray()) streams.Remove(gone);
        foreach (var stream in streams.Values)
            if (!stream.Blocked && stream.Tcp.GapSince is { } gap && now - gap > GapLimit) Resync(stream, now, T("Game packets were lost on the way · resyncing with the game…"));
    }
    private void Resync(Stream stream, DateTimeOffset now, string message)
    {
        while (stream.Resyncs.TryPeek(out var first) && now - first > ResyncWindow) stream.Resyncs.Dequeue();
        stream.Resyncs.Enqueue(now);
        if (stream.Resyncs.Count > ResyncLimit) { Block(stream, T("Unsupported or incomplete packets · reconnect the game before retrying.")); return; }
        stream.Tcp.Reset(); stream.Decoder.Resync(); stream.Resyncing = true; status(CaptureStatus.Resyncing, message);
    }
    private void Block(Stream stream, string message) { stream.Blocked = true; stream.Tcp.Reset(); stream.Decoder.Reset(); status(CaptureStatus.Problem, message); }
}
