global using static Helpers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using K4os.Compression.LZ4;

// What the check groups share: Check, and the frame builders of the decoder fixtures. A group that
// needs another shape (a different Identity, its own Check) declares its own, which then wins.
static class Helpers
{
    // A failed check names its file and line, its message and the condition that was false. The
    // overloads with two to four conditions say which of them failed (all are evaluated, in order).
    public static void Check(bool condition, string message, [CallerArgumentExpression(nameof(condition))] string condition1 = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition) throw new CheckFailed(file, line, message, condition1);
    }
    public static void Check(bool a, bool b, string message, [CallerArgumentExpression(nameof(a))] string partA = "", [CallerArgumentExpression(nameof(b))] string partB = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Parts(message, file, line, (a, partA), (b, partB));
    public static void Check(bool a, bool b, bool c, string message, [CallerArgumentExpression(nameof(a))] string partA = "", [CallerArgumentExpression(nameof(b))] string partB = "", [CallerArgumentExpression(nameof(c))] string partC = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Parts(message, file, line, (a, partA), (b, partB), (c, partC));
    public static void Check(bool a, bool b, bool c, bool d, string message, [CallerArgumentExpression(nameof(a))] string partA = "", [CallerArgumentExpression(nameof(b))] string partB = "", [CallerArgumentExpression(nameof(c))] string partC = "", [CallerArgumentExpression(nameof(d))] string partD = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Parts(message, file, line, (a, partA), (b, partB), (c, partC), (d, partD));
    static void Parts(string message, string file, int line, params (bool Ok, string Part)[] parts)
    {
        var failed = parts.Where(p => !p.Ok).Select(p => p.Part).ToArray();
        if (failed.Length > 0) throw new CheckFailed(file, line, message, string.Join(" ; ", failed));
    }

    // Decoder fixtures: a varint, a frame (its length varint counts itself as 4), an LZ4 bundle, and the
    // own character's 3336 (varint id, u32 mask, u8 mask, u8 name length, name, u16 server, u32 class).
    public static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 127); value >>= 7; bytes.Add((byte)(b | (value > 0 ? 128 : 0))); } while (value > 0);
        return bytes.ToArray();
    }
    public static byte[] Frame(byte[] content) => [.. Varint((ulong)content.Length + 4), .. content];
    public static byte[] Bundle(byte[] frames)
    {
        var encoded = new byte[LZ4Codec.MaximumOutputSize(frames.Length)]; var length = LZ4Codec.Encode(frames, encoded);
        var bundle = new byte[6 + length]; bundle[0] = bundle[1] = 255;
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(2), frames.Length); encoded.AsSpan(0, length).CopyTo(bundle.AsSpan(6));
        return bundle;
    }
    public static byte[] Identity(ulong id, string name, ushort server = 2304, uint job = 25)
    {
        var text = Encoding.UTF8.GetBytes(name); var tail = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(tail, server); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(2), job);
        return [0x33, 0x36, .. Varint(id), 0, 0, 0, 0, 1, (byte)text.Length, .. text, .. tail];
    }
}
sealed class CheckFailed(string file, int line, string message, string failed)
    : Exception($"{Path.GetFileName(file)}:{line}: {message}{(failed.Length > 0 ? $" (false: {failed})" : "")}");
