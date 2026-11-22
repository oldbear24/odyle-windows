using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// When and how this build updates itself (W#90: moved out of MainWindow, which shows it in Settings and on the
// card). A released build looks for a newer GitHub release at every start and once a day and downloads and
// verifies it in the background (Updater). What the first automatic check of a start finds is installed right
// away and restarts; one found later offers a restart and otherwise installs at the next start. Versions the
// paired backend holds back (#19) are never installed, and a build that is held back moves off it at once; the
// server's operators can ask every app to update now.
//
// The timer looks every hour (UpdateSchedule), so a day spent asleep catches up within the hour. The package's
// files are read, checked, extracted, copied and removed off the UI thread (#87). The checks replace the seams
// (IsRelease, Check, Download, FindStaged, Forget, Apply, CleanUp); the app uses Updater.
internal sealed class UpdateFlow
{
    internal readonly DispatcherTimer Timer = new() { Interval = UpdateSchedule.First };
    internal readonly UpdateSchedule Schedule = new();
    // A newer package downloaded and verified, waiting to be installed.
    internal StagedUpdate? Staged;
    // A check runs (Settings disables its button), and what Settings says of it.
    internal bool Checking;
    internal string Note = "";
    // The versions the paired backend holds back, as it last said.
    internal Version[] HeldBack = [];
    // The server's operators asked every app to update now: what the running check finds is installed at once.
    internal bool UpdateNow;
    // Installing is one at a time (a click while the server's operators ask every app to update, say). The copy on
    // its way, which quitting waits for: a swap cut short would leave no Odyle.exe. Installed: the copy succeeded,
    // so a quit asked for meanwhile restarts into the new build.
    internal bool Installing;
    internal Task InstallRun = Task.CompletedTask;
    internal volatile bool Installed;

    internal Func<bool> IsRelease = () => Updater.IsReleaseBuild;
    internal Func<IReadOnlyCollection<Version>, CancellationToken, Task<AppRelease?>> Check = (held, token) => Updater.CheckAsync(held, token);
    internal Func<AppRelease, IProgress<double>, CancellationToken, Task<StagedUpdate>> Download = (release, progress, token) => Updater.DownloadAsync(release, progress, token);
    internal Func<StagedUpdate?> FindStaged = () => Updater.Staged();
    internal Action<IEnumerable<Version>> Forget = held => Updater.Forget(held);
    internal Action<StagedUpdate> Apply = update => Updater.Apply(update);
    internal Action CleanUp = () => Updater.CleanUp();

