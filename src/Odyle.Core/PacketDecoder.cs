using System.Buffers.Binary;
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
/// <param name="ParserVersion">Adapter name the backend must approve before storing the value.</param>
/// <param name="Source">Whether the value came from a full snapshot or an incremental update.</param>
public sealed record PacketCandidate(int Total, string ParserVersion, CandidateSource Source = CandidateSource.Snapshot)
{
    /// <summary>Base component; null when only the total is known (a reading handed over by a restart).</summary>
    public int? Base { get; init; }
    /// <summary>Bonus component; null when only the total is known.</summary>
    public int? Bonus { get; init; }
}
public enum CandidateSource { Snapshot, Update }

/// <summary>
/// One record of the stat snapshot (0B61) and stat update (0C61): a tag byte, the stat's
/// resource id as a little-endian u32, the base value as a varint when the tag has bit 4 and
/// the bonus value when it has bit 8. A value the tag leaves out is zero: 0C carries base and
/// bonus, 08 the bonus only, 04 the base only and 00 neither.
/// </summary>
public readonly record struct StatRecord(byte Tag, uint Resource, long Base, long Bonus)
{
    public long Total => Base + Bonus;
    public static bool IsTag(byte tag) => (tag & ~0x0c) == 0;
    /// <summary>Reads the record at offset and moves past it; false, offset unchanged, for an unknown tag or a short or oversized value. Never throws.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, ref int offset, out StatRecord record)
    {
        record = default; var p = offset;
        if (p < 0 || data.Length - p < 5 || !IsTag(data[p])) return false;
        var tag = data[p]; var resource = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]); p += 5;
        ulong value = 0, bonus = 0;
        if ((tag & 4) != 0 && !ExperimentalOdyleDecoder.TryVarint(data, ref p, out value)) return false;
        if ((tag & 8) != 0 && !ExperimentalOdyleDecoder.TryVarint(data, ref p, out bonus)) return false;
        record = new(tag, resource, (long)value, (long)bonus); offset = p; return true;
    }
}

// Diagnostic fields only; the reading itself is the candidate. Resource is the stat's id:
// 60000001 is Odyle, anything else another counter that is never read.
public sealed record OdyleUpdateObservation(uint Resource, byte Tag, long Base, long Bonus, int TrailerBytes, byte Direction);
// The Odyle records of one stat snapshot: Records is the record count when the whole layout
// was walked, null when only the Odyle record's id was searched for.
public sealed record OdyleSnapshotField(int Offset, byte Tag, long Base, long Bonus);
public sealed record OdyleSnapshotObservation(int PayloadBytes, int? Records, OdyleSnapshotField[] Fields, bool Read);
public sealed record OdyleFrameLayout(ushort Opcode, byte Header0, byte Header1, byte Header2, int PayloadBytes);

