using System;
using System.IO;
using System.Text.Json;

namespace Odyle.Reader;
// How the window is shown on this PC: pinned above other windows, with the daily and
// weekly checklist beside the card, and in which language ("auto" follows Windows).
// Everything else is configured on the dashboard.
public sealed record Preferences(bool StayOnTop = false, bool ShowTasks = false, string Language = "auto");
public static class PreferencesStore
{
    // Checks point this at a temporary file so they never touch the user's preferences.
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "preferences.json");
    public static Preferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(Preferences preferences)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(preferences)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
