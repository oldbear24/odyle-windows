using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Odyle.Core;

// Every text the app passes to T() or Tn() as a literal, every literal text in its XAML
// (titles, text blocks, captions, headers, tooltips) and every server message it may show
// must have a Czech entry with the same placeholders, as the dashboard's tests/i18n.test.cjs
// checks for the web app.
static class LocalizationChecks
{
    // English messages of the backend (odyle-dashboard backend/odyle) and PocketBase that
    // reach the app's status lines through T(error.Message).
    static readonly string[] ServerMessages = [
        "Request could not be completed.", "Something went wrong.", "Something went wrong while processing your request.", "The request requires valid record authorization token.",
        "Too Many Requests.", "The requested resource wasn't found.", "You are not a member of this room.", "This character no longer exists.", "This character belongs to another account.",
        "Publisher session expired. Reconnect.", "Publisher session changed. Reconnect your client.", "This task no longer exists.", "This task belongs to another account.",
        "Send done (true or false) or remove: true.", "Only character name, server and region may be saved here.", "Pairing code is invalid or expired.", "Pairing new PCs is paused for now. Try again later.", "Pairing code is required (max 24 characters).",
        "Device key is required (max 100 characters).", "This PC is no longer paired with an account.", "Select a Global region.", "Outdated reading.",
        "Odyle must come from game packets. Manual readings are disabled.", "This packet adapter has not been validated for Global. No reading was saved.", "A verified packet observation is required.",
        "You can keep up to 30 characters. Delete one you no longer play under My rooms & characters.", "Character is required (max 40 characters).", "Server is required (max 40 characters)."];
    const string Literal = "\"((?:[^\"\\\\]|\\\\.)*)\"";
    public static void Run()
    {
        var root = RepositoryRoot(); var missing = new List<string>(); var seen = 0;
        void Need(string text) { seen++; if (!Czech.Texts.ContainsKey(text)) missing.Add(text); }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            // An interpolated or concatenated key could never be found in the dictionary.
            Check(!Regex.IsMatch(source, @"\bTn?\(\s*\$"), "no interpolated text is passed to T() in " + Path.GetFileName(file));
            foreach (Match m in Regex.Matches(source, @"\bT\(\s*" + Literal)) Need(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(source, @"\bTn\([^,;]+,\s*" + Literal + @"\s*,\s*" + Literal))
            {
                var key = Regex.Unescape(m.Groups[1].Value) + "|" + Regex.Unescape(m.Groups[2].Value); seen++;
                if (!Czech.Plurals.TryGetValue(key, out var forms) || forms.Length != 3) missing.Add(key);
            }
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories))
            foreach (var element in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName is not ("TextBox" or "PasswordBox")))
                foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "Text" or "Content" or "ToolTip" or "Header" or "Title"))
                    if (!attribute.Value.StartsWith('{') && attribute.Value.Any(char.IsLetter)) Need(attribute.Value);
        foreach (var message in ServerMessages) Need(message);
        Check(seen > 200, "the scan found the app's texts");
        Check(missing.Count == 0, "Czech entries missing for:\n  " + string.Join("\n  ", missing.Distinct()));
        // Placeholders survive translation, in every plural form too.
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{\d+(?::[^}]*)?\}").Select(m => m.Value).Order());
        foreach (var (key, value) in Czech.Texts) Check(Holes(key) == Holes(value), "placeholders of " + key);
        foreach (var (key, forms) in Czech.Plurals) foreach (var form in forms) Check(Holes(key.Split('|')[1]) == Holes(form), "placeholders of " + key);
        // The Windows display language decides unless settings chose one; plurals follow Czech rules.
        var czech = CultureInfo.GetCultureInfo("cs-CZ"); var german = CultureInfo.GetCultureInfo("de-DE");
        Check(Localizer.Resolve(null, czech) == "cs" && Localizer.Resolve("auto", german) == "en" && Localizer.Resolve("en", czech) == "en" && Localizer.Resolve("cs", german) == "cs", "language resolution");
        try
        {
            Localizer.Use("cs", german, german);
            Check(Localizer.Culture.Name == "cs-CZ" && Localizer.T("Connected to your account.") == "Připojeno k tvému účtu." && Localizer.T("Not a known text") == "Not a known text", "Czech lookup falls back to English");
            Check(new[] { 1, 2, 4, 5, 0, 21 }.Select(n => Localizer.Tn(n, "{0} cube", "{0} cubes")).SequenceEqual(["1 kostka", "2 kostky", "4 kostky", "5 kostek", "0 kostek", "21 kostek"]), "Czech plurals");
            Check(Localizer.T("Live · {0:N0} Odyle saved to your account.", 12345) == "Živě · 12 345 Odyle uloženo na tvůj účet.", "numbers follow the Czech format");
            Localizer.Use("auto", CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
            Check(Localizer.Language == "en" && Localizer.Culture.Name == "en-US" && Localizer.Tn(1, "{0} cube", "{0} cubes") == "1 cube" && Localizer.Tn(2, "{0} cube", "{0} cubes") == "2 cubes", "English keeps the regional format");
            Localizer.Use("en", czech, czech); Check(Localizer.Culture.Name == "en-GB", "English on a Czech PC formats like the dashboard's English");
            Check(Localizer.Wait(TimeSpan.FromSeconds(14.2)) == "15 s" && Localizer.Wait(TimeSpan.FromMinutes(5)) == "5 min" && Localizer.Wait(TimeSpan.Zero) == "1 s", "short waits");
        }
        finally { Localizer.Use("en"); }
        Console.WriteLine($"Czech translations for {seen} texts, placeholders, language choice and plurals passed.");
    }
    static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Odyle.Reader"))) return directory.FullName;
        throw new Exception("Localization check: the repository root was not found.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Localization check failed: " + message); }
}
