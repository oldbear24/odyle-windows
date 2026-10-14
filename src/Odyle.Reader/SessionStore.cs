using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Odyle.Core;

namespace Odyle.Reader;
// DeviceKey keeps the pairing across restarts and expired tokens; sessions saved by
// older versions have none until the app fetches one (PartyApi.EnsureDeviceKeyAsync).
public sealed record SavedSession(string Endpoint, string Token, string? DeviceKey = null);
public static class SessionStore
{
    // Checks point this at a temporary file so they never touch the user's pairing.
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "session.bin");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Blob output;
            var ok = protect ? CryptProtectData(ref input, "Odyle account", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    // Written whole or not at all, so a crash while saving never damages the pairing.
    public static void Save(SavedSession session)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(session);
        try { AtomicFile.Write(FilePath, Transform(json, true)); } finally { Array.Clear(json); }
    }
    // A file this Windows account cannot decrypt (copied from another PC or user, damaged)
    // or that names no server counts as not paired: `unreadable` tells the window to say
    // so, and the file is removed so the next pairing starts clean. A file that cannot be
    // opened right now (locked, no access) is kept for the next start.
    public static SavedSession? Load(out bool unreadable)
    {
        unreadable = false; byte[] stored;
        try { if (!File.Exists(FilePath)) return null; stored = File.ReadAllBytes(FilePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("The saved session could not be opened", error); unreadable = true; return null; }
        try
        {
            var json = Transform(stored, false);
            try { if (JsonSerializer.Deserialize<SavedSession>(json) is { Endpoint.Length: > 0 } session) return session; }
            finally { Array.Clear(json); }
        }
        catch (Exception error) when (error is Win32Exception or JsonException or NotSupportedException) { Log.Error("The saved session could not be read", error); }
        unreadable = true; Clear(); return null;
    }
    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("The saved session could not be removed", error); }
    }
}
