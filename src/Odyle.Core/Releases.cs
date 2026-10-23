using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Odyle.Core;
// A file attached to a GitHub release of the Windows companion.
public sealed record ReleaseAsset(string Name, long Size, string Url);
// What a package name says about its contents: odyle-<version>-<runtime>[-self-contained].zip.
public sealed record PackageKind(Version Version, string Runtime, bool SelfContained);
// A published release. The release workflow attaches one ZIP per package kind and a
// .sha256 file beside each, written by scripts/publish.ps1 as "<hash>  <file name>".
public sealed record AppRelease(Version Version, string Tag, string Url, DateTimeOffset? PublishedAt, ReleaseAsset[] Assets)
{
    public ReleaseAsset? Package(string runtime, bool selfContained) => Assets.FirstOrDefault(a => ReleaseFeed.Describe(a.Name) is { } kind && kind.Runtime == runtime && kind.SelfContained == selfContained);
    public ReleaseAsset? Checksum(ReleaseAsset package) => Assets.FirstOrDefault(a => a.Name == package.Name + ".sha256");
    public bool IsNewerThan(Version current) => Version > ReleaseFeed.Normalize(current);
}
// The GitHub releases of this repository, where every push to main that changes what ships
// publishes a release tagged v<Version>, a semantic version (AGENTS.md); releases before
// that were v0.6.0.<run number>, and missing parts compare as zero. Only full versions count
// as releases, so a tag like "nightly" or a pre-release is never offered as an update.
public static class ReleaseFeed
{
    public const string Repository = "oldbear24/odyle-windows";
    public static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/latest");
    private static readonly Regex Package = new(@"^odyle-(?<version>\d+(?:\.\d+){1,3})-(?<runtime>win-(?:x64|arm64))(?<bundled>-self-contained)?\.zip$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    public static PackageKind? Describe(string assetName)
    {
        var match = Package.Match(assetName);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) ? new PackageKind(Normalize(version), match.Groups["runtime"].Value, match.Groups["bundled"].Success) : null;
    }
    // Which release this build installs (#19): the latest one when it is newer and has this
    // machine's package, never a version the paired backend holds back, and, when this build's own
    // version is held back, the latest one even if it is older. A held-back latest release is
    // never chosen, so a rollback cannot loop between two versions.
    public static AppRelease? Choose(AppRelease? latest, Version current, IEnumerable<Version> heldBack, string runtime, bool selfContained)
    {
        if (latest is null || latest.Package(runtime, selfContained) is null) return null;
        var held = heldBack.Select(Normalize).ToHashSet();
        if (held.Contains(latest.Version)) return null;
        if (latest.IsNewerThan(current)) return latest;
        return held.Contains(Normalize(current)) && latest.Version != Normalize(current) ? latest : null;
    }
    // The versions a windows-release answer holds back ("0.18.0"); none when it names none.
    public static Version[] HeldBack(IEnumerable<string>? versions) => (versions ?? []).Select(v => TryParseTag(v, out var version) ? version : null).OfType<Version>().Distinct().ToArray();
    // Missing components count as zero, so 0.5.0 and 0.5.0.0 compare equal.
    public static Version Normalize(Version? version) => version is null ? new Version(0, 0, 0, 0) : new Version(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = Normalize(null);
        var text = tag?.Trim() ?? "";
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Regex.IsMatch(text, @"^\d+(\.\d+){1,3}$") || !Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed); return true;
    }
    // Parses GitHub's "latest release" JSON; null for drafts, pre-releases and tags that are not versions.
    // An answer that is not that JSON (a proxy's sign-in page, a cut-off body) is a FormatException
    // with a message for the settings window, never a bare JsonException.
    public static AppRelease? Parse(string json)
    {
        try { return Read(json); }
        catch (Exception error) when (error is JsonException or FormatException) { throw new FormatException(Localizer.T("GitHub sent an answer the app cannot read."), error); }
    }
    private static AppRelease? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
        var tag = Text(root, "tag_name");
        if (!TryParseTag(tag, out var version)) return null;
        var url = Text(root, "html_url");
        if (!url.StartsWith("https://github.com/", StringComparison.Ordinal)) url = ReleasesPage.ToString();
        DateTimeOffset? published = DateTimeOffset.TryParse(Text(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var asset in list.EnumerateArray())
            {
                var name = Text(asset, "name"); var download = Text(asset, "browser_download_url");
                // Packages are only ever fetched from GitHub's own release storage.
                if (name.Length == 0 || !download.StartsWith("https://github.com/", StringComparison.Ordinal)) continue;
                assets.Add(new ReleaseAsset(name, asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0, download));
            }
        return new AppRelease(version, tag, url, published, assets.ToArray());
    }
    // The hash a .sha256 file states for exactly this asset, or null when it names another file.
    public static string? ExpectedHash(string checksumFile, string assetName)
    {
        foreach (var line in checksumFile.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == assetName && Hash.IsMatch(parts[0].ToLowerInvariant())) return parts[0].ToLowerInvariant();
        }
        return null;
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
// When a released build looks for a newer release. The first check runs a few seconds after
// start; while it cannot reach GitHub (no network yet when Windows starts) it is tried again
// after 15 s, 30 s, 1 min, 2 min and then every 5 minutes (Retry.Backoff). What the start
// check finds is installed right away. Afterwards the timer looks every hour and checks once
// a day, and a release found then waits for a restart or the next start.
public sealed class UpdateSchedule
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(5), Tick = TimeSpan.FromHours(1), Daily = TimeSpan.FromDays(1);
    private DateTimeOffset upToDateAt = DateTimeOffset.MinValue;
    private int failures;
    // True until a check of this start reaches GitHub.
    public bool AtStart { get; private set; } = true;
    public bool Due(DateTimeOffset now) => now - upToDateAt >= Daily;
    // GitHub answered: nothing newer for this PC, or a release to download.
    public void Reached(DateTimeOffset now, bool upToDate) { AtStart = false; if (upToDate) upToDateAt = now; }
    public void Failed() => failures++;
    public TimeSpan Next => AtStart ? Retry.Backoff(failures - 1) : Tick;
}
