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
// The GitHub releases of this repository, where every push to main publishes a
// release tagged v<Version>.<run number>. Only full versions count as releases, so
// a tag like "nightly" or a pre-release is never offered as an update.
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
    public static AppRelease? Parse(string json)
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
