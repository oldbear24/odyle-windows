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
// Release builds are marked by the publish script (Released); `dotnet run` and the
// checks are not, and never replace themselves. The package kind (small or
// self-contained) is stamped into the assembly by the publish script and the CPU
// architecture comes from the running process, so the same kind is downloaded.
// Windows lets a running executable be renamed but not overwritten or deleted, so
// installing moves Odyle.exe aside to Odyle.exe.old, copies the new files in and
// relaunches. The new build keeps the old file until it started healthy (#80, below).
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
    // The largest package a download takes; the checks lower it.
    public static long PackageLimit { get; set; } = 512L * 1024 * 1024;
    private static HttpClient Http = NewClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }, disposeHandler: true);
    // The checks pass a handler that answers like GitHub, as PartyApi takes one; the app always uses the network.
    public static void UseHandler(HttpMessageHandler handler) => Http = NewClient(handler, disposeHandler: false);
    private static HttpClient NewClient(HttpMessageHandler handler, bool disposeHandler) => new(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };

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
    // The release to install: newer than this build, with a package for this machine and not held
    // back, or, while this build is held back, the latest one even if older (ReleaseFeed.Choose).
    // A version this PC rolled back from is skipped like a held-back one.
    public static async Task<AppRelease?> CheckAsync(IReadOnlyCollection<Version> heldBack, CancellationToken token) =>
        ReleaseFeed.Choose(await FetchLatestAsync(token), Current, Executable is { } exe && FailedVersion(exe, Current) is { } failed ? [.. heldBack, failed] : heldBack, Runtime, SelfContained);
    // Drops downloaded packages of held-back versions, so none is installed at the next start.
    // Takes the swap's lock, so a package being copied in (Apply) is never removed halfway; the window
    // calls it off its UI thread.
    public static void Forget(IEnumerable<Version> heldBack)
    {
        lock (Swap) ForgetLocked(heldBack);
    }
    private static void ForgetLocked(IEnumerable<Version> heldBack)
    {
        if (!Directory.Exists(Staging)) return;
        foreach (var version in heldBack)
        {
            var directory = Path.Combine(Staging, ReleaseFeed.Normalize(version).ToString());
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Warn($"Could not drop the held-back Odyle {version}: {error.Message}"); }
        }
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
        var failed = Executable is { } exe ? FailedVersion(exe, Current) : null;
        foreach (var directory in Directory.EnumerateDirectories(Staging))
        {
            if (!ReleaseFeed.TryParseTag(Path.GetFileName(directory), out var version) || version <= Current || version == failed || StampedVersion(Path.Combine(directory, "Odyle.exe")) != version) continue;
            if (best is null || version > best.Version) best = new StagedUpdate(version, directory, $"https://github.com/{ReleaseFeed.Repository}/releases/tag/v{version}");
        }
        return best;
    }
    public static void Apply(StagedUpdate update) => Apply(update.Directory, Path.GetDirectoryName(Executable) ?? throw new InvalidOperationException(T("The application folder is unknown.")));
    // Copies the staged files over the installed ones. The running executable cannot be
    // overwritten, so it moves aside first; a failure puts it back and this build keeps working.
    // The window installs off its UI thread (#87); the lock keeps ConfirmStart, which removes the
    // .old of this start, from running while the swap moves this build to .old.
    public static void Apply(string source, string target)
    {
        lock (Swap) ApplyLocked(source, target);
    }
    private static readonly object Swap = new();
    private static void ApplyLocked(string source, string target)
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
        // The new build counts its own starts from none, and a version rolled back from earlier is no
        // longer skipped. This start's own count is over: .old now holds this build, the new one's fallback.
        foreach (var (path, _) in aside) { Try(() => File.Delete(path + Starts)); Try(() => File.Delete(path + Failed)); }
        pending = null;
    }
    public static void Relaunch(bool tray)
    {
        var exe = Executable ?? throw new InvalidOperationException(T("The application path is unknown."));
        Process.Start(new ProcessStartInfo(exe, tray ? StartupCommand.TrayArgument : "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
    }
    // Removes packages that are no longer newer than this build, and the executable a rollback moved
    // aside once this build is that version or newer (a reinstall). The executable an update moved aside
    // stays until this build started healthy (ConfirmStart).
    // Takes the swap's lock like Forget.
    public static void CleanUp()
    {
        lock (Swap) CleanUpLocked();
    }
    private static void CleanUpLocked()
    {
        if (IsReleaseBuild && File.Exists(Executable + Failed) && FailedVersion(Executable!, Current) is null) Try(() => File.Delete(Executable + Failed));
        if (!Directory.Exists(Staging)) return;
        foreach (var directory in Directory.EnumerateDirectories(Staging))
            if (!ReleaseFeed.TryParseTag(Path.GetFileName(directory), out var version) || version <= Current) Try(() => Directory.Delete(directory, true));
        foreach (var file in Directory.EnumerateFiles(Staging)) Try(() => File.Delete(file));
    }
    // ----- Start health (#80) -----
    // An update leaves the build it replaced as Odyle.exe.old. The new build counts its starts in
    // Odyle.exe.starts ("<version> <count>") until one proves healthy: 30 seconds after its window was
    // built, or an orderly quit after that (App). Only then is .old deleted. A build that fails to start
    // twice in a row (it ended before proving healthy, or could not build its window) puts .old back and
    // keeps itself as Odyle.exe.failed, whose version the update checks skip until a newer release is out.
    // App calls these for released builds only: development builds and the checks never touch the files.
    public enum StartHealth { Settled, Pending, RolledBack }
    public const int FailedStartsLimit = 2;
    private const string Old = ".old", Starts = ".starts", Failed = ".failed";
    // The executable whose start still counts, until ConfirmStart.
    private static string? pending;
    private static Version pendingVersion = Current;
    public static bool StartPending => pending is not null;
    // Called once a start owns the single-instance guard, before anything that could fail: answers
    // RolledBack when the two starts before this one failed and .old is back (the caller relaunches it).
    public static StartHealth BeginStart(string exe, Version current)
    {
        pending = null; current = ReleaseFeed.Normalize(current);
        var marker = exe + Starts;
        if (!File.Exists(exe + Old)) { Try(() => File.Delete(marker)); return StartHealth.Settled; }
        var (version, count) = ReadStarts(marker);
        if (version != current) count = 0;
        // Healthy already, but .old could not be deleted then (the build before still exiting).
        if (count < 0) { Try(() => File.Delete(exe + Old)); if (!File.Exists(exe + Old)) Try(() => File.Delete(marker)); return StartHealth.Settled; }
        if (count >= FailedStartsLimit && RollBack(exe, current)) return StartHealth.RolledBack;
        WriteStarts(marker, current, count + 1);
        pending = exe; pendingVersion = current;
        return StartHealth.Pending;
    }
    // This start failed before its window was built: answers true when that was the second failed start
    // in a row and .old is back (the caller relaunches it); false when it should simply end.
    public static bool FailedStart(string exe, Version current)
    {
        if (pending != exe) return false;
        current = ReleaseFeed.Normalize(current);
        var (version, count) = ReadStarts(exe + Starts);
        return version == current && count >= FailedStartsLimit && RollBack(exe, current);
    }
    // This start proved healthy: the build before it is no longer needed.
    // An update being copied in meanwhile (Apply) ends this start's count itself, so the UI thread does
    // not wait for the copy: the confirmation is skipped.
    public static void ConfirmStart()
    {
        if (!Monitor.TryEnter(Swap)) return;
        try
        {
            if (pending is not { } exe) return;
            pending = null;
            Try(() => File.Delete(exe + Old));
            if (File.Exists(exe + Old)) WriteStarts(exe + Starts, pendingVersion, -1); else Try(() => File.Delete(exe + Starts));
            Log.Info(File.Exists(exe + Old) ? "This start is healthy; the previous build could not be removed yet." : "This start is healthy; the previous build was removed.");
        }
        finally { Monitor.Exit(Swap); }
    }
    // Puts the build before back in place of the running one, which stays as Odyle.exe.failed.
    public static bool RollBack(string exe, Version current)
    {
        string old = exe + Old, failed = exe + Failed;
        try
        {
            if (File.Exists(failed)) File.Delete(failed);
            File.Move(exe, failed);
            try { File.Move(old, exe); }
            catch { File.Move(failed, exe); throw; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Odyle {current} failed to start twice, but the previous build could not be put back", error);
            return false;
        }
        Try(() => File.Delete(exe + Starts)); pending = null;
        Log.Warn($"Odyle {current} failed to start twice; the previous build is back, and update checks skip {current} until a newer release is out.");
        return true;
    }
    // The version a rollback moved away from, while it is newer than this build; null otherwise.
    public static Version? FailedVersion(string exe, Version current) => StampedVersion(exe + Failed) is { } failed && failed > ReleaseFeed.Normalize(current) ? failed : null;
    private static (Version? Version, int Count) ReadStarts(string marker)
    {
        try
        {
            var parts = File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 && ReleaseFeed.TryParseTag(parts[0], out var version) && int.TryParse(parts[1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var count) ? (version, count) : (null, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (null, 0); }
    }
    private static void WriteStarts(string marker, Version version, int count) =>
        Try(() => File.WriteAllText(marker, $"{ReleaseFeed.Normalize(version)} {count.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
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
        if (progress is not null) progress = new WholePercent(progress);
        while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token); done += read;
            if (done > PackageLimit) throw new InvalidOperationException(T("The package is unexpectedly large."));
            if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
        }
    }
    private static void Try(Action action) { try { action(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
// A download's progress passed on only when its whole percent changes, as that percent: about a hundred
// reports per package instead of one per 64 KB read, each of which renders the update line (#87).
public sealed class WholePercent(IProgress<double> inner) : IProgress<double>
{
    private int last = -1;
    public void Report(double value)
    {
        var percent = (int)Math.Floor(Math.Clamp(value, 0, 1) * 100);
        if (percent == last) return;
        last = percent; inner.Report(percent / 100.0);
    }
}
