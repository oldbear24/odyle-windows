using System.Xml.Linq;
using Odyle.Core;

// What's new (#34): CHANGELOG.md as Odyle.Core.Changelog reads it, the version rule and the
// release notes, in the same format as odyle-dashboard's src/changelog.js.
static class ChangelogChecks
{
    const string Sample = "# Changelog\r\n\r\nIntro.\r\n\r\n## 0.10.0 · 2026-10-16\r\n\r\n### New\r\n- **Bold** first\r\n  continued here\r\n- Second with `code`\r\n\r\n### Fixed\r\n- A fix\r\n\r\n## 0.9.0\r\n\r\n### Odd\r\n- x\r\n\r\n## 0.8.0 · 2026-10-15\r\n\r\n### New\r\n";
    public static void Run()
    {
        var entries = Changelog.Parse(Sample);
        Check(entries.Select(e => (e.Version, e.Date)).SequenceEqual([("0.10.0", "2026-10-16"), ("0.9.0", ""), ("0.8.0", "2026-10-15")]), "versions newest first, with their dates");
        Check(entries[0].Groups is [{ Title: "New", Items: ["**Bold** first continued here", "Second with `code`"] }, { Title: "Fixed", Items: ["A fix"] }] && entries[2].Groups.Length == 0, "groups and entries; empty groups left out");
        Check(Changelog.Problem(Sample, "0.10.0") == "" && Changelog.Problem(Sample, "0.11.0").StartsWith("CHANGELOG.md has no section for 0.11.0.") && Changelog.Problem(Sample, "0.8.0").Contains("lists nothing") && Changelog.Problem(Sample, "0.9.0").Contains("group \"Odd\""), "a version must describe what it changed");
        Check(Changelog.Notes(entries[0]) == "<!-- changes -->\n### New\n- **Bold** first continued here\n- Second with `code`\n\n### Fixed\n- A fix\n\n<!-- /changes -->", "release notes between the markers the dashboard reads back");
        Check(Changelog.Runs("**Compact mode** is the `same` window").SequenceEqual([("Compact mode", true), (" is the same window", false)]), "an entry splits into plain and bold runs");
        // The repository's own changelog describes the project's version, newest first.
        var root = Root();
        var version = XDocument.Load(Path.Combine(root, "src", "Odyle.Reader", "Odyle.Reader.csproj")).Descendants("Version").First().Value;
        var text = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Check(Changelog.Problem(text, version) == "", "CHANGELOG.md describes " + version);
        var versions = Changelog.Parse(text).Select(e => Version.Parse(e.Version)).ToArray();
        Check(versions.SequenceEqual(versions.OrderByDescending(v => v)), "CHANGELOG.md lists the newest version first");
        Console.WriteLine("Changelog sections, the version rule, release notes and the repository's changelog passed.");
    }
    static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")) && Directory.Exists(Path.Combine(directory.FullName, "src", "Odyle.Reader"))) return directory.FullName;
        throw new Exception("Changelog check: the repository root was not found.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Changelog check failed: " + message); }
}
