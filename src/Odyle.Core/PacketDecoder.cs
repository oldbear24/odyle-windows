using System.Buffers.Binary;
using System.Collections.Concurrent;
using K4os.Compression.LZ4;

namespace Odyle.Core;

// Bounded, receive-only TCP reassembly. Gaps never become guessed bytes.
public sealed class TcpReceiveStream
{
    private const int HistoryBytes = 65536, PendingLimit = 524288;
    private uint? next;
    private readonly Dictionary<uint, byte[]> pending = new();
    private int pendingBytes;
    // The last 64 KB delivered, for overlap checks. It lives in the front of a
    // double-sized buffer: appending never allocates and compaction is one in-place
    // copy per 64 KB received instead of a fresh 64 KB array per segment.
    private readonly byte[] history = new byte[HistoryBytes * 2];
    private int historyLength;
    public DateTimeOffset? GapSince { get; private set; }
    public void Reset() { next = null; pending.Clear(); pendingBytes = 0; historyLength = 0; GapSince = null; }
    public IEnumerable<byte[]> Feed(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return Array.Empty<byte[]>();
        next ??= sequence;
        var distance = unchecked((int)(sequence - next.Value));
        CheckOverlap(sequence, payload, next.Value - (uint)historyLength, history.AsSpan(0, historyLength));
        foreach (var piece in pending) CheckOverlap(sequence, payload, piece.Key, piece.Value);
        if (distance > 0)
        {
            if (distance > PendingLimit || pending.Count >= 64 || pendingBytes + payload.Length > PendingLimit) throw new InvalidDataException("TCP gap exceeds buffer limit.");
            if (pending.TryGetValue(sequence, out var old)) { if (old.Length >= payload.Length) return Array.Empty<byte[]>(); pendingBytes -= old.Length; }
            pending[sequence] = payload.ToArray(); pendingBytes += payload.Length; GapSince ??= DateTimeOffset.UtcNow; return Array.Empty<byte[]>();
        }
        var chunks = new List<byte[]>();
        Append(sequence, payload, chunks);
        while (true)
        {
            var available = pending.Keys.Where(k => unchecked((int)(k - next!.Value)) <= 0).ToArray();
            if (available.Length == 0) break;
            foreach (var key in available) { var bytes = pending[key]; pending.Remove(key); pendingBytes -= bytes.Length; Append(key, bytes, chunks); }
        }
        if (pending.Count == 0) GapSince = null;
        return chunks;
    }
    private void Append(uint seq, ReadOnlySpan<byte> data, List<byte[]> chunks)
    {
        var skip = unchecked((int)(next!.Value - seq));
        if (skip >= data.Length) return;
        var bytes = data[Math.Max(0, skip)..].ToArray(); chunks.Add(bytes); next += (uint)bytes.Length; Retain(bytes);
    }
    private void Retain(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= HistoryBytes) { bytes[^HistoryBytes..].CopyTo(history); historyLength = HistoryBytes; return; }
        if (historyLength + bytes.Length > history.Length)
        {
            var keep = HistoryBytes - bytes.Length;
            history.AsSpan(historyLength - keep, keep).CopyTo(history); historyLength = keep;
        }
        bytes.CopyTo(history.AsSpan(historyLength)); historyLength += bytes.Length;
    }
    private static void CheckOverlap(uint sequence, ReadOnlySpan<byte> data, uint otherSequence, ReadOnlySpan<byte> other)
    {
        var offset = (long)unchecked((int)(sequence - otherSequence));
        var start = Math.Max(0, offset); var end = Math.Min(other.Length, offset + data.Length);
        if (end > start && !data.Slice((int)(start - offset), (int)(end - start)).SequenceEqual(other.Slice((int)start, (int)(end - start))))
            throw new InvalidDataException("Conflicting TCP overlap.");
    }
}

