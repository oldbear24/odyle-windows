using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Odyle.Core;

// Bounded, receive-only TCP reassembly. Gaps never become guessed bytes.
public sealed class TcpReceiveStream
{
    private uint? next;
    private readonly Dictionary<uint, byte[]> pending = new();
    private byte[] history = Array.Empty<byte>();
    public DateTimeOffset? GapSince { get; private set; }
    public void Reset() { next = null; pending.Clear(); history = Array.Empty<byte>(); GapSince = null; }
    public IEnumerable<byte[]> Feed(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return Array.Empty<byte[]>();
        next ??= sequence;
        var distance = unchecked((int)(sequence - next.Value));
        CheckOverlap(sequence, payload, next.Value - (uint)history.Length, history);
        foreach (var piece in pending) CheckOverlap(sequence, payload, piece.Key, piece.Value);
        if (distance > 0)
        {
            if (distance > 524288 || pending.Count >= 64 || pending.Values.Sum(x => x.Length) + payload.Length > 524288) throw new InvalidDataException("TCP gap exceeds buffer limit.");
            if (pending.TryGetValue(sequence, out var old) && old.Length >= payload.Length) return Array.Empty<byte[]>();
            pending[sequence] = payload.ToArray(); GapSince ??= DateTimeOffset.UtcNow; return Array.Empty<byte[]>();
        }
        var chunks = new List<byte[]>();
        void Append(uint seq, byte[] data)
        {
            var skip = unchecked((int)(next!.Value - seq));
            if (skip >= data.Length) return;
            var bytes = data.AsSpan(Math.Max(0, skip)).ToArray(); chunks.Add(bytes); next += (uint)bytes.Length;
            var retained = history.Concat(bytes).ToArray(); history = retained.AsSpan(Math.Max(0, retained.Length - 65536)).ToArray();
        }
        Append(sequence, payload.ToArray());
        while (true)
        {
            var available = pending.Keys.Where(k => unchecked((int)(k - next!.Value)) <= 0).ToArray();
            if (available.Length == 0) break;
            foreach (var key in available) { var bytes = pending[key]; pending.Remove(key); Append(key, bytes); }
        }
        if (pending.Count == 0) GapSince = null;
        return chunks;
    }
    private static void CheckOverlap(uint sequence, ReadOnlySpan<byte> data, uint otherSequence, ReadOnlySpan<byte> other)
    {
        var offset = (long)unchecked((int)(sequence - otherSequence));
        var start = Math.Max(0, offset); var end = Math.Min(other.Length, offset + data.Length);
        if (end > start && !data.Slice((int)(start - offset), (int)(end - start)).SequenceEqual(other.Slice((int)start, (int)(end - start))))
            throw new InvalidDataException("Conflicting TCP overlap.");
    }
}

public sealed record PacketCandidate(int Total, ulong EntityId, string ParserVersion)
{
    // One Global startup read matched; live updates and identity binding
    // still require validation before this adapter can publish.
    public bool IsVerified => false;
    public int Primary { get; init; }
    public int Bonus { get; init; }
}

// Diagnostic fields only; an update without a snapshot is not a total.
public sealed record OdyleUpdateObservation(byte Mode, ulong First, ulong? Second, int TrailerBytes, byte Direction);
public sealed record OdyleFrameLayout(ushort Opcode, byte Header0, byte Header1, byte Header2, int PayloadBytes);

