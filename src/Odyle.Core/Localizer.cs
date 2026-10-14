using System.Globalization;

namespace Odyle.Core;
// English is the source language, as on the dashboard (src/i18n.js): T() looks the English
// text up in the active dictionary and falls back to it, so a missing translation shows
// English, never a key. {0} placeholders are filled in the chosen culture. The server
// answers in English, so its messages go through the same lookup. Czech.cs holds the
// dictionary; the Core checks fail when a text the app uses has no Czech entry.
public static class Localizer
{
    public const string Automatic = "auto";
    public static string Language { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;
    // A saved "en" or "cs" wins; otherwise Czech for a Czech Windows display language, else English.
    public static string Resolve(string? choice, CultureInfo display) => choice is "en" or "cs" ? choice : display.TwoLetterISOLanguageName == "cs" ? "cs" : "en";
    // Numbers and dates keep the Windows regional format while it is of the same language,
    // else the language's own: cs-CZ, or en-GB like the dashboard.
    public static void Use(string? choice, CultureInfo? display = null, CultureInfo? regional = null)
    {
        Language = Resolve(choice, display ?? CultureInfo.CurrentUICulture);
        var format = regional ?? CultureInfo.CurrentCulture;
        Culture = format.TwoLetterISOLanguageName == Language ? format : CultureInfo.GetCultureInfo(Language == "cs" ? "cs-CZ" : "en-GB");
    }
    public static string T(string text) => Language == "cs" && Czech.Texts.TryGetValue(text, out var found) ? found : text;
    public static string T(string text, params object?[] args) => string.Format(Culture, T(text), args);
    // Plurals are keyed by both English forms joined with '|'. English has one and other,
    // Czech one (1), few (2–4) and other (0, 5 and more); {0} is the count.
    public static string Tn(long count, string one, string other, params object?[] args)
    {
        var form = Language == "cs" && Czech.Plurals.TryGetValue(one + "|" + other, out var forms) ? forms[CzechForm(count)] : count == 1 ? one : other;
        return string.Format(Culture, form, [count, .. args]);
    }
    public static int CzechForm(long count) => count == 1 ? 0 : count is >= 2 and <= 4 ? 1 : 2;
    // A short wait for status lines: "45 s" below a minute and a half, else whole minutes.
    public static string Wait(TimeSpan span)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(span.TotalSeconds));
        return seconds < 90 ? T("{0} s", seconds) : T("{0} min", (seconds + 59) / 60);
    }
}