/// <summary>One Odyle observation decoded from the game stream.</summary>
/// <param name="Total">Base plus bonus energy as the game displays it.</param>
/// <param name="EntityId">Stat owner id from the snapshot or update frame.</param>
/// <param name="ParserVersion">Adapter name the backend must approve before storing the value.</param>
/// <param name="Source">Whether the value came from a full snapshot or an incremental update.</param>
public sealed record PacketCandidate(int Total, ulong EntityId, string ParserVersion, CandidateSource Source = CandidateSource.Snapshot)
{
    /// <summary>Base component; null when the frame carried only the total.</summary>
    public int? Primary { get; init; }
    /// <summary>Bonus component; null when the frame carried only the total.</summary>
    public int? Bonus { get; init; }
    /// <summary>False while the owner is the first stat id seen on a connection that had no snapshot.</summary>
    public bool OwnerConfirmed { get; init; } = true;
}
public enum CandidateSource { Snapshot, Update }

/// <summary>Stat owner ids a snapshot named as the Odyle counter. Shared by every stream of a capture.</summary>
public sealed class KnownOwners
{
    private readonly ConcurrentDictionary<ulong, byte> ids = new();
    public bool Any => !ids.IsEmpty;
    public bool Contains(ulong id) => ids.ContainsKey(id);
    public void Add(ulong id) { if (id > 0) ids[id] = 0; }
    public ulong[] ToArray() => ids.Keys.ToArray();
}

// Diagnostic fields only; the reading itself is the candidate.
public sealed record OdyleUpdateObservation(byte Mode, ulong First, ulong? Second, int TrailerBytes, byte Direction);
public sealed record OdyleFrameLayout(ushort Opcode, byte Header0, byte Header1, byte Header2, int PayloadBytes);

