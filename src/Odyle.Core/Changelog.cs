using System.Text.RegularExpressions;

namespace Odyle.Core;
// The changelog (CHANGELOG.md, #34): one section per version, "## 0.10.0 · 2026-10-16", with groups
// "### New", "### Changed" and "### Fixed" of "- " entries written for players. The app shows it
// as What's new; the release workflow puts a version's section into the release notes between
// markers, which the dashboard reads back (the same format as odyle-dashboard's src/changelog.js).
public sealed record ChangeGroup(string Title, string[] Items);
public sealed record ChangeEntry(string Version, string Date, ChangeGroup[] Groups);
public static class Changelog
{
    public static readonly string[] Titles = ["New", "Changed", "Fixed"];
    private static readonly Regex Heading = new(@"^## (\d+\.\d+\.\d+)(?:\s*·\s*(\d{4}-\d{2}-\d{2}))?\s*$", RegexOptions.CultureInvariant);

    // The versions, newest first as written.
    public static ChangeEntry[] Parse(string text)
    {
        var entries = new List<(string Version, string Date, List<(string Title, List<string> Items)> Groups)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (Heading.Match(line) is { Success: true } heading) { entries.Add((heading.Groups[1].Value, heading.Groups[2].Value, [])); continue; }
            if (entries.Count == 0) continue;
            var groups = entries[^1].Groups;
            if (line.StartsWith("### ", StringComparison.Ordinal)) groups.Add((line[4..].Trim(), []));
            else if (line.StartsWith("- ", StringComparison.Ordinal) && groups.Count > 0) groups[^1].Items.Add(line[2..].Trim());
            else if (line.Length > 0 && char.IsWhiteSpace(raw[0]) && groups.Count > 0 && groups[^1].Items.Count > 0) groups[^1].Items[^1] += " " + line.Trim();
        }
        return entries.Select(e => new ChangeEntry(e.Version, e.Date, e.Groups.Where(g => g.Items.Count > 0).Select(g => new ChangeGroup(g.Title, g.Items.ToArray())).ToArray())).ToArray();
    }

    // Why the changelog does not describe a version yet, or "" when it does.
    public static string Problem(string text, string version)
    {
        var entry = Parse(text).FirstOrDefault(e => e.Version == version);
        if (entry is null) return $"CHANGELOG.md has no section for {version}. Add \"## {version} · <date>\" with what changed, for players (AGENTS.md, Versioning).";
        if (entry.Groups.Length == 0) return $"CHANGELOG.md's section for {version} lists nothing. Add \"### New\", \"### Changed\" or \"### Fixed\" with \"- \" entries.";
        return entry.Groups.FirstOrDefault(g => !Titles.Contains(g.Title)) is { } odd ? $"CHANGELOG.md's section for {version} has a group \"{odd.Title}\"; use New, Changed or Fixed." : "";
    }

    // A version's entries as release notes, between the markers the dashboard reads them back from.
    public static string Notes(ChangeEntry entry) =>
        string.Join("\n", ["<!-- changes -->", .. entry.Groups.SelectMany(g => (string[])["### " + g.Title, .. g.Items.Select(i => "- " + i), ""]), "<!-- /changes -->"]);

    // An entry split into plain and **bold** runs, for the app's text blocks.
    public static IEnumerable<(string Text, bool Bold)> Runs(string item)
    {
        var parts = item.Split("**");
        for (var i = 0; i < parts.Length; i++) if (parts[i].Length > 0) yield return (parts[i].Replace("`", ""), i % 2 == 1);
    }
}
