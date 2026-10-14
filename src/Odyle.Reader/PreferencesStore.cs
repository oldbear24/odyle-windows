using System;
using System.IO;
using System.Text.Json;
using Odyle.Core;

namespace Odyle.Reader;
// How the window is shown on this PC: pinned above other windows, with the daily and
// weekly checklist beside the card, in which language ("auto" follows Windows) and
// where it was last (WPF units). Everything else is configured on the dashboard.
public sealed record Preferences(bool StayOnTop = false, bool ShowTasks = false, string Language = "auto", double? Left = null, double? Top = null);
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
        try { AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(preferences)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("Preferences could not be saved", error); }
    }
}