    private readonly Func<PartyApi?> api;
    private readonly Func<bool> quitting, busy;
    private readonly Action<bool> changed;
    private readonly Action<StagedUpdate> ready;
    private readonly Action<string> status;
    private readonly Func<Task> restart;
    // api: the paired server's request client, asked for the held-back versions. changed(card): Settings shows the
    // update again, and the card too when card is true. ready: a package found later than the start's check waits
    // (the window tells the player once per version). status: a footer line. restart: quit and start the new build.
    internal UpdateFlow(Func<PartyApi?> api, Func<bool> quitting, Func<bool> busy, Action<bool> changed, Action<StagedUpdate> ready, Action<string> status, Func<Task> restart)
    {
        this.api = api; this.quitting = quitting; this.busy = busy; this.changed = changed; this.ready = ready; this.status = status; this.restart = restart;
    }
    // The versions the paired backend holds back, asked before each update check; none without a pairing or from
    // an older backend, which leaves the check as GitHub says.
    private async Task<Version[]> HeldBackVersionsAsync()
    {
        if (api() is not { } current) return HeldBack;
        try { HeldBack = await current.GetHeldBackAsync(); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ObjectDisposedException or System.Text.Json.JsonException) { Log.Info($"Held-back versions not known: {error.Message}"); }
        return HeldBack;
    }
    // The server says which versions it holds back now: a downloaded one is dropped, and this build moves off one it runs.
    internal async Task HeldBackAsync(Version[] held)
    {
        // Off the UI thread: Forget waits for a package being copied in (#87).
        HeldBack = held; await Task.Run(() => Forget(held));
        if (Staged is { } waiting && held.Contains(waiting.Version)) { Log.Info($"Odyle {waiting.Version} is held back; its download is dropped."); Staged = null; changed(true); }
        if (held.Contains(Updater.Current)) { Log.Info($"Odyle {Updater.Current} is held back; looking for the latest release."); await CheckAsync(manual: false); }
    }
    // The timer's tick. It always starts the timer again (unless Odyle is quitting), also after a failure nobody
    // expected, so update checks never stop for the rest of the session.
    internal async Task TickAsync()
    {
        Timer.Stop();
        try { if (Staged is null && Schedule.Due(DateTimeOffset.UtcNow)) { await Task.Run(CleanUp); await CheckAsync(manual: false); } }
        finally { if (!quitting()) { Timer.Interval = Schedule.Next; Timer.Start(); } }
    }
    // "Update all apps now" on the dashboard's admin pages: a build older than the latest release installs it at once
    // and restarts, handing its live character over. The release is checked and verified like any update, so a
    // held-back version is never installed.
    internal async Task UpdateNowAsync(UpdateRequest asked)
    {
        if (!IsRelease() || quitting()) return;
        if (!asked.Needed(Updater.Current)) { Log.Info($"The server's operators asked every app to update; Odyle {Updater.Current} is up to date."); return; }
        Log.Info($"The server's operators asked every app to update{(asked.Version is { } latest ? $" to {latest}" : "")}; updating now.");
        UpdateNow = true;
        if (Staged is not null) await InstallAsync(automatic: true);
        else await CheckAsync(manual: false);
    }
    internal async Task CheckAsync(bool manual)
    {
        if (Checking || Staged is not null || !IsRelease()) return;
        Checking = true; Note = T("checking GitHub for a newer release…"); changed(false);
        var install = false;
        try
        {
            // The progress reaches the window once per whole percent.
            var held = await HeldBackVersionsAsync(); await Task.Run(() => Forget(held));
            var release = await Check(held, CancellationToken.None);
            // What the first automatic check of this start to reach GitHub finds is installed right away.
            install = Schedule.AtStart && !manual; Schedule.Reached(DateTimeOffset.UtcNow, upToDate: release is null);
            // Moving off a held-back version is installed at once: the next start would not install an older package.
            if (release is not null && release.Version < Updater.Current) { install = true; Log.Info($"Odyle {Updater.Current} is held back; going back to {release.Version}."); }
            if (release is null) { Note = T("up to date · checked {0:HH:mm}", DateTime.Now); Log.Info($"Update check: Odyle {Updater.Current} is up to date."); UpdateNow = false; return; }
            // A package an earlier start downloaded but could not install is not fetched again.
            // (Not when going back: a staged package is always newer than this build.)
            if (release.Version > Updater.Current && await Task.Run(FindStaged) is { } found && found.Version >= release.Version && !HeldBack.Contains(found.Version)) Staged = found;
            else
            {
                Log.Info($"Update check: downloading Odyle {release.Version} ({Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")}).");
                Note = T("downloading Odyle {0}…", release.Version); changed(false);
                var progress = new Progress<double>(part => { Note = T("downloading Odyle {0} · {1:P0}", release.Version, part); changed(false); });
                Staged = await Task.Run(() => Download(release, progress, CancellationToken.None));
            }
            Note = ""; Log.Info($"Odyle {Staged.Version} is downloaded, verified and waiting in {Staged.Directory}.");
            // Installed below, after the finally (a return here would skip it, as it did from #1 to 0.36.1).
            if (!install && !UpdateNow) ready(Staged);
        }
        // An answer that is not GitHub's release data (a proxy's page, say) is a failed check too (ReleaseFeed.Parse).
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            Schedule.Failed(); Log.Warn($"Update check failed: {error.Message}");
            Note = manual ? T("update check failed: {0}", T(error.Message)) : T("could not check for updates: {0}", T(error.Message));
        }
        // The card's hint for an outdated adapter names a downloaded update.
        finally { Checking = false; changed(true); }
        // A request to update now that came while this check ran is honoured here too.
        if ((install || UpdateNow) && Staged is not null) await InstallAsync(automatic: true);
        else UpdateNow = false;
    }
    // The files are swapped while this build still runs; only then does it stop and start the new one. A swap that
    // fails leaves everything as it was. An installation at start does not wait for a running operation to finish:
    // quitting waits for it.
    internal async Task InstallAsync(bool automatic)
    {
        if (Staged is not { } update || quitting() || Installing || (busy() && !automatic)) return;
        Log.Info($"Installing Odyle {update.Version}{(UpdateNow ? " as the server's operators asked" : automatic ? " found at start" : "")} and restarting.");
        Installing = true;
        try { await (InstallRun = Task.Run(() => { Apply(update); Installed = true; })); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error($"Odyle {update.Version} could not be installed", error);
            status(T("Could not install the update: {0} Download it from the release page instead.", T(error.Message))); return;
        }
        finally { Installing = false; }
        status(T("Installing Odyle {0} · restarting…", update.Version));
        await restart();
    }
}