/// <summary>
/// Decodes the Global AION2 stream: length-prefixed frames, optional transport
/// flag, LZ4 blocks, own identity (3336), stat snapshot (0B61) and stat update (0C61).
/// </summary>
public sealed class ExperimentalOdyleDecoder
{
    /// <summary>Adapter name sent with every reading. The backend registry lists the names it stores.</summary>
    public const string Version = "global-reference-v2";
    /// <summary>Stat resource id of Odyle, bytes 01 87 93 03 on the wire. 80000001 is another, small counter.</summary>
    public const uint OdyleResource = 60000001;
    private const int FrameLimit = 1048576, BundleLimit = 8 * 1048576, ValueLimit = 1000000;
    // Undecoded stream bytes live in buffer[start..end). Consuming a frame only moves
    // start; the data is compacted or grown when the tail runs out of room, instead
    // of shifting the whole remainder on every frame.
    private byte[] buffer = new byte[16384];
    private int start, end;
    private ulong? ownCharacter;
    private readonly bool startsAligned;
    private bool synchronized;
    /// <param name="startsAligned">False for a connection joined midstream; framing then waits for a heartbeat.</param>
    public ExperimentalOdyleDecoder(bool startsAligned = true) { this.startsAligned = startsAligned; synchronized = startsAligned; }
    /// <summary>True once a frame boundary is known. A midstream start or a resync waits for a protocol heartbeat.</summary>
    public bool Synchronized => synchronized;
    public event Action<PacketCandidate>? Candidate;
    /// <summary>The game's id of the character switched to: a valid own identity with another id than before on this connection.</summary>
    public event Action<ulong>? CharacterChanged;
    public event Action<GameIdentity>? IdentityObserved;
    /// <summary>A stat snapshot whose layout was read whole has no Odyle record: the character has no Odyle yet.</summary>
    public event Action? SnapshotWithoutOdyle;
    public event Action<ushort, int>? FrameObserved;
    public event Action<OdyleUpdateObservation>? UpdateObserved;
    public event Action<OdyleFrameLayout>? LayoutObserved;
    public event Action<OdyleSnapshotObservation>? SnapshotObserved;
    public void Reset() { start = end = 0; ownCharacter = null; synchronized = startsAligned; }
    /// <summary>Drops the undecoded bytes and waits for the next heartbeat; the own character stays known.</summary>
    public void Resync() { start = end = 0; synchronized = false; }
    private void Push(ReadOnlySpan<byte> bytes)
    {
        if (end - start + bytes.Length > FrameLimit) throw new InvalidDataException("Frame buffer limit exceeded.");
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
        if (!synchronized && !Synchronize()) return;
        while (end > start)
        {
            var data = buffer.AsSpan(start, end - start);
            var header = Header(data, out var position, out var length);
            if (header == FrameHeader.Incomplete) return;
            if (header == FrameHeader.Invalid) throw new InvalidDataException("Unsupported game framing.");
            if (length == 0) { Drop(1); continue; }
            if (data.Length < length) return;
            var frame = data.Slice(position, length - position).ToArray();
            Drop(length); DecodeFrame(frame, 0);
        }
    }
    private enum FrameHeader { Valid, Incomplete, Invalid }
    // A frame starts with a varint holding the size of the rest of the frame plus four; a
    // zero byte is padding. Valid frames hold at least a two-byte opcode and at most 1 MB.
    private static FrameHeader Header(ReadOnlySpan<byte> data, out int position, out int length)
    {
        position = 0; length = 0;
        if (!TryVarint(data, ref position, out var encoded)) return data.Length < 5 ? FrameHeader.Incomplete : FrameHeader.Invalid;
        if (encoded == 0) return FrameHeader.Valid;
        var total = (long)encoded + position - 4;
        if (total < position + 2 || total > FrameLimit) return FrameHeader.Invalid;
        length = (int)total; return FrameHeader.Valid;
    }
    // A midstream capture or a resync has no frame boundary. A complete protocol heartbeat
    // (06 00 36, or the 11-byte 0E 00 36 form) marks one, but only when the frame after it
    // starts with a valid length too: three matching bytes inside a payload are not enough.
    private bool Synchronize()
    {
        var data = buffer.AsSpan(start, end - start);
        for (var i = 0; i + 3 <= data.Length; i++)
        {
            if (data[i + 1] != 0 || data[i + 2] != 0x36 || data[i] is not (6 or 0x0e)) continue;
            var next = i + (data[i] == 6 ? 3 : 11); var padding = 0;
            while (next < data.Length && data[next] == 0 && padding < 8) { next++; padding++; }
            if (padding == 8) continue;
            var header = Header(data[Math.Min(next, data.Length)..], out _, out _);
            if (header == FrameHeader.Invalid) continue;
            Drop(i);
            if (header == FrameHeader.Incomplete) return false;
            synchronized = true; return true;
        }
        if (data.Length > 10) Drop(data.Length - 10);
        return false;
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
            // LZ4 restores at most about 255 bytes per compressed byte; a larger claim is not a bundle.
            if (size < 1 || size > (frame.Length - 6) * 256L + 64) throw new InvalidDataException("Invalid LZ4 size.");
            // A bundle above 8 MB is skipped whole; the frames after it are read as usual.
            if (size > BundleLimit) return;
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
            // The own-character identity may arrive after its stat snapshot and repeats every
            // few minutes. Only a complete identity counts: a short or unreadable 3336 changes
            // nothing, the same id again is a repeat, and only another id is a switch.
            if (GameIdentity.Read(frame[2..]) is not { } identity) return;
            var switched = ownCharacter is { } previous && previous != identity.Id;
            ownCharacter = identity.Id;
            if (switched) CharacterChanged?.Invoke(identity.Id);
            IdentityObserved?.Invoke(identity);
            return;
        }
        if (frame[1] != 0x61) return; // Sprint/dash fields never enter this adapter.
        var payload = frame[2..];
        if (frame[0] is 0x0b or 0x0c && payload.Length >= 3)
            LayoutObserved?.Invoke(new(BinaryPrimitives.ReadUInt16BigEndian(frame), payload[0], payload[1], payload[2], payload.Length));
        if (frame[0] == 0x0b) ReadSnapshot(payload);
        else if (frame[0] == 0x0c) ReadUpdate(payload);
    }
    // The own character's stats: a varint record count, then the records. Odyle is the record
    // of resource 60000001; another resource is never read, whatever its shape.
    private void ReadSnapshot(ReadOnlySpan<byte> payload)
    {
        var fields = new List<OdyleSnapshotField>(); var p = 0; int? records = null;
        if (TryVarint(payload, ref p, out var count) && count <= (ulong)payload.Length)
        {
            var walked = 0UL;
            while (walked < count)
            {
                var at = p;
                if (!StatRecord.TryRead(payload, ref p, out var record)) break;
                walked++;
                if (record.Resource == OdyleResource) fields.Add(new(at, record.Tag, record.Base, record.Bonus));
            }
            if (walked == count && p == payload.Length) records = (int)count; else fields.Clear();
        }
        // A layout that is not walked whole (an unknown tag) is searched for the Odyle id after a valid tag.
        if (records is null)
            for (var i = 0; i + 5 <= payload.Length; i++)
            {
                var at = i;
                if (BinaryPrimitives.ReadUInt32LittleEndian(payload[(i + 1)..]) == OdyleResource && StatRecord.TryRead(payload, ref at, out var record))
                    fields.Add(new(i, record.Tag, record.Base, record.Bonus));
            }
        var read = fields.Count > 0 && fields.All(f => f.Base == fields[0].Base && f.Bonus == fields[0].Bonus) && fields[0].Base + fields[0].Bonus <= ValueLimit;
        SnapshotObserved?.Invoke(new(payload.Length, records, fields.ToArray(), read));
        if (read) Candidate?.Invoke(new((int)(fields[0].Base + fields[0].Bonus), Version) { Base = (int)fields[0].Base, Bonus = (int)fields[0].Bonus });
        // Only a snapshot understood whole shows that the record is missing, not just unfound.
        else if (records is not null && fields.Count == 0) SnapshotWithoutOdyle?.Invoke();
    }
    // A stat update: direction byte, one record, trailer. An increase (direction 1) ends with a
    // flag byte and a 32-bit delta, a decrease or a regeneration tick (direction 0) with one byte;
    // other trailers are unknown layouts. Its record carries the new base and bonus, so it needs
    // no snapshot first: the regeneration tick repeats both, a spend drains base before bonus.
    private void ReadUpdate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6 || payload[0] > 1) return;
        var p = 1;
        if (!StatRecord.TryRead(payload, ref p, out var record)) return;
        var trailer = payload.Length - p;
        UpdateObserved?.Invoke(new(record.Resource, record.Tag, record.Base, record.Bonus, trailer, payload[0]));
        if (record.Resource != OdyleResource || trailer != (payload[0] == 1 ? 5 : 1) || record.Total > ValueLimit) return;
        Candidate?.Invoke(new((int)record.Total, Version, CandidateSource.Update) { Base = (int)record.Base, Bonus = (int)record.Bonus });
    }
    /// <summary>Reads a varint of at most five bytes. False, offset unchanged, when the data ends first or the varint is longer; never throws.</summary>
    public static bool TryVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0; var start = offset;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset < 0 || offset >= data.Length) { offset = start; return false; }
            var b = data[offset++]; value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return true;
        }
        offset = start; value = 0; return false;
    }
}
