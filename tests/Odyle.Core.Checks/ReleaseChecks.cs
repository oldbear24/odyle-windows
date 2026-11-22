using Odyle.Core;

// The release feed the updater reads (packages, checksums, held-back versions) and when it looks.
static class ReleaseChecks
{
    public static void Run()
    {
        // Release feed: GitHub's latest-release JSON names the packages the updater may fetch. Only
        // GitHub download links count, so the duplicate asset pointing elsewhere is dropped.
        const string releaseJson = """{"tag_name":"v0.5.0.2","name":"Odyle 0.5.0.2","html_url":"https://github.com/oldbear24/odyle-windows/releases/tag/v0.5.0.2","published_at":"2026-10-13T22:34:09Z","draft":false,"prerelease":false,"assets":[{"name":"odyle-0.5.0.2-win-x64-self-contained.zip","size":71595826,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64-self-contained.zip"},{"name":"odyle-0.5.0.2-win-x64-self-contained.zip.sha256","size":108,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64-self-contained.zip.sha256"},{"name":"odyle-0.5.0.2-win-x64.zip","size":217557,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64.zip"},{"name":"odyle-0.5.0.2-win-x64.zip.sha256","size":93,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.5.0.2/odyle-0.5.0.2-win-x64.zip.sha256"},{"name":"odyle-0.5.0.2-win-x64.zip","size":1,"browser_download_url":"http://mirror.example/odyle-0.5.0.2-win-x64.zip"}]}""";
        var release = ReleaseFeed.Parse(releaseJson) ?? throw new Exception("Release feed not parsed.");
        if (release.Version != new Version(0, 5, 0, 2) || release.Tag != "v0.5.0.2" || release.Url != "https://github.com/oldbear24/odyle-windows/releases/tag/v0.5.0.2" || release.PublishedAt != DateTimeOffset.Parse("2026-10-13T22:34:09Z") || release.Assets.Length != 4) throw new Exception("Release metadata contract failed.");
        var small = release.Package("win-x64", false) ?? throw new Exception("Small package not found.");
        if (small.Name != "odyle-0.5.0.2-win-x64.zip" || small.Size != 217557 || !small.Url.StartsWith("https://github.com/")) throw new Exception("Small package selection failed.");
        if (release.Package("win-x64", true)?.Name != "odyle-0.5.0.2-win-x64-self-contained.zip" || release.Package("win-arm64", false) is not null) throw new Exception("Package kind selection failed.");
        if (release.Checksum(small)?.Name != "odyle-0.5.0.2-win-x64.zip.sha256") throw new Exception("Checksum asset lookup failed.");
        if (!release.IsNewerThan(new Version(0, 5, 0, 1)) || !release.IsNewerThan(new Version(0, 5, 0)) || release.IsNewerThan(new Version(0, 5, 0, 2)) || release.IsNewerThan(new Version(0, 6, 0, 1))) throw new Exception("Release version comparison failed.");
        if (ReleaseFeed.Parse(releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true")) is not null || ReleaseFeed.Parse("""{"tag_name":"nightly","assets":[]}""") is not null || ReleaseFeed.Parse("[]") is not null) throw new Exception("Pre-releases and unversioned tags must never be offered.");
        // An answer that is not release data (a TLS-inspecting proxy's page, a cut-off body, a size that is no
        // whole number) is a FormatException with a readable message, which the update check counts as a failed check.
        foreach (var unreadable in new[] { "<html><body>Sign in to the proxy</body></html>", "", """{"tag_name":"v0.5.0.2","assets":[""", releaseJson.Replace("\"size\":217557", "\"size\":2.5") })
        {
            try { ReleaseFeed.Parse(unreadable); throw new Exception("An unreadable release answer was accepted: " + unreadable); }
            catch (FormatException error) when (error.Message == "GitHub sent an answer the app cannot read.") { }
        }
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        if (ReleaseFeed.ExpectedHash(hash.ToUpperInvariant() + "  odyle-0.5.0.2-win-x64.zip\n", "odyle-0.5.0.2-win-x64.zip") != hash) throw new Exception("Checksum file parsing failed.");
        if (ReleaseFeed.ExpectedHash(hash + "  odyle-0.5.0.2-win-x64-self-contained.zip", "odyle-0.5.0.2-win-x64.zip") is not null || ReleaseFeed.ExpectedHash("not-a-hash  odyle-0.5.0.2-win-x64.zip", "odyle-0.5.0.2-win-x64.zip") is not null) throw new Exception("A checksum for another file or a malformed hash was accepted.");
        if (!ReleaseFeed.TryParseTag("v0.6.0.15", out var tagged) || tagged != new Version(0, 6, 0, 15) || ReleaseFeed.TryParseTag("v1", out _) || ReleaseFeed.TryParseTag("latest", out _)) throw new Exception("Tag parsing failed.");
        if (ReleaseFeed.Describe("odyle-0.5.0.2-win-arm64-self-contained.zip") is not { Runtime: "win-arm64", SelfContained: true } || ReleaseFeed.Describe("odyle-0.5.0.2-win-x64.zip.sha256") is not null) throw new Exception("Package name parsing failed.");
        // A release with ARM64 packages and the installer: ARM64 PCs take the ARM64 package of their
        // kind, and the setup program is never mistaken for an update package.
        string Asset(string name) => $$"""{"name":"{{name}}","size":10,"browser_download_url":"https://github.com/oldbear24/odyle-windows/releases/download/v0.6.0.3/{{name}}"}""";
        string[] published = ["odyle-0.6.0.3-win-x64-setup.exe", "odyle-0.6.0.3-win-x64-setup.exe.sha256", "odyle-0.6.0.3-win-arm64-setup.exe", "odyle-0.6.0.3-win-arm64-setup.exe.sha256", "odyle-0.6.0.3-win-x64.zip", "odyle-0.6.0.3-win-x64.zip.sha256", "odyle-0.6.0.3-win-x64-self-contained.zip", "odyle-0.6.0.3-win-x64-self-contained.zip.sha256", "odyle-0.6.0.3-win-arm64.zip", "odyle-0.6.0.3-win-arm64.zip.sha256", "odyle-0.6.0.3-win-arm64-self-contained.zip", "odyle-0.6.0.3-win-arm64-self-contained.zip.sha256"];
        var full = ReleaseFeed.Parse($$"""{"tag_name":"v0.6.0.3","html_url":"https://github.com/oldbear24/odyle-windows/releases/tag/v0.6.0.3","assets":[{{string.Join(",", published.Select(Asset))}}]}""") ?? throw new Exception("Release with ARM64 packages not parsed.");
        if (full.Package("win-arm64", false)?.Name != "odyle-0.6.0.3-win-arm64.zip" || full.Package("win-arm64", true)?.Name != "odyle-0.6.0.3-win-arm64-self-contained.zip" || full.Checksum(full.Package("win-arm64", true)!)?.Name != "odyle-0.6.0.3-win-arm64-self-contained.zip.sha256") throw new Exception("ARM64 package selection failed.");
        if (full.Package("win-x64", true)?.Name != "odyle-0.6.0.3-win-x64-self-contained.zip" || full.Package("win-x64", false)?.Name != "odyle-0.6.0.3-win-x64.zip" || ReleaseFeed.Describe("odyle-0.6.0.3-win-x64-setup.exe") is not null || ReleaseFeed.Describe("odyle-0.6.0.3-win-arm64-setup.exe") is not null || full.Assets.Any(a => ReleaseFeed.Describe(a.Name) is not null && !a.Name.EndsWith(".zip"))) throw new Exception("The installer must never be picked as an update package.");
        // Held-back versions (#19): never install one, and move off one this build runs, even to an older
        // release; a held-back latest release is never chosen, so a rollback cannot loop.
        Version V(string text) => ReleaseFeed.TryParseTag(text, out var parsed) ? parsed : throw new Exception("bad version " + text);
        if (ReleaseFeed.Choose(full, V("0.6.0.2"), [], "win-x64", false) != full || ReleaseFeed.Choose(full, V("0.6.0.3"), [], "win-x64", false) is not null || ReleaseFeed.Choose(null, V("0.6.0.2"), [], "win-x64", false) is not null) throw new Exception("Choosing the latest release failed.");
        if (ReleaseFeed.Choose(full, V("0.6.0.2"), [V("0.6.0.3")], "win-x64", false) is not null) throw new Exception("A held-back release was chosen.");
        if (ReleaseFeed.Choose(full, V("0.7.0"), [V("0.7.0")], "win-x64", false) != full || ReleaseFeed.Choose(full, V("0.7.0"), [], "win-x64", false) is not null) throw new Exception("A held-back build must go back to the latest release, and only then.");
        if (ReleaseFeed.Choose(full, V("0.6.0.3"), [V("0.6.0.3")], "win-x64", false) is not null || ReleaseFeed.Choose(full, V("0.7.0"), [V("0.7.0")], "win-x64", true) != full) throw new Exception("Going back needs another release with this machine's package.");
        if (!ReleaseFeed.HeldBack(["0.18.0", "v0.18.0", "latest", ""]).SequenceEqual([V("0.18.0")]) || ReleaseFeed.HeldBack(null).Length != 0) throw new Exception("Held-back versions parsing failed.");
        if (System.Text.Json.JsonSerializer.Deserialize<WindowsReleaseInfo>("""{"latest":null,"blocked":["0.18.0"]}""", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) is not { Blocked: ["0.18.0"] } || System.Text.Json.JsonSerializer.Deserialize<WindowsReleaseInfo>("""{"latest":null}""") is not { Blocked: null }) throw new Exception("The windows-release answer was not read.");
        // The check at start is tried again soon until it reaches GitHub, and what it finds installs at once;
        // afterwards the timer looks hourly and checks once a day after GitHub last said this build is current.
        var schedule = new UpdateSchedule(); var started = DateTimeOffset.Parse("2026-10-15T08:00:00Z");
        if (!schedule.AtStart || !schedule.Due(started) || UpdateSchedule.First != TimeSpan.FromSeconds(5)) throw new Exception("The first update check must run soon after start.");
        var retries = new List<TimeSpan>(); for (var i = 0; i < 6; i++) { schedule.Failed(); retries.Add(schedule.Next); }
        if (!schedule.AtStart || !retries.Select(r => r.TotalSeconds).SequenceEqual([15, 30, 60, 120, 300, 300])) throw new Exception("A start check without network must be retried soon.");
        schedule.Reached(started.AddMinutes(9), upToDate: true);
        if (schedule.AtStart || schedule.Next != TimeSpan.FromHours(1) || schedule.Due(started.AddHours(20)) || !schedule.Due(started.AddMinutes(9).AddDays(1))) throw new Exception("After the start check, updates are checked once a day.");
        schedule.Failed();
        if (schedule.AtStart || schedule.Next != TimeSpan.FromHours(1)) throw new Exception("A later failed check must wait for the hourly tick.");
        var found = new UpdateSchedule(); found.Reached(started, upToDate: false);
        if (found.AtStart || !found.Due(started.AddHours(1))) throw new Exception("A release found but not installed must be looked for again.");
        Console.WriteLine("Release feed and update schedule passed.");
    }
}
