using System;
using System.IO;
using System.Text.Json;
using Odyle.Core;

namespace Odyle.Reader;
// How the window is shown on this PC: pinned above other windows, with the daily and
// weekly checklist beside the card, in which language ("auto" follows Windows) and
// where it was last (WPF units). RemindRuns was the planned-run reminders' switch before the
// account's notification settings (moved to the account once, RemindRunsMoved; kept for
// backends without them); RemindedRuns and NotifiedUpdate keep notifications from repeating after a restart;
// AutostartOffered, once the one-time offer to start with Windows was answered. Compact mode:
// whether it was on and its shortcut ("" off); the window keeps its one position.
// Everything else is configured on the dashboard.
public sealed record Preferences(bool StayOnTop = false, bool ShowTasks = false, string Language = "auto", double? Left = null, double? Top = null,
    bool RemindRuns = true, string[]? RemindedRuns = null, string NotifiedUpdate = "", bool AutostartOffered = false, bool RemindRunsMoved = false,
    bool CompactMode = false, string CompactHotkey = CompactStrip.DefaultHotkey);
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
