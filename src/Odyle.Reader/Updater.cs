using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// A newer package that was downloaded, checked against the release checksum and
// extracted under %LOCALAPPDATA%\Odyle\updates\<version>, waiting to be installed.
public sealed record StagedUpdate(Version Version, string Directory, string ReleaseUrl);
// Keeps a released Odyle current from the GitHub releases of its repository.
//
// Release builds carry the workflow's run number (0.5.0.12); `dotnet run` and the
// checks are 0.x.y.0 and never replace themselves. The package kind (small or
// self-contained) is stamped into the assembly by the publish script and the CPU
// architecture comes from the running process, so the same kind is downloaded.
// Windows lets a running executable be renamed but not overwritten or deleted, so
// installing moves Odyle.exe aside to Odyle.exe.old, copies the new files in and
// relaunches; the next start removes the old file.
public static class Updater
{
    public static readonly Version Current = ReleaseFeed.Normalize(typeof(Updater).Assembly.GetName().Version);
    public static readonly bool SelfContained = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "SelfContained" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
    public static readonly string Runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    public static readonly string? Executable = Environment.ProcessPath;
    // Built by the release workflow (scripts/publish.ps1 -Version marks it OdyleRelease). Builds from
    // before semantic versions were told apart by the run number in their version (0.6.0.18); the
    // semantic ones, 0.7.0 and 0.8.0, had neither and took themselves for development builds.
    public static readonly bool Released = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "OdyleRelease" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase)) || Current.Revision > 0;
    public static bool IsReleaseBuild => Released && Executable is not null;
    public static string Staging { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "updates");
    // Why the last automatic installation at start did not happen, shown in settings.
    public static string? LastFailure { get; set; }
    private const long PackageLimit = 512L * 1024 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };

    // The newest published release, whatever its version; null when there is none yet.
    public static async Task<AppRelease?> FetchLatestAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(ReleaseFeed.Latest, "application/vnd.github+json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(T("GitHub answered {0}.", (int)response.StatusCode), null, response.StatusCode);
        return ReleaseFeed.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }
    // A release newer than this build that has a package for this machine, else null.
    public static async Task<AppRelease?> CheckAsync(CancellationToken token)
    {
        var release = await FetchLatestAsync(token);
        return release is not null && release.IsNewerThan(Current) && release.Package(Runtime, SelfContained) is not null ? release : null;
    }
    // Downloads this machine's package, verifies it against the release's .sha256 file
    // and extracts it; the folder is renamed into place only once everything checked out.
    public static async Task<StagedUpdate> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken token)
    {
        var package = release.Package(Runtime, SelfContained) ?? throw new InvalidOperationException(T("Release {0} has no package for this PC ({1}).", release.Version, Runtime));
        var checksum = release.Checksum(package) ?? throw new InvalidOperationException(T("Release {0} has no checksum for {1}.", release.Version, package.Name));
        Directory.CreateDirectory(Staging);
        string target = Path.Combine(Staging, release.Version.ToString()), partial = target + ".partial", archive = Path.Combine(Staging, package.Name);
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        try
        {
            var expected = ReleaseFeed.ExpectedHash(await ReadTextAsync(new Uri(checksum.Url), token), package.Name) ?? throw new InvalidOperationException(T("The release checksum does not name {0}.", package.Name));
            await DownloadFileAsync(new Uri(package.Url), archive, package.Size, progress, token);
            string actual;
            await using (var stream = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException(T("The downloaded package does not match the release checksum."));
            // ExtractToDirectory refuses entries that would land outside the folder.
            ZipFile.ExtractToDirectory(archive, partial, overwriteFiles: true);
            if (StampedVersion(Path.Combine(partial, "Odyle.exe")) != release.Version) throw new InvalidOperationException(T("The package does not contain Odyle {0}.", release.Version));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(partial, target);
        }
        finally { Try(() => File.Delete(archive)); Try(() => { if (Directory.Exists(partial)) Directory.Delete(partial, true); }); }
        return new StagedUpdate(release.Version, target, release.Url);
    }
    // A package an earlier run downloaded and verified, while it is still newer than this build.
    public static StagedUpdate? Staged()
    {
        if (!Directory.Exists(Staging)) return null;
        StagedUpdate? best = null;
        foreach (var directory in Directory.EnumerateDirectories(Staging))
        {
            if (!ReleaseFeed.TryParseTag(Path.GetFileName(directory), out var version) || version <= Current || StampedVersion(Path.Combine(directory, "Odyle.exe")) != version) continue;
            if (best is null || version > best.Version) best = new StagedUpdate(version, directory, $"https://github.com/{ReleaseFeed.Repository}/releases/tag/v{version}");
        }
        return best;
    }
    public static void Apply(StagedUpdate update) => Apply(update.Directory, Path.GetDirectoryName(Executable) ?? throw new InvalidOperationException(T("The application folder is unknown.")));
    // Copies the staged files over the installed ones. The running executable cannot be
    // overwritten, so it moves aside first; a failure puts it back and this build keeps working.
    public static void Apply(string source, string target)
    {
        var aside = new List<(string Path, string Old)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                {
                    var old = destination + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(destination, old); aside.Add((destination, old));
                }
                File.Copy(file, destination, overwrite: true);
            }
        }
        catch
        {
            foreach (var (path, old) in aside) { Try(() => File.Delete(path)); Try(() => File.Move(old, path)); }
            throw;
        }
    }
    public static void Relaunch(bool tray)
    {
        var exe = Executable ?? throw new InvalidOperationException(T("The application path is unknown."));
        Process.Start(new ProcessStartInfo(exe, tray ? StartupCommand.TrayArgument : "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
    }
    // Removes the executable an update moved aside and packages that are no longer newer than this build.
    public static void CleanUp()
    {
        if (Executable is not null) Try(() => File.Delete(Executable + ".old"));
        if (!Directory.Exists(Staging)) return;
        foreach (var directory in Directory.EnumerateDirectories(Staging))
            if (!ReleaseFeed.TryParseTag(Path.GetFileName(directory), out var version) || version <= Current) Try(() => Directory.Delete(directory, true));
        foreach (var file in Directory.EnumerateFiles(Staging)) Try(() => File.Delete(file));
    }
    // The version Windows reads from an executable's resources, or null when there is none.
    private static Version? StampedVersion(string exe)
    {
        if (!File.Exists(exe)) return null;
        try { return ReleaseFeed.TryParseTag(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var version) ? version : null; }
        catch (IOException) { return null; }
    }
    private static HttpRequestMessage Request(Uri url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"Odyle/{Current} (+https://github.com/{ReleaseFeed.Repository})");
        request.Headers.Accept.ParseAdd(accept);
        return request;
    }
    private static async Task<string> ReadTextAsync(Uri url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(url, "*/*");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        return text.Length <= 4096 ? text : throw new InvalidOperationException(T("The checksum file is unexpectedly large."));
    }
    private static async Task DownloadFileAsync(Uri url, string path, long expectedSize, IProgress<double>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var request = Request(url, "*/*");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        if (total > PackageLimit) throw new InvalidOperationException(T("The package is unexpectedly large."));
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16]; long done = 0; int read;
        while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token); done += read;
            if (done > PackageLimit) throw new InvalidOperationException(T("The package is unexpectedly large."));
            if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
        }
    }
    private static void Try(Action action) { try { action(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