public sealed class ExperimentalOdyleDecoder
{
    public const string Version = "reference-odyle-v1-unverified";
    private readonly List<byte> buffered = new();
    private ulong? entity;
    private ulong? ownCharacter;
    private int? baseline;
    private readonly bool startsAligned;
    private bool synchronized;
    public ExperimentalOdyleDecoder(bool startsAligned = true) { this.startsAligned = startsAligned; synchronized = startsAligned; }
    public event Action<PacketCandidate>? Candidate;
    public event Action? CharacterChanged;
    public event Action<ushort, int>? FrameObserved;
    public event Action<OdyleUpdateObservation>? UpdateObserved;
    public event Action<OdyleFrameLayout>? LayoutObserved;
    public void Reset() { buffered.Clear(); entity = null; ownCharacter = null; baseline = null; synchronized = startsAligned; }
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (buffered.Count + bytes.Length > 1048576) throw new InvalidDataException("Frame buffer limit exceeded.");
        buffered.AddRange(bytes.ToArray());
        if (!synchronized)
        {
            // A midstream capture has no frame boundary. Wait for a complete
            // protocol heartbeat instead of interpreting arbitrary payload bytes.
            var start = -1;
            for (var i = 0; i + 3 <= buffered.Count; i++)
                if (buffered[i + 1] == 0 && buffered[i + 2] == 0x36 && (buffered[i] == 6 || (buffered[i] == 0x0e && i + 11 <= buffered.Count))) { start = i; break; }
            if (start < 0) { if (buffered.Count > 10) buffered.RemoveRange(0, buffered.Count - 10); return; }
            buffered.RemoveRange(0, start); synchronized = true;
        }
        while (buffered.Count > 0)
        {
            var data = buffered.ToArray(); var position = 0;
            if (!TryVarint(data, ref position, out var encoded)) return;
            if (encoded == 0) { buffered.RemoveAt(0); continue; }
            var length = (long)encoded + position - 4;
            if (length < position + 2 || length > 1048576) throw new InvalidDataException("Unsupported game framing.");
            if (data.Length < length) return;
            buffered.RemoveRange(0, (int)length); DecodeFrame(data.AsSpan(position, (int)length - position), 0);
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
            var offset = 2;
            // The own-character identity may arrive after its stat snapshot
            // and may repeat. Neither establishes a character switch alone.
            if (TryVarint(frame, ref offset, out var character) && character > 0 && frame.Length - offset >= 5)
            {
                var changed = ownCharacter is { } previous && previous != character;
                ownCharacter = character;
                if (!changed) return;
            }
            var hadIdentity = entity is not null; entity = null; baseline = null;
            if (hadIdentity) CharacterChanged?.Invoke();
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
                    // Reference servers omit the base when it is zero. This
                    // shape remains a local hypothesis until Global validation.
                    if (TryVarint(payload, ref p, out var singleId) && TryVarint(payload, ref p, out var value) && singleId > 0 && value <= 1000000)
                        candidates.Add((singleId, 0, (int)value));
                    continue;
                }
                if (TryVarint(payload, ref p, out var id) && TryVarint(payload, ref p, out var left) && TryVarint(payload, ref p, out var right) && id > 0 && left <= 1000000 && right <= 1000000 && left + right <= 1000000)
                    candidates.Add((id, (int)left, (int)(left + right)));
            }
            if (candidates.Count != 1) { entity = null; baseline = null; return; }
            var c = candidates[0]; entity = c.Id; baseline = c.Base; Candidate?.Invoke(new(c.Total, c.Id, Version) { Primary = c.Base, Bonus = c.Total - c.Base });
        }
        else if (frame[0] == 0x0c)
        {
            // 0C61 is a general stat update. Other field IDs do not
            // invalidate the Odyle snapshot or supply an energy reading.
            if (payload.Length >= 3 && payload[2] != 1) return;
            if (payload.Length < 6 || payload[0] > 1 || (payload[1] != 8 && payload[1] != 12)) { entity = null; baseline = null; return; }
            var p = 3;
            if (!TryVarint(payload, ref p, out var id) || !TryVarint(payload, ref p, out var first)) return;
            ulong? second = null;
            if (payload[1] == 12) { if (!TryVarint(payload, ref p, out var value)) return; second = value; }
            if (first > 1000000 || second > 1000000) { entity = null; baseline = null; return; }
            UpdateObserved?.Invoke(new(payload[1], first, second, payload.Length - p, payload[0]));
            if (entity != id || baseline is null) return;
            ulong left = second is null ? (ulong)baseline.Value : first, right = second ?? first;
            if (payload.Length - p != (payload[0] == 1 ? 5 : 1) || left > 1000000 || right > 1000000 || left + right > 1000000) { entity = null; baseline = null; return; }
            baseline = (int)left; Candidate?.Invoke(new((int)(left + right), id, Version) { Primary = (int)left, Bonus = (int)right });
        }
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
