using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;

// Passive capture of the game's incoming traffic through Npcap. A supervisor keeps one
// worker per capture interface: a worker that stops (adapter reset or removed, capture
// broken) is opened again after a short delay that grows while it keeps failing, the
// interface list is scanned again every 30 seconds for new adapters (VPN, accelerator,
// a reset network card), and everything restarts after the PC resumes from sleep. The
// status is the capture's as a whole: Problem only while no interface can capture. When
// AION2 runs but its connection does not show up, the waiting line says why if it can.
public sealed class PacketCapture : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task supervisor = Task.CompletedTask;
    public event Action<CaptureStatus, string>? Status;
    public event Action<PacketCandidate>? Candidate;
    public event Action<ulong>? CharacterChanged;
    public event Action<GameIdentity>? IdentityObserved;
    public event Action? SnapshotWithoutOdyle;
    public event Action<ushort, int>? FrameObserved;
    public event Action<OdyleUpdateObservation>? UpdateObserved;
    public event Action<OdyleFrameLayout>? LayoutObserved;
    public event Action<OdyleSnapshotObservation>? SnapshotObserved;
    public event Action<GameLocation>? LocationObserved;
    private long receivedPackets, gameBytes;
    public long ReceivedPackets => Interlocked.Read(ref receivedPackets);
    // Payload bytes that reached a decoder; blocked or unknown connections add nothing.
    public long GameBytes => Interlocked.Read(ref gameBytes);
    private sealed class Worker
    {
        public Task Task = Task.CompletedTask;
        public CancellationTokenSource? Stop;
        public volatile bool Capturing;
        public bool Unsupported;
        public int Failures;
        public bool Announced;
        public DateTimeOffset RetryAt, OpenedAt;
    }
    private readonly Dictionary<string, Worker> workers = new();
    private volatile bool restart, subscribed;
    private bool? reportedConnected;
    private int reportedCapturing = -1;
    [StructLayout(LayoutKind.Sequential)] private struct Device { public IntPtr Next, Name, Description, Addresses; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Filter { public uint Length; public IntPtr Instructions; }
    static PacketCapture()
    {
        NativeLibrary.SetDllImportResolver(typeof(PacketCapture).Assembly, (name, _, _) =>
        {
            if (name != "wpcap") return IntPtr.Zero;
            var path = Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll");
            if (!File.Exists(path)) throw new DllNotFoundException(T("Install Npcap from npcap.com before starting the packet reader."));
            var handle = LoadLibraryEx(path, IntPtr.Zero, 0x1100);
            if (handle == IntPtr.Zero) throw new DllNotFoundException(T("Npcap could not load. Check its installation and CPU architecture."));
            return handle;
        });
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW")] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_findalldevs(out IntPtr devices, StringBuilder error);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freealldevs(IntPtr devices);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern IntPtr pcap_open_live(string name, int snaplen, int promiscuous, int timeout, StringBuilder error);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_close(IntPtr handle);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_next_ex(IntPtr handle, out IntPtr header, out IntPtr bytes);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_datalink(IntPtr handle);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setnonblock(IntPtr handle, int enabled, StringBuilder error);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int pcap_compile(IntPtr handle, out Filter filter, string expression, int optimize, uint netmask);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setfilter(IntPtr handle, ref Filter filter);
    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freecode(ref Filter filter);
    [DllImport("iphlpapi")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    // Fails at once without Npcap or without any interface, so the window can say so;
    // after that the supervisor only adds, re-opens and restarts.
    public void Start()
    {
        var names = Interfaces();
        if (names.Count == 0) throw new InvalidOperationException(T("No Npcap capture interfaces available."));
        Log.Info($"Capture starts on {names.Count} Npcap interface(s).");
        SystemEvents.PowerModeChanged += PowerChanged; subscribed = true;
        supervisor = Task.Run(() => SuperviseAsync(names));
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Log.Info("The PC resumed from sleep; the capture restarts."); restart = true;
    }
    private static List<string> Interfaces() => Devices().Select(d => d.Name).ToList();
    // The capture interfaces Npcap lists, by description, for Copy diagnostics.
    public static string[] Adapters() => Devices().Select(d => d.Description is { Length: > 0 } text ? text : "unnamed").ToArray();
    private static List<(string Name, string? Description)> Devices()
    {
        var error = new StringBuilder(256);
        if (pcap_findalldevs(out var list, error) != 0) throw new InvalidOperationException(T("Npcap device discovery failed. Check capture permissions."));
        var devices = new List<(string, string?)>();
        try { for (var p = list; p != IntPtr.Zero; ) { var device = Marshal.PtrToStructure<Device>(p); var name = Marshal.PtrToStringAnsi(device.Name); if (name is not null) devices.Add((name, Marshal.PtrToStringAnsi(device.Description))); p = device.Next; } }
        finally { if (list != IntPtr.Zero) pcap_freealldevs(list); }
        return devices;
    }
    private async Task SuperviseAsync(List<string> names)
    {
        var token = cancellation.Token; var scanAt = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (restart) { restart = false; await StopWorkersAsync(); ForgetFlows(); scanAt = now; }
                if (now >= scanAt)
                {
                    scanAt = now.AddSeconds(30);
                    try { var found = Interfaces(); foreach (var added in found.Except(names)) Log.Info("New capture interface " + added + "."); names = found; }
                    catch (Exception error) when (error is InvalidOperationException or DllNotFoundException) { Log.Warn("Interface scan failed: " + error.Message); }
                }
                foreach (var name in names) Ensure(name, now);
                Report();
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { await StopWorkersAsync(); }
    }
    // Opens an interface that has no worker yet, or again once its retry time has come.
    private void Ensure(string name, DateTimeOffset now)
    {
        if (!workers.TryGetValue(name, out var worker)) workers[name] = worker = new Worker();
        else if (!worker.Task.IsCompleted || worker.Unsupported || now < worker.RetryAt) return;
        worker.Stop?.Dispose(); worker.Stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var stop = worker.Stop.Token;
        worker.Task = Task.Run(() => Receive(name, worker, stop));
    }
    private async Task StopWorkersAsync()
    {
        foreach (var worker in workers.Values) worker.Stop?.Cancel();
        await Task.WhenAll(workers.Values.Select(w => w.Task));
        foreach (var worker in workers.Values) worker.Stop?.Dispose();
        workers.Clear(); reportedCapturing = -1; reportedConnected = null; reportedTwoClients = false;
    }
    // A VPN or Bluetooth adapter that cannot be opened is normal: Problem only while no
    // interface captures at all once every one has tried. Otherwise one line says whether
    // the game is connected.
    private void Report()
    {
        var capturing = workers.Values.Count(w => w.Capturing);
        if (capturing == 0)
        {
            if (reportedCapturing != 0 && workers.Count > 0 && workers.Values.All(w => w.Task.IsCompleted))
            {
                reportedCapturing = 0; reportedConnected = null; Log.Warn("No capture interface works; retrying.");
                Status?.Invoke(CaptureStatus.Problem, T("No network interface can capture packets · check Npcap and its permissions. Retrying…"));
            }
            return;
        }
        if (reportedCapturing == 0) Log.Info($"Capturing again on {capturing} interface(s).");
        reportedCapturing = capturing;
        var connected = CurrentFlows(immediate: false).Length > 0; var now = DateTimeOffset.UtcNow;
        // Two game clients on this PC: their characters' messages would mix, so nothing is read
        // until one of them closes; the next report then starts over like a new connection.
        int clients; lock (flowsLock) clients = knownClients;
        if (clients > 1 != reportedTwoClients)
        {
            reportedTwoClients = clients > 1; reportedConnected = null;
            if (reportedTwoClients)
            {
                Log.Warn($"{clients} game clients are connected; reading pauses until one closes.");
                Status?.Invoke(CaptureStatus.TwoClients, T("Two game clients are running · Odyle reads one at a time. Close one to read again."));
                return;
            }
        }
        if (reportedTwoClients) return;
        if (connected != reportedConnected)
        {
            reportedConnected = connected; explainAt = now + ExplainAfter; explained = null;
            if (connected) Status?.Invoke(CaptureStatus.Listening, T("Listening to AION2 · waiting for the Odyle snapshot."));
            else Status?.Invoke(CaptureStatus.Waiting, T("Waiting for the AION2 game connection…"));
            return;
        }
        // About a minute without the game's connection while AION2 runs: the connection table
        // may say why (IPv6, an accelerator, other ports). Looked at again every 15 seconds.
        if (connected || now < explainAt) return;
        explainAt = now.AddSeconds(15);
        var reason = ExplainMissingConnection();
        if (reason == explained) return;
        explained = reason;
        Status?.Invoke(CaptureStatus.Waiting, reason ?? T("Waiting for the AION2 game connection…"));
    }
    private static readonly TimeSpan ExplainAfter = TimeSpan.FromMinutes(1);
    private DateTimeOffset explainAt;
    private string? explained;
    // Null while no AION2 process runs or the table does not say (GameConnection.Explain). A
    // failed look is no reason to stop the capture.
    private static string? ExplainMissingConnection()
    {
        var games = new HashSet<int>();
        try
        {
            foreach (var process in Process.GetProcesses())
                using (process) { try { if (GameConnection.IsGameProcess(process.ProcessName)) games.Add(process.Id); } catch (InvalidOperationException) { } }
            return games.Count == 0 ? null : GameConnection.Explain(GameConnections(games));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }
    // The game's connections, shared by every capture worker: refreshed every two
    // seconds, or at once when a packet arrives on an unknown flow (at most ten
    // times a second, a SYN-ACK always), instead of each worker enumerating processes on its own.
    private readonly object flowsLock = new();
    private TcpFlow[] knownFlows = Array.Empty<TcpFlow>();
    // How many game processes own those connections: more than one is two clients on this PC.
    private int knownClients;
    private bool reportedTwoClients;
    private DateTimeOffset knownFlowsAt = DateTimeOffset.MinValue;
    // A connection's SYN-ACK always gets its own look: the table read just before it may predate
    // the connection, and without the SYN-ACK the connection would be joined midstream and its
    // login snapshot lost. Only the capture filter's game-port packets get here, and at most
    // OpeningLooks a second, so a flood of SYN-ACKs cannot keep the table reads going.
    private const int OpeningLooks = 20;
    private DateTimeOffset openingLooksSince = DateTimeOffset.MinValue;
    private int openingLooks;
    private void ForgetFlows() { lock (flowsLock) knownFlowsAt = DateTimeOffset.MinValue; }
    private TcpFlow[] CurrentFlows(bool immediate, bool opening = false)
    {
        lock (flowsLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - knownFlowsAt < (immediate ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2)))
            {
                if (!opening) return knownFlows;
                if (now - openingLooksSince >= TimeSpan.FromSeconds(1)) { openingLooksSince = now; openingLooks = 0; }
                if (openingLooks >= OpeningLooks) return knownFlows;
                openingLooks++;
            }
            knownFlows = GameFlows(out knownClients); knownFlowsAt = now; return knownFlows;
        }
    }
    private static string ProcessName(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;
        var name = "process " + pid;
        try { using var process = Process.GetProcessById(pid); name = process.ProcessName; } catch { }
        return cache[pid] = name;
    }
    // The operating system's TCP connection table (connection metadata, never packets), one
    // address family at a time: IPv4 (2) with 24-byte rows or IPv6 (23) with 56-byte rows.
    private static void ReadTcpTable(int family, int rowSize, Action<IntPtr> each)
    {
        var size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 4, 0);
        if (size < 4 || size > 4194304) return;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, 4, 0) != 0) return;
            var count = Marshal.ReadInt32(table);
            if (count < 0 || count > (size - 4) / rowSize) return;
            for (var i = 0; i < count; i++) each(IntPtr.Add(table, 4 + i * rowSize));
        }
        finally { Marshal.FreeHGlobal(table); }
    }
    private static byte[] Address(IntPtr row, int offset) { var bytes = new byte[16]; Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, 16); return bytes; }
    private static ushort PortAt(IntPtr row, int offset) => (ushort)((Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1));
    private static TcpFlow[] GameFlows(out int clients)
    {
        var result = new List<TcpFlow>(); var processes = new Dictionary<int, string>(); var owners = new HashSet<int>();
        ReadTcpTable(2, 24, row =>
        {
            // Only live connections to the game port are checked against their owning
            // process; the game's HTTPS/login connections and every other program need
            // no process lookup at all. Live is SYN_SENT (3, where a connection is when its
            // SYN-ACK arrives), SYN_RCVD (4) or ESTABLISHED (5).
            if (result.Count >= 32 || PortAt(row, 16) != GameConnection.Port || Marshal.ReadInt32(row) is not (3 or 4 or 5)) return;
            var owner = Marshal.ReadInt32(row, 20);
            if (!GameConnection.IsGameProcess(ProcessName(owner, processes))) return;
            var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4))).ToString();
            var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 12))).ToString();
            result.Add(new(local, PortAt(row, 8), remote, PortAt(row, 16))); owners.Add(owner);
        });
        // A dual-mode socket to an IPv4 server is listed in the IPv6 table with IPv4-mapped
        // addresses; its packets are IPv4 and are read like the rows above.
        ReadTcpTable(23, 56, row =>
        {
            if (result.Count >= 32 || PortAt(row, 44) != GameConnection.Port || Marshal.ReadInt32(row, 48) is not (3 or 4 or 5)) return;
            var owner = Marshal.ReadInt32(row, 52);
            if (GameConnection.MappedIPv4(Address(row, 0)) is not { } local || GameConnection.MappedIPv4(Address(row, 24)) is not { } remote || !GameConnection.IsGameProcess(ProcessName(owner, processes))) return;
            result.Add(new(local.ToString(), PortAt(row, 20), remote.ToString(), PortAt(row, 44))); owners.Add(owner);
        });
        clients = owners.Count;
        return result.ToArray();
    }
    // What GameConnection.Explain needs: the live connections of the game's processes and every
    // live connection to the game port, over IPv4 and IPv6, with their owning process.
    private static List<TcpConnection> GameConnections(HashSet<int> games)
    {
        var result = new List<TcpConnection>(); var processes = new Dictionary<int, string>();
        void Add(IntPtr row, int state, int pid, int port, bool ipv6)
        {
            // An IPv4-mapped row of the IPv6 table is an IPv4 connection.
            if (ipv6 && GameConnection.MappedIPv4(Address(row, 24)) is not null) ipv6 = false;
            if (result.Count >= 64 || Marshal.ReadInt32(row, state) is not (3 or 4 or 5)) return;
            var owner = Marshal.ReadInt32(row, pid); var remote = PortAt(row, port); var game = games.Contains(owner);
            if (game || remote == GameConnection.Port) result.Add(new(game, ProcessName(owner, processes), remote, ipv6));
        }
        ReadTcpTable(2, 24, row => Add(row, 0, 20, 16, false));
        ReadTcpTable(23, 56, row => Add(row, 48, 52, 44, true));
        return result;
    }
    private void Attach(ExperimentalOdyleDecoder decoder)
    {
        decoder.Candidate += c => Candidate?.Invoke(c); decoder.CharacterChanged += id => CharacterChanged?.Invoke(id); decoder.FrameObserved += (opcode, size) => FrameObserved?.Invoke(opcode, size);
        decoder.UpdateObserved += value => UpdateObserved?.Invoke(value); decoder.LayoutObserved += value => LayoutObserved?.Invoke(value); decoder.IdentityObserved += value => IdentityObserved?.Invoke(value);
        decoder.SnapshotObserved += value => SnapshotObserved?.Invoke(value); decoder.SnapshotWithoutOdyle += () => SnapshotWithoutOdyle?.Invoke();
        decoder.LocationObserved += value => LocationObserved?.Invoke(value);
    }
    // Each game connection is decoded on one interface only, the first to deliver it (FlowOwners).
    private readonly FlowOwners owners = new(TimeSpan.FromSeconds(10));
    private void Receive(string name, Worker worker, CancellationToken token)
    {
        var error = new StringBuilder(256); var handle = pcap_open_live(name, 65536, 0, 200, error);
        worker.OpenedAt = DateTimeOffset.UtcNow;
        if (handle == IntPtr.Zero) { Stopped(name, worker, token, "cannot be opened: " + error); return; }
        var streams = new GameStreams(Attach, (kind, message) => Status?.Invoke(kind, message));
        var flows = Array.Empty<TcpFlow>(); var nextRefresh = DateTimeOffset.MinValue; var reason = "";
        try
        {
            if (pcap_setnonblock(handle, 1, error) != 0) throw new InvalidOperationException("Nonblocking capture is unavailable.");
            var linkType = pcap_datalink(handle);
            if (linkType is not (1 or 0 or 108 or 12 or 101)) { worker.Unsupported = true; return; }
            // Keep discovery open across reconnects. Every packet must still match a
            // connection owned by AION2 before decoding.
            if (pcap_compile(handle, out var filter, "tcp and src port 13328", 1, uint.MaxValue) != 0) throw new InvalidOperationException("Unable to configure game-only capture filter.");
            try { if (pcap_setfilter(handle, ref filter) != 0) throw new InvalidOperationException("Unable to apply capture filter."); }
            finally { pcap_freecode(ref filter); }
            worker.Capturing = true;
            if (worker.Announced) { worker.Announced = false; Log.Info($"Capture interface {name} works again."); }
            while (!token.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextRefresh) { nextRefresh = DateTimeOffset.UtcNow.AddSeconds(2); flows = CurrentFlows(immediate: false); owners.Keep(flows); streams.Maintain(flows, DateTimeOffset.UtcNow); }
                var received = pcap_next_ex(handle, out var header, out var pointer);
                if (received == 0) { Thread.Sleep(50); continue; }
                if (received < 0) throw new InvalidOperationException("Packet capture disconnected.");
                Interlocked.Increment(ref receivedPackets);
                var length = Marshal.ReadInt32(header, 8);
                if (length < 1 || length > 65536 || length != Marshal.ReadInt32(header, 12)) continue;
                var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
                try
                {
                    var ip = linkType == 1 ? 14 : linkType is 0 or 108 ? 4 : 0;
                    if (linkType == 1) { if (length < 14) continue; var type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(12)); if (type is 0x8100 or 0x88a8) { if (length < 18) continue; type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16)); ip += 4; } if (type != 0x0800) continue; }
                    if (length < ip + 20 || bytes[ip] >> 4 != 4 || bytes[ip + 9] != 6 || (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ip + 6)) & 0x3fff) != 0) continue;
                    var ipLength = (bytes[ip] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ip + 2)); var tcp = ip + ipLength;
                    if (ipLength < 20 || total < ipLength + 20 || ip + total > length) continue;
                    var tcpLength = (bytes[tcp + 12] >> 4) * 4; if (tcpLength < 20 || ipLength + tcpLength > total) continue;
                    var source = new IPAddress(bytes.AsSpan(ip + 12, 4)).ToString(); var destination = new IPAddress(bytes.AsSpan(ip + 16, 4)).ToString();
                    var flow = new TcpFlow(destination, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tcp + 2)), source, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tcp)));
                    var flags = bytes[tcp + 13];
                    if (!flows.Contains(flow)) { flows = CurrentFlows(immediate: true, opening: (flags & 2) != 0); if (!flows.Contains(flow)) continue; }
                    if (!owners.Claim(flow, name, DateTimeOffset.UtcNow)) continue;
                    var decoded = streams.Feed(flow, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(tcp + 4)), flags, bytes.AsSpan(tcp + tcpLength, total - ipLength - tcpLength));
                    if (decoded > 0) Interlocked.Add(ref gameBytes, decoded);
                }
                finally { Array.Clear(bytes); }
            }
        }
        catch (Exception failure) { reason = failure.Message; }
        finally { worker.Capturing = false; owners.Release(name); pcap_close(handle); if (!worker.Unsupported) Stopped(name, worker, token, reason); }
    }
    // A worker that ended without being asked to is opened again after 5 s, then 10, 20,
    // 40 and at most 60 seconds while it keeps failing; a minute of capturing resets that.
    // Only the first failure of a series is logged.
    private static void Stopped(string name, Worker worker, CancellationToken token, string reason)
    {
        if (token.IsCancellationRequested) return;
        var now = DateTimeOffset.UtcNow;
        if (now - worker.OpenedAt > TimeSpan.FromMinutes(1)) worker.Failures = 0;
        worker.Failures++; worker.RetryAt = now.AddSeconds(Math.Min(60, 5 << Math.Min(worker.Failures - 1, 4)));
        if (worker.Failures == 1) { worker.Announced = true; Log.Warn($"Capture interface {name} stopped{(reason.Length > 0 ? ": " + reason : "")}; it is opened again shortly."); }
    }
    public async ValueTask DisposeAsync()
    {
        if (subscribed) SystemEvents.PowerModeChanged -= PowerChanged;
        cancellation.Cancel(); await supervisor; cancellation.Dispose();
    }
}
