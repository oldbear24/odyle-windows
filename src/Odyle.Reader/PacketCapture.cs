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
using Odyle.Core;

namespace Odyle.Reader;

public sealed class PacketCapture : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task[] workers = Array.Empty<Task>();
    public event Action<string>? Status;
    public event Action<PacketCandidate>? Candidate;
    public event Action? CharacterChanged;
    private sealed record Flow(string Local, ushort LocalPort, string Remote, ushort RemotePort)
    { public string Filter => $"(src host {Remote} and src port {RemotePort} and dst host {Local} and dst port {LocalPort})"; }
    [StructLayout(LayoutKind.Sequential)] private struct Device { public IntPtr Next, Name, Description, Addresses; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Filter { public uint Length; public IntPtr Instructions; }
    static PacketCapture()
    {
        NativeLibrary.SetDllImportResolver(typeof(PacketCapture).Assembly, (name, _, _) =>
        {
            if (name != "wpcap") return IntPtr.Zero;
            var path = Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll");
            if (!File.Exists(path)) throw new DllNotFoundException("Install Npcap from npcap.com before starting the packet reader.");
            var handle = LoadLibraryEx(path, IntPtr.Zero, 0x1100);
            if (handle == IntPtr.Zero) throw new DllNotFoundException("Npcap could not load. Check its installation and CPU architecture.");
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

    public void Start()
    {
        var error = new StringBuilder(256);
        if (pcap_findalldevs(out var list, error) != 0) throw new InvalidOperationException("Npcap device discovery failed. Check capture permissions.");
        var names = new List<string>();
        try { for (var p = list; p != IntPtr.Zero; ) { var device = Marshal.PtrToStructure<Device>(p); var name = Marshal.PtrToStringAnsi(device.Name); if (name is not null) names.Add(name); p = device.Next; } }
        finally { if (list != IntPtr.Zero) pcap_freealldevs(list); }
        if (names.Count == 0) throw new InvalidOperationException("No Npcap capture interfaces available.");
        workers = names.Select(name => Task.Run(() => Receive(name))).ToArray();
    }
    private static Flow[] GameFlows()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcesses()) using (process) { try { if (process.ProcessName.Contains("AION2", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Contains("AION-2", StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id); } catch { } }
        if (ids.Count == 0) return Array.Empty<Flow>();
        var size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 4, 0);
        if (size < 4 || size > 4194304) return Array.Empty<Flow>();
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 4, 0) != 0) return Array.Empty<Flow>();
            var count = Marshal.ReadInt32(table); var result = new List<Flow>();
            if (count < 0 || count > (size - 4) / 24) return Array.Empty<Flow>();
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(table, 4 + i * 24);
                if (Marshal.ReadInt32(row) != 5 || !ids.Contains(Marshal.ReadInt32(row, 20))) continue;
                var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4))).ToString();
                var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 12))).ToString();
                ushort Port(int offset) => (ushort)((Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1));
                result.Add(new(local, Port(8), remote, Port(16)));
            }
            return result.Take(32).ToArray();
        }
        finally { Marshal.FreeHGlobal(table); }
    }
    private void Receive(string name)
    {
        var error = new StringBuilder(256); var handle = pcap_open_live(name, 65536, 0, 200, error);
        if (handle == IntPtr.Zero) { Status?.Invoke("Capture interface unavailable. Check Npcap permissions."); return; }
        var streams = new Dictionary<Flow, (TcpReceiveStream Tcp, ExperimentalOdyleDecoder Decoder, bool Failed)>();
        var lastFilter = ""; var flows = Array.Empty<Flow>(); var nextRefresh = DateTimeOffset.MinValue;
        try
        {
            if (pcap_setnonblock(handle, 1, error) != 0) throw new InvalidOperationException("Nonblocking capture is unavailable.");
            var linkType = pcap_datalink(handle);
            if (linkType is not (1 or 0 or 108 or 12 or 101)) return;
            while (!cancellation.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextRefresh)
                {
                    nextRefresh = DateTimeOffset.UtcNow.AddSeconds(2); flows = GameFlows();
                    var filterText = flows.Length == 0 ? "tcp and port 0" : "tcp and (" + string.Join(" or ", flows.Select(f => f.Filter)) + ")";
                    if (filterText != lastFilter)
                    {
                        if (pcap_compile(handle, out var filter, filterText, 1, uint.MaxValue) != 0) throw new InvalidOperationException("Unable to configure game-only capture filter.");
                        try { if (pcap_setfilter(handle, ref filter) != 0) throw new InvalidOperationException("Unable to apply capture filter."); }
                        finally { pcap_freecode(ref filter); }
                        lastFilter = filterText;
                        foreach (var removed in streams.Keys.Where(key => !flows.Contains(key)).ToArray()) streams.Remove(removed);
                        Status?.Invoke(flows.Length == 0 ? "Waiting for an AION2 IPv4 connection." : "Listening to AION2 packets · Global format remains unverified.");
                    }
                    foreach (var key in streams.Keys.ToArray()) if (streams[key].Tcp.GapSince is { } gap && DateTimeOffset.UtcNow - gap > TimeSpan.FromSeconds(3)) streams[key] = (new(), new(), true);
                }
                var received = pcap_next_ex(handle, out var header, out var pointer);
                if (received == 0) { Thread.Sleep(50); continue; }
                if (received < 0) throw new InvalidOperationException("Packet capture disconnected.");
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
                    var flow = new Flow(destination, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tcp + 2)), source, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(tcp)));
                    if (!flows.Contains(flow)) continue;
                    if (!streams.TryGetValue(flow, out var state))
                    {
                        var decoder = new ExperimentalOdyleDecoder(); decoder.Candidate += c => Candidate?.Invoke(c); decoder.CharacterChanged += () => CharacterChanged?.Invoke();
                        streams[flow] = state = (new(), decoder, false);
                    }
                    if ((bytes[tcp + 13] & 7) != 0) { streams.Remove(flow); continue; }
                    if (state.Failed) continue;
                    foreach (var chunk in state.Tcp.Feed(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(tcp + 4)), bytes.AsSpan(tcp + tcpLength, total - ipLength - tcpLength)))
                        state.Decoder.Feed(chunk);
                }
                catch (InvalidDataException)
                {
                    // Never restart framing in the middle of a damaged stream.
                    foreach (var key in streams.Keys.ToArray()) streams[key] = (new(), new(), true);
                    Status?.Invoke("Unsupported or incomplete packets · reconnect the game before retrying.");
                }
                finally { Array.Clear(bytes); }
            }
        }
        catch (Exception) { Status?.Invoke("Capture unavailable. Check Npcap and reconnect the game."); }
        finally { pcap_close(handle); }
    }
    public async ValueTask DisposeAsync() { cancellation.Cancel(); await Task.WhenAll(workers); cancellation.Dispose(); }
}
