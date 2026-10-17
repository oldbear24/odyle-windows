namespace Odyle.Core;
// Which capture interface decodes each game connection (#5). A connection can reach this PC on
// more than one Npcap interface (a virtual switch, a bridge), and each interface used to decode it
// on its own. The first interface to deliver a segment of a connection now owns it; the others skip
// it, so it is decoded once. An owner that delivered nothing for `quiet` gives it up to the next
// interface that does (an adapter that went away), and connections the game no longer has, or that
// a stopped interface owned, are forgotten. Capture workers call it from their own threads.
public sealed class FlowOwners(TimeSpan quiet)
{
    private readonly Dictionary<TcpFlow, (string Owner, DateTimeOffset At)> owners = new();
    private readonly object gate = new();
    // Whether `worker` decodes this segment of `flow`, received at `now`.
    public bool Claim(TcpFlow flow, string worker, DateTimeOffset now)
    {
        lock (gate)
        {
            if (owners.TryGetValue(flow, out var current) && current.Owner != worker && now - current.At <= quiet) return false;
            owners[flow] = (worker, now); return true;
        }
    }
    // The connections the game still has; the others are forgotten.
    public void Keep(IReadOnlyCollection<TcpFlow> live)
    {
        lock (gate) foreach (var gone in owners.Keys.Where(flow => !live.Contains(flow)).ToArray()) owners.Remove(gone);
    }
    // A stopped interface gives up its connections at once.
    public void Release(string worker)
    {
        lock (gate) foreach (var flow in owners.Where(o => o.Value.Owner == worker).Select(o => o.Key).ToArray()) owners.Remove(flow);
    }
    public string? OwnerOf(TcpFlow flow) { lock (gate) return owners.TryGetValue(flow, out var current) ? current.Owner : null; }
}
