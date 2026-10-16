using System;
using System.IO;
using System.Text.Json;
using Odyle.Core;

namespace Odyle.Reader;
// The live character a restarting Odyle (update, language) hands to the next start. The
// game names a character at login, at a channel change and every few minutes, so without
// it the new start would wait for that. Total is the last reading, already saved on the
// account; the next start sends it again under its own session, with the adapter name that
// read it. Nothing secret is kept. A hand-over written by an older build carries stat owner
// fields too, which are ignored.
public sealed record Handover(string Endpoint, string AccountId, string CharacterId, string CharacterKey, string CharacterName,
    int Total, string ParserVersion, CandidateSource Source, DateTimeOffset SavedAt);
public static class HandoverStore
{
    // A restart takes seconds; an older hand-over may no longer describe what the game plays.
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    // Checks point this at a temporary file so they never touch the user's.
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "handover.json");
    public static void Save(Handover handover)
    {
        try { AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(handover)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("The live character could not be handed to the restart", error); }
    }
    // Read once: the file is removed whatever it holds, and only a fresh hand-over counts.
    public static Handover? Take(DateTimeOffset now)
    {
        Handover? handover = null;
        try { if (File.Exists(FilePath)) handover = JsonSerializer.Deserialize<Handover>(File.ReadAllText(FilePath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { Log.Error("The hand-over from the restart could not be read", error); }
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("The hand-over from the restart could not be removed", error); }
        return handover is not null && now - handover.SavedAt is var age && age >= TimeSpan.Zero && age <= Lifetime ? handover : null;
    }
}