/// <summary>
/// Decodes the Global AION2 stream: length-prefixed frames, optional transport
/// flag, LZ4 blocks, own identity (3336), stat snapshot (0B61) and stat update (0C61).
/// </summary>
public sealed class ExperimentalOdyleDecoder
{
    /// <summary>Adapter name sent with every reading. The backend registry lists the names it stores.</summary>
    public const string Version = "global-reference-v1";
    // Undecoded stream bytes live in buffer[start..end). Consuming a frame only moves
    // start; the data is compacted or grown when the tail runs out of room, instead
    // of shifting the whole remainder on every frame.
    private byte[] buffer = new byte[16384];
    private int start, end;
    private readonly KnownOwners owners;
    private ulong? entity;
    private bool ownerConfirmed;
    private ulong? ownCharacter;
    private readonly bool startsAligned;
    private bool synchronized;
    /// <param name="startsAligned">False for a connection joined midstream; framing then waits for a heartbeat.</param>
    /// <param name="owners">Owner ids confirmed by snapshots of other streams or earlier runs.</param>
    public ExperimentalOdyleDecoder(bool startsAligned = true, KnownOwners? owners = null) { this.startsAligned = startsAligned; synchronized = startsAligned; this.owners = owners ?? new(); }
    /// <summary>True once a frame boundary is known. A midstream start waits for a protocol heartbeat.</summary>
    public bool Synchronized => synchronized;
    public event Action<PacketCandidate>? Candidate;
    public event Action? CharacterChanged;
    public event Action<GameIdentity>? IdentityObserved;
    public event Action<ushort, int>? FrameObserved;
    public event Action<OdyleUpdateObservation>? UpdateObserved;
    public event Action<OdyleFrameLayout>? LayoutObserved;
    public void Reset() { start = end = 0; ForgetOwner(); ownCharacter = null; synchronized = startsAligned; }
    private void ForgetOwner() { entity = null; ownerConfirmed = false; }
    private void Push(ReadOnlySpan<byte> bytes)
    {
        if (end - start + bytes.Length > 1048576) throw new InvalidDataException("Frame buffer limit exceeded.");
        if (end + bytes.Length > buffer.Length)
        {
            if (start > 0) { buffer.AsSpan(start, end - start).CopyTo(buffer); end -= start; start = 0; }
            if (end + bytes.Length > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, end + bytes.Length));
        }
        bytes.CopyTo(buffer.AsSpan(end)); end += bytes.Length;
    }
    private void Drop(int count) { start += count; if (start >= end) start = end = 0; }
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        Push(bytes);
        if (!synchronized)
        {
            // A midstream capture has no frame boundary. Wait for a complete
            // protocol heartbeat instead of interpreting arbitrary payload bytes.
            var data = buffer.AsSpan(start, end - start); var at = -1;
            for (var i = 0; i + 3 <= data.Length; i++)
                if (data[i + 1] == 0 && data[i + 2] == 0x36 && (data[i] == 6 || (data[i] == 0x0e && i + 11 <= data.Length))) { at = i; break; }
            if (at < 0) { if (data.Length > 10) Drop(data.Length - 10); return; }
            Drop(at); synchronized = true;
        }
        while (end > start)
        {
            var data = buffer.AsSpan(start, end - start); var position = 0;
            if (!TryVarint(data, ref position, out var encoded)) return;
            if (encoded == 0) { Drop(1); continue; }
            var length = (long)encoded + position - 4;
            if (length < position + 2 || length > 1048576) throw new InvalidDataException("Unsupported game framing.");
            if (data.Length < length) return;
            var frame = data.Slice(position, (int)length - position).ToArray();
            Drop((int)length); DecodeFrame(frame, 0);
        }
    }
    private void DecodeFrame(ReadOnlySpan<byte> frame, int depth)
    {
        // Global frames can carry one transport flag before the opcode.
        if (!frame.IsEmpty && frame[0] is >= 0xf0 and <= 0xfe) frame = frame[1..];
        if (frame.Length < 2) throw new InvalidDataException("Incomplete opcode.");
        if (frame[0] == 0xff && frame[1] == 0xff)
        {
            if (depth >= 3 || frame.Length < 6) throw new InvalidDataException("Invalid compressed frame.");
            var size = BinaryPrimitives.ReadInt32LittleEndian(frame[2..]);
            if (size < 1 || size > 1048576) throw new InvalidDataException("Invalid LZ4 size.");
            var restored = new byte[size];
            if (LZ4Codec.Decode(frame[6..], restored) != size) throw new InvalidDataException("Incomplete LZ4 block.");
            var offset = 0;
            while (offset < restored.Length)
            {
                var start = offset;
                if (!TryVarint(restored, ref offset, out var encoded)) throw new InvalidDataException("Incomplete inner frame.");
                if (encoded == 0) continue;
                var length = (long)encoded + offset - start - 4;
                if (length < offset - start + 2 || length > restored.Length - start) throw new InvalidDataException("Invalid inner frame.");
                DecodeFrame(restored.AsSpan(offset, (int)length - offset + start), depth + 1); offset = start + (int)length;
            }
            return;
        }
        FrameObserved?.Invoke(BinaryPrimitives.ReadUInt16BigEndian(frame), frame.Length - 2);
        if (frame[0] == 0x33 && frame[1] == 0x36)
        {
            var identity = GameIdentity.Read(frame[2..]);
            var offset = 2;
            // The own-character identity may arrive after its stat snapshot
            // and may repeat. Neither establishes a character switch alone.
            if (TryVarint(frame, ref offset, out var character) && character > 0 && frame.Length - offset >= 5)
            {
                var changed = ownCharacter is { } previous && previous != character;
                ownCharacter = character;
                if (!changed) { if (identity is not null) IdentityObserved?.Invoke(identity); return; }
            }
            var hadOwner = entity is not null; ForgetOwner();
            if (hadOwner) CharacterChanged?.Invoke();
            if (identity is not null) IdentityObserved?.Invoke(identity);
            return;
        }
        if (frame[1] != 0x61) return; // Sprint/dash fields never enter this adapter.
        var payload = frame[2..];
        if (frame[0] is 0x0b or 0x0c && payload.Length >= 3)
            LayoutObserved?.Invoke(new(BinaryPrimitives.ReadUInt16BigEndian(frame), payload[0], payload[1], payload[2], payload.Length));
        if (frame[0] == 0x0b)
        {
            var candidates = new List<(ulong Id, int Base, int Total)>();
            for (var i = 0; i + 2 < payload.Length; i++)
            {
                var twoValues = payload[i] == 0x0c && payload[i + 1] == 1;
                var oneValue = payload[i] == 0 && payload[i + 1] == 8 && payload[i + 2] == 1;
                if (!twoValues && !oneValue) continue;
                var p = i + (twoValues ? 2 : 3);
                if (oneValue)
                {
                    // Servers omit the base component when it is zero.
                    if (TryVarint(payload, ref p, out var singleId) && TryVarint(payload, ref p, out var value) && singleId > 0 && value <= 1000000)
                        candidates.Add((singleId, 0, (int)value));
                    continue;
                }
                if (TryVarint(payload, ref p, out var id) && TryVarint(payload, ref p, out var left) && TryVarint(payload, ref p, out var right) && id > 0 && left <= 1000000 && right <= 1000000 && left + right <= 1000000)
                    candidates.Add((id, (int)left, (int)(left + right)));
            }
            if (candidates.Count != 1) { ForgetOwner(); return; }
            var c = candidates[0]; entity = c.Id; ownerConfirmed = true; owners.Add(c.Id);
            Candidate?.Invoke(new(c.Total, c.Id, Version) { Primary = c.Base, Bonus = c.Total - c.Base });
        }
        else if (frame[0] == 0x0c)
        {
            // 0C61 is a general stat update. Other field IDs do not
            // disturb the Odyle owner or supply an energy reading.
            if (payload.Length >= 3 && payload[2] != 1) return;
            if (payload.Length < 6 || payload[0] > 1 || (payload[1] != 8 && payload[1] != 12)) { ForgetOwner(); return; }
            var p = 3;
            if (!TryVarint(payload, ref p, out var id) || !TryVarint(payload, ref p, out var first)) return;
            ulong? second = null;
            if (payload[1] == 12) { if (!TryVarint(payload, ref p, out var value)) return; second = value; }
            if (first > 1000000 || second > 1000000) { ForgetOwner(); return; }
            UpdateObserved?.Invoke(new(payload[1], first, second, payload.Length - p, payload[0]));
            // The single-value form carries the new total and the two-value form repeats
            // base and bonus, so neither needs a snapshot first. An increase ends with a
            // flag byte and a 32-bit delta, a decrease with one byte; other layouts are unknown.
            var total = first + (second ?? 0);
            if (payload.Length - p != (payload[0] == 1 ? 5 : 1) || total > 1000000) { if (entity == id) ForgetOwner(); return; }
            if (!ClaimOwner(id)) return;
            Candidate?.Invoke(new((int)total, id, Version, CandidateSource.Update) { Primary = second is null ? null : (int)first, Bonus = second is { } bonus ? (int)bonus : null, OwnerConfirmed = ownerConfirmed });
        }
    }
    // The update opcode is shared with other counters that differ only in their owner id.
    // A snapshot-confirmed id, from this stream, another connection or an earlier run, is
    // trusted and replaces an adopted one. Without any confirmed id the first id seen on
    // this connection is adopted until a snapshot names the owner.
    private bool ClaimOwner(ulong id)
    {
        if (id == 0) return false;
        if (owners.Contains(id)) { entity = id; ownerConfirmed = true; return true; }
        if (entity == id) return true;
        if (entity is not null || owners.Any) return false;
        entity = id; ownerConfirmed = false; return true;
    }
    public static bool TryVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0; var start = offset;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= data.Length) { offset = start; return false; }
            var b = data[offset++]; value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return true;
        }
        throw new InvalidDataException("Oversized varint.");
    }
}
