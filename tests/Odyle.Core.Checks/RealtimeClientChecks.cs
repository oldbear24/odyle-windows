using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Odyle.Core;

// The realtime connection against a stub server, offline (#82): server-sent events are read and
// dispatched, a failure of any kind connects again, a connection that does not answer or stays
// silent is dropped, a refused room falls back to the account's own topic and is subscribed again
// by the next ListenAsync with the same topics, and Reconnect drops the connection at once.
static class RealtimeClientChecks
{
    const string Own = "odyle.user.u";
    static readonly string[] Topics = [Own, "odyle.room.r", "odyle.presence.r"];
    public static async Task Run()
    {
        var server = new SseServer();
        using var api = new PartyApi("https://party.example", "token", null, server);
        // An exception of a type nobody listed (the old loop caught a fixed list) before the first connection.
        server.Throw = true;
        var realtime = new RealtimeClient(api) { Silence = TimeSpan.FromSeconds(1.5), ConnectTimeout = TimeSpan.FromMilliseconds(400), Delay = _ => TimeSpan.FromMilliseconds(20) };
        var connected = Channel.CreateUnbounded<bool>(); var dropped = Channel.CreateUnbounded<string>(); var messages = Channel.CreateUnbounded<(string Topic, RealtimeNews News)>();
        realtime.Connected += again => connected.Writer.TryWrite(again);
        realtime.Disconnected += () => dropped.Writer.TryWrite(realtime.LastProblem ?? "");
        realtime.Message += (topic, data) => messages.Writer.TryWrite((topic, RealtimeNews.Parse(data)));
        try
        {
            realtime.Start(Topics);
            var first = await Next(server.Connections, "a connection after an unlisted exception");
            Check(server.Thrown == 1, "the unlisted exception happened and the loop went on");
            // SSE: the connection's start subscribes every topic; a message split over two data lines is one event.
            first.Write("event: PB_CONNECT\ndata: {\"clientId\":\"one\"}\n\n");
            Check(!await Next(connected.Reader, "the first connection") && realtime.IsConnected && realtime.Subscribed.SequenceEqual(Topics) && server.Subscriptions.Last().SequenceEqual(Topics), "the first connection subscribes every topic");
            first.Write(": a comment\nevent: " + Own + "\ndata: {\"refresh\":\ndata: true}\n\n");
            var (topic, news) = await Next(messages.Reader, "a message");
            Check(topic == Own && news.Refresh, "a message split over data lines is read as one event");
            // Silence: no line for longer than the limit drops the connection; the next one is a reconnect.
            var silent = await Next(dropped.Reader, "a drop after the silence");
            Check(silent.Contains("silent") && !realtime.IsConnected, "a silent connection counts as dropped: " + silent);
            var second = await Next(server.Connections, "a connection after the silence");
            // A room the server refuses (403): the account's own topic is subscribed, and remembered as all it took.
            server.Refuse = true;
            second.Write("event: PB_CONNECT\ndata: {\"clientId\":\"two\"}\n\n");
            Check(await Next(connected.Reader, "the second connection") && realtime.Subscribed.SequenceEqual([Own]), "after a 403 only the account's own topic is subscribed");
            // The same topics again subscribe again once the server takes them, and only then stop asking.
            server.Refuse = false; var asked = server.Subscriptions.Count;
            await realtime.ListenAsync(Topics);
            Check(server.Subscriptions.Count == asked + 1 && realtime.Subscribed.SequenceEqual(Topics), "listening to the same topics after a 403 subscribes them again");
            await realtime.ListenAsync(Topics);
            Check(server.Subscriptions.Count == asked + 1, "topics already subscribed are not asked for again");
            // A server that accepts and never answers: the request gives up after the connect limit.
            server.Hang = true; second.End();
            Check((await Next(dropped.Reader, "a drop when the server closed")).Contains("closed"), "the server closing the stream is a drop");
            await Until(() => server.Hung >= 1 && (realtime.LastProblem ?? "").Contains("did not answer"), "a connection that does not answer is given up");
            server.Hang = false;
            var third = await Next(server.Connections, "a connection after the connect timeout");
            third.Write("event: PB_CONNECT\ndata: {\"clientId\":\"three\"}\n\n");
            Check(await Next(connected.Reader, "the third connection"), "connected again after the connect timeout");
            // Reconnect (the PC woke from sleep) drops the connection at once.
            realtime.Reconnect("The PC woke from sleep.");
            Check(await Next(dropped.Reader, "a drop on Reconnect") == "The PC woke from sleep.", "Reconnect drops the connection");
            var fourth = await Next(server.Connections, "a connection after Reconnect");
            fourth.Write("event: PB_CONNECT\ndata: {\"clientId\":\"four\"}\n\n");
            Check(await Next(connected.Reader, "the fourth connection"), "connected again after Reconnect");
            // An event the app cannot parse is a failure like any other: the connection is made again.
            fourth.Write("event: " + Own + "\ndata: {not json\n\n");
            await Next(dropped.Reader, "a drop after an unreadable event");
            var fifth = await Next(server.Connections, "a connection after an unreadable event");
            fifth.Write("event: PB_CONNECT\ndata: {\"clientId\":\"five\"}\n\n");
            Check(await Next(connected.Reader, "the fifth connection"), "connected again after an unreadable event");
        }
        finally { realtime.Dispose(); }
        var before = server.Requests;
        await Task.Delay(200);
        Check(server.Requests == before, "a disposed connection connects no more");
        Console.WriteLine("Realtime connection (stub server): events, reconnects, silence, connect timeout, 403 fallback and Reconnect passed.");
    }
    static async Task<T> Next<T>(ChannelReader<T> reader, string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return await reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new Exception("Realtime client check failed: no " + what + "."); }
    }
    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Check(condition(), what);
    }
    // PocketBase's realtime endpoints: GET opens a stream the check writes to; POST subscribes, refusing
    // room topics with 403 while Refuse is set. Throw fails the next GET with an unlisted exception and
    // Hang never answers GETs.
    sealed class SseServer : HttpMessageHandler
    {
        public readonly Channel<FeedStream> Channel = System.Threading.Channels.Channel.CreateUnbounded<FeedStream>();
        public ChannelReader<FeedStream> Connections => Channel.Reader;
        public readonly List<string[]> Subscriptions = [];
        public volatile bool Throw, Hang, Refuse;
        public int Thrown, Hung, Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            if (request.Method == HttpMethod.Post)
            {
                using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var topics = sent.RootElement.GetProperty("subscriptions").EnumerateArray().Select(t => t.GetString()!).ToArray();
                lock (Subscriptions) Subscriptions.Add(topics);
                if (Refuse && topics.Any(t => !t.StartsWith("odyle.user.", StringComparison.Ordinal)))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"status":403,"message":"You are not a member of this room."}""", Encoding.UTF8, "application/json") };
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (Throw) { Throw = false; Thrown++; throw new NotSupportedException("A failure of a kind nobody listed."); }
            if (Hang) { Interlocked.Increment(ref Hung); await Task.Delay(Timeout.Infinite, token); }
            var stream = new FeedStream(); Channel.Writer.TryWrite(stream);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }
    }
    // A response body the check writes lines into; End ends it as a server closing the connection.
    sealed class FeedStream : Stream
    {
        readonly Channel<byte[]> chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        byte[] current = []; int offset;
        public void Write(string text) => chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void End() => chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            while (offset >= current.Length)
            {
                if (!await chunks.Reader.WaitToReadAsync(token)) return 0;
                if (chunks.Reader.TryRead(out var next)) { current = next; offset = 0; }
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    static void Check(bool condition, string what) { if (!condition) throw new Exception("Realtime client check failed: " + what + "."); }
}
