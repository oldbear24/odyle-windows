using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Odyle.Reader;
// Numeric stat-owner ids that game snapshots named as the Odyle counter, per account,
// so a reader started after login can trust live updates. Nothing here leaves this PC.
public static class OwnerStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "stat-owners.json");
    private static Dictionary<string, ulong[]> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, ulong[]>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }
    public static ulong[] Load(string accountId) => accountId.Length > 0 && Read().TryGetValue(accountId, out var ids) ? ids : Array.Empty<ulong>();
    public static void Remember(string accountId, ulong id)
    {
        if (accountId.Length == 0 || id == 0) return;
        var all = Read();
        var known = all.TryGetValue(accountId, out var ids) ? ids : Array.Empty<ulong>();
        if (known.Contains(id)) return;
        all[accountId] = known.Append(id).TakeLast(16).ToArray();
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(all)); } catch { }
    }
}
