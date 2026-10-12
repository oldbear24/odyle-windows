using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Odyle.Reader;
public sealed record SavedSession(string Endpoint, string Token);
public static class SessionStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle");
    private static string FilePath => Path.Combine(Folder, "session.bin");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Blob output;
            var ok = protect ? CryptProtectData(ref input, "Odyle account", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    public static void Save(SavedSession session)
    {
        Directory.CreateDirectory(Folder);
        var json = JsonSerializer.SerializeToUtf8Bytes(session);
        try { File.WriteAllBytes(FilePath, Transform(json, true)); } finally { Array.Clear(json); }
    }
    public static SavedSession? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var json = Transform(File.ReadAllBytes(FilePath), false);
        try { return JsonSerializer.Deserialize<SavedSession>(json); } finally { Array.Clear(json); }
    }
    public static void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
