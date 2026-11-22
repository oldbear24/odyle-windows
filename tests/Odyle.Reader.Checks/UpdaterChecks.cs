using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Odyle.Core;
using Odyle.Reader;

// The updater's download and install against a stub GitHub (Updater.UseHandler), so they run in every
// build: a verified package is staged, and a checksum mismatch, a wrong stamped version, an oversized
// package or checksum file and a release without this PC's package all stage nothing. Then a failed
// copy rolls the install back, and Forget drops held-back packages.
static class UpdaterChecks
{
    const string Base = "https://github.com/oldbear24/odyle-windows/releases/download/";
    public static async Task Run()
    {
        var root = Directory.CreateTempSubdirectory("odyle-updater-check-").FullName;
        var staging = Updater.Staging; var limit = Updater.PackageLimit;
        var github = new Stub();
        try
        {
            Updater.Staging = Path.Combine(root, "updates"); Updater.UseHandler(github);
            // This checks executable is stamped 90.0.0.0: a package holding it as Odyle.exe is Odyle 90.0.0.
            var zip = Path.Combine(root, "package.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(Environment.ProcessPath!, "Odyle.exe");
                using var notices = new StreamWriter(archive.CreateEntry("THIRD-PARTY.md").Open()); notices.Write("notices");
            }
            var package = File.ReadAllBytes(zip); var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();

            // A verified package is extracted under its version, and the download is removed.
            var name = PackageName("90.0.0"); var release = Release("90.0.0", name);
            github.Answer(name, () => Bytes(package)); github.Answer(name + ".sha256", () => Text($"{hash}  {name}\n"));
            var reported = new List<double>();
            var staged = await Updater.DownloadAsync(release, new Recorder(reported.Add), CancellationToken.None);
            Check(staged.Version == release.Version && staged.Directory == Path.Combine(Updater.Staging, "90.0.0.0") && File.Exists(Path.Combine(staged.Directory, "Odyle.exe")) && File.ReadAllText(Path.Combine(staged.Directory, "THIRD-PARTY.md")) == "notices", "a verified package is staged under its version");
            Check(Leftovers().SequenceEqual(["90.0.0.0"]) && reported.Count > 0 && reported[^1] == 1, "the download is removed once staged, and progress reaches 100 %: " + string.Join(", ", Leftovers()));
            Check(github.Requests.All(r => r.UserAgent.StartsWith("Odyle/", StringComparison.Ordinal)), "every request names Odyle as its user agent");
            Directory.Delete(staged.Directory, true);

            // A package that does not match the release checksum is never extracted.
            github.Answer(name + ".sha256", () => Text($"{new string('0', 64)}  {name}\n"));
            await Fails(release, "does not match the release checksum", "a package that does not match its checksum");
            // A checksum file that names another file, or is far too large, stages nothing either.
            github.Answer(name + ".sha256", () => Text($"{hash}  odyle-90.0.0-other.zip\n"));
            await Fails(release, "does not name", "a checksum file for another package");
            github.Answer(name + ".sha256", () => Text($"{hash}  {name}\n" + new string(' ', 5000)));
            await Fails(release, "checksum file is unexpectedly large", "a checksum file above 4 KB");
            github.Answer(name + ".sha256", () => Text($"{hash}  {name}\n"));

            // A package whose Odyle.exe is stamped with another version than the release's is refused.
            var other = PackageName("2.0.0"); var mislabelled = Release("2.0.0", other);
            github.Answer(other, () => Bytes(package)); github.Answer(other + ".sha256", () => Text($"{hash}  {other}\n"));
            await Fails(mislabelled, "does not contain Odyle 2.0.0", "a package stamped with another version");

            // Size limits: a declared size above the limit stops before reading, and a body that runs past
            // the limit without declaring its size stops as soon as it does.
            github.Answer(name, () => { var r = Bytes(package); r.Content.Headers.ContentLength = Updater.PackageLimit + 1; return r; });
            await Fails(release, "package is unexpectedly large", "a package that declares more than the limit");
            Updater.PackageLimit = package.Length / 2;
            github.Answer(name, () => { var r = Bytes(package); r.Content.Headers.ContentLength = null; Check(r.Content.Headers.ContentLength is null, "the stub sends no size"); return r; });
            await Fails(release, "package is unexpectedly large", "a package that runs past the limit");
            Updater.PackageLimit = limit;

            // A release without this PC's package, or without its checksum, is never downloaded.
            var requests = github.Requests.Count;
            try { await Updater.DownloadAsync(Release("90.0.0", "odyle-90.0.0-win-x86.zip"), null, CancellationToken.None); throw new Exception("Updater check failed: a release without this PC's package was downloaded."); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("has no package for this PC") && github.Requests.Count == requests, "a release without this PC's package fetches nothing"); }
            try { await Updater.DownloadAsync(new AppRelease(release.Version, release.Tag, release.Url, null, [new ReleaseAsset(name, package.Length, Base + "v90.0.0/" + name)]), null, CancellationToken.None); throw new Exception("Updater check failed: a package without a checksum was downloaded."); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("has no checksum") && github.Requests.Count == requests, "a package without a checksum fetches nothing"); }
            // GitHub's answer to the download is checked too.
            github.Answer(name, () => new HttpResponseMessage(HttpStatusCode.NotFound));
            try { await Updater.DownloadAsync(release, null, CancellationToken.None); throw new Exception("Updater check failed: a missing package was staged."); }
            catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.NotFound && Leftovers().Length == 0, "a package GitHub cannot find stages nothing"); }

            CopyRollback(root);
            ForgetChecks();
        }
        finally
        {
            Updater.Staging = staging; Updater.PackageLimit = limit; Updater.UseHandler(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
            Directory.Delete(root, true);
        }
        Console.WriteLine("Updater download, checksum, stamped version, size limits, missing package, copy rollback and Forget checks passed.");
    }

    // A file that cannot be copied in puts the moved executable back: the running build keeps working.
    static void CopyRollback(string root)
    {
        var source = Path.Combine(root, "rollback-source"); var target = Path.Combine(root, "rollback-target"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "Odyle.exe"), "new"); File.WriteAllText(Path.Combine(source, "zlocked.dll"), "new library");
        File.WriteAllText(Path.Combine(target, "Odyle.exe"), "running"); File.WriteAllText(Path.Combine(target, "zlocked.dll"), "old library");
        using (new FileStream(Path.Combine(target, "zlocked.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { Updater.Apply(source, target); throw new Exception("Updater check failed: a copy onto a locked file was reported as installed."); }
            catch (IOException) { }
        }
        Check(File.ReadAllText(Path.Combine(target, "Odyle.exe")) == "running", !File.Exists(Path.Combine(target, "Odyle.exe.old")), File.ReadAllText(Path.Combine(target, "zlocked.dll")) == "old library", "a failed copy puts the running executable back");
    }

    // Forget drops the staged packages of held-back versions only, whatever form their version takes.
    static void ForgetChecks()
    {
        Updater.Forget([new Version(9, 0, 0)]);
        Check(!Directory.Exists(Updater.Staging) || Directory.GetDirectories(Updater.Staging).Length == 0, "Forget without staged packages does nothing");
        foreach (var folder in new[] { "0.40.0.0", "0.41.0.0", "0.42.0.0" }) Directory.CreateDirectory(Path.Combine(Updater.Staging, folder));
        Updater.Forget([new Version(0, 40, 0), new Version(0, 42, 0, 0), new Version(0, 43, 0)]);
        Check(Directory.GetDirectories(Updater.Staging).Select(Path.GetFileName).SequenceEqual(["0.41.0.0"]), "Forget drops held-back packages and keeps the rest: " + string.Join(", ", Directory.GetDirectories(Updater.Staging).Select(Path.GetFileName)));
        Directory.Delete(Path.Combine(Updater.Staging, "0.41.0.0"));
    }

    static async Task Fails(AppRelease release, string reason, string what)
    {
        try { await Updater.DownloadAsync(release, null, CancellationToken.None); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains(reason), what + " fails for its reason: " + error.Message);
            Check(Leftovers().Length == 0, what + " leaves nothing staged: " + string.Join(", ", Leftovers()));
            return;
        }
        throw new Exception("Updater check failed: " + what + " was staged.");
    }
    static string[] Leftovers() => Directory.Exists(Updater.Staging) ? Directory.GetFileSystemEntries(Updater.Staging).Select(p => Path.GetFileName(p)).ToArray() : [];
    static string PackageName(string version) => $"odyle-{version}-{Updater.Runtime}{(Updater.SelfContained ? "-self-contained" : "")}.zip";
    static AppRelease Release(string version, string package) =>
        new(ReleaseFeed.Normalize(Version.Parse(version)), "v" + version, $"https://github.com/oldbear24/odyle-windows/releases/tag/v{version}", null,
            [new ReleaseAsset(package, 1000, Base + $"v{version}/" + package), new ReleaseAsset(package + ".sha256", 100, Base + $"v{version}/" + package + ".sha256")]);
    static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    sealed class Stub : HttpMessageHandler
    {
        readonly Dictionary<string, Func<HttpResponseMessage>> answers = [];
        public readonly List<(string File, string UserAgent)> Requests = [];
        public void Answer(string file, Func<HttpResponseMessage> answer) => answers[file] = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var file = Path.GetFileName(request.RequestUri!.AbsolutePath); Requests.Add((file, request.Headers.UserAgent.ToString()));
            return Task.FromResult(answers.TryGetValue(file, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    sealed class Recorder(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    // Counted and reported by the runner like every other Reader check; the scenario goes on after a failure.
    static void Check(bool condition, string message) => ReaderChecks.Check(condition, message);
    static void Check(bool a, bool b, bool c, string message) { Check(a, message + " (the executable)"); Check(b, message + " (no .old left)"); Check(c, message + " (the locked file)"); }
}
