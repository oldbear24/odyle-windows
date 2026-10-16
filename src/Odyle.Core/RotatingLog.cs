using System.Text;
using System.Text.RegularExpressions;

namespace Odyle.Core;
// A small troubleshooting log: the current file grows to about `limit` bytes, then becomes
// <name>.previous.log and a new one starts, so at most two files exist. Any thread may
// write; a full or locked disk never breaks the caller. Callers never pass credentials,
// and Redact masks anything shaped like an account token, device key or pairing code that
// could still slip in with a message (a server answer, an exception).
public sealed class RotatingLog(string path, long limit = 1024 * 1024)
{
    private readonly object sync = new();
    public string FilePath => path;
    public string PreviousPath => Path.ChangeExtension(path, null) + ".previous.log";
    public void Write(string level, string message)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} {Redact(message).TrimEnd().ReplaceLineEndings(Environment.NewLine + "    ")}{Environment.NewLine}";
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > limit) File.Move(path, PreviousPath, overwrite: true);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.Write(bytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    // The last lines of the log, the previous file's first (Copy diagnostics).
    public string[] Tail(int count)
    {
        var lines = new List<string>();
        lock (sync)
            foreach (var file in new[] { PreviousPath, path })
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine()) { lines.Add(line); if (lines.Count > count * 2) lines.RemoveRange(0, lines.Count - count); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        return lines.Skip(Math.Max(0, lines.Count - count)).ToArray();
    }
    private static readonly (Regex Pattern, string Mask)[] Secrets = [
        (new(@"eyJ[\w-]{6,}\.[\w-]{6,}\.[\w-]{6,}", RegexOptions.CultureInvariant), "[token]"),
        (new(@"(?i)\bbearer\s+\S+", RegexOptions.CultureInvariant), "Bearer [token]"),
        (new(@"odyle-pair=[^\s&#""']+", RegexOptions.CultureInvariant), "odyle-pair=[code]"),
        (new(@"\b[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}(?:[\s-]?[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}){3}\b", RegexOptions.CultureInvariant), "[code]"),
        // Device keys are long random letters and digits; long names in stack traces have no digits.
        (new(@"\b(?=[\w-]*\d)(?=[\w-]*[A-Za-z])[\w-]{32,}\b", RegexOptions.CultureInvariant), "[secret]")];
    public static string Redact(string text)
    {
        foreach (var (pattern, mask) in Secrets) text = pattern.Replace(text, mask);
        return text;
    }
}
// Writes a whole file or nothing: the bytes go to a temporary file beside it, which then
// replaces the old one, so a crash or full disk never leaves half a file behind.
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    public static void WriteText(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));
}
