using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Odyle.Core;
namespace Odyle.Reader;
public partial class App : System.Windows.Application
{
    // The UI checks build the application beside a possibly running Odyle; they skip
    // the single-instance guard and create their own window.
    public static bool Headless { get; set; }
    private Mutex? instance;
    // A second start signals the running one to bring its window forward, then ends; a start
    // with --unpair signals it to quit.
    private EventWaitHandle? showSignal, quitSignal;
    private RegisteredWaitHandle? showWait, quitWait;
    private PairPipe? pairPipe;
    private bool primary;
    private DateTimeOffset failuresSince, lastNotice = DateTimeOffset.MinValue;
    private int failures;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Headless) return;
        // --build-info <file>: what this build is, for the publish script; nothing else starts.
        if (StartupCommand.BuildInfoPath(e.Args) is { } infoPath)
        {
            try { System.IO.File.WriteAllText(infoPath, StartupCommand.BuildInfo(Updater.Current, Updater.Released, Updater.SelfContained, Updater.Runtime)); }
            finally { Shutdown(); }
            return;
        }
        // The language chosen in settings, else the Windows display language; numbers
        // and dates follow it (Localizer.Culture).
        Localizer.Use(PreferencesStore.Load().Language);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = Localizer.Culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Localizer.Culture.IetfLanguageTag)));
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.IsTerminating ? "Unhandled exception, Odyle stops" : "Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        // --tray (Start with Windows) opens only the tray icon.
        var tray = StartupCommand.InTray(e.Args); var user = WindowsIdentity.GetCurrent().User!.Value;
        // The uninstaller waits for --unpair to end, so it ends whatever goes wrong, without a message box.
        if (StartupCommand.Unpairs(e.Args))
        {
            try { Unpair(user); }
            catch (Exception error) { Log.Error("Disconnecting this PC failed (--unpair)", error); }
            finally { Shutdown(); }
            return;
        }
        instance = new Mutex(true, "Local\\Odyle-" + user, out primary);
        // Already running: a start by hand brings that window forward; a start with Windows just ends; an
        // Open in Odyle link (--pair) goes to the running Odyle, which asks before pairing.
        var pairLink = StartupCommand.PairLinkOf(e.Args);
        if (!primary) { if (pairLink is not null) { AllowSetForegroundWindow(-1); if (!PairPipe.Send(user, pairLink)) ShowRunning(user); } else if (!tray) ShowRunning(user); Shutdown(); return; }
        Log.Info($"Odyle {Updater.Current} starts{(tray ? " in the tray" : "")}: {Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")} package, {Environment.OSVersion}, language {Localizer.Language} ({Localizer.Culture.Name}).");
        // A newer version downloaded and verified by an earlier run replaces this
        // build before any window opens; the new version then starts in its place.
        if (Updater.IsReleaseBuild && Updater.Staged() is { } staged && TryInstall(staged, tray)) return;
        Updater.CleanUp(); Autostart.Repair(); ForgetStatOwners();
        if (Updater.IsReleaseBuild) PairProtocol.Register(Environment.ProcessPath);
        var window = new MainWindow(); MainWindow = window;
        // --decoder-trace: the patch-day trace, for re-deriving the decoder after a game patch.
        if (StartupCommand.Traces(e.Args)) window.StartTrace();
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Show-" + user);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Quit-" + user);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => window.Dispatcher.BeginInvoke(window.QuitOnRequest), null, Timeout.Infinite, executeOnlyOnce: true);
        pairPipe = new PairPipe(user, link => window.Dispatcher.BeginInvoke(() => window.OfferPairing(link)));
        if (!tray || pairLink is not null) window.Show();
        _ = window.StartAsync(tray);
        if (pairLink is not null) window.Dispatcher.BeginInvoke(() => window.OfferPairing(pairLink), DispatcherPriority.ApplicationIdle);
    }
    // "Odyle.exe --unpair", run by the uninstaller when asked to disconnect this PC: a running Odyle
    // quits first (it marks its reading stale and lets the uninstaller remove its files), then the
    // device key is forgotten on the server and the saved pairing on this PC. Nothing is shown, and
    // a running Odyle is not brought forward. Offline, only this PC forgets the pairing; the PC can
    // still be removed on the dashboard. A running Odyle this start may not signal (one started as
    // administrator) keeps running; the pairing is forgotten all the same.
    private static void Unpair(string user)
    {
        Log.Info("Disconnecting this PC from its account (--unpair).");
        try
        {
            if (EventWaitHandle.TryOpenExisting("Local\\Odyle-Quit-" + user, out var quit))
            {
                using (quit) quit.Set();
                using var running = new Mutex(false, "Local\\Odyle-" + user);
                try { if (running.WaitOne(TimeSpan.FromSeconds(15))) running.ReleaseMutex(); else Log.Warn("The running Odyle did not quit within 15 seconds."); }
                catch (AbandonedMutexException) { running.ReleaseMutex(); }
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or System.IO.IOException)
        {
            Log.Warn("The running Odyle could not be asked to quit: " + error.Message);
        }
        var saved = SessionStore.Load(out _);
        if (saved is null) { Log.Info("No pairing is saved on this PC."); return; }
        try
        {
            using var api = new PartyApi(saved.Endpoint, saved.Token, saved.DeviceKey);
            Task.Run(api.UnpairAsync).GetAwaiter().GetResult();
            Log.Info(saved.DeviceKey is null ? "The pairing had no device key; it is forgotten on this PC." : "The server forgot this PC's device key.");
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or ArgumentException or UriFormatException or InvalidOperationException)
        {
            Log.Warn("The server could not be told to forget this PC: " + error.Message);
        }
        SessionStore.Clear();
    }
    // Builds before the global-reference-v2 decoder kept stat owner ids in stat-owners.json. Odyle
    // is now read by its resource id, so the file is no longer used and is removed.
    private static void ForgetStatOwners()
    {
        try { System.IO.File.Delete(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "stat-owners.json")); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
    }
    // This start came from the user, so it may hand the foreground on to the running Odyle.
    private static void ShowRunning(string user)
    {
        AllowSetForegroundWindow(-1);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting("Local\\Odyle-Show-" + user, out var signal)) { using (signal) signal.Set(); return; }
            Thread.Sleep(100);
        }
        MessageBox.Show(Localizer.T("Odyle is already running. Open it from the system tray."), "Odyle");
    }
    // A failure in one click or timer tick is logged and the app keeps running, with a
    // short note at most once a minute. Many in a row mean it cannot work, so it stops.
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the window thread", e.Exception);
        var now = DateTimeOffset.UtcNow;
        if (now - failuresSince > TimeSpan.FromMinutes(1)) { failuresSince = now; failures = 0; }
        if (++failures > 10 || e.Exception is OutOfMemoryException) { Log.Error("Too many failures in a minute; Odyle stops."); return; }
        e.Handled = true;
        if (now - lastNotice < TimeSpan.FromMinutes(1)) return;
        lastNotice = now;
        MessageBox.Show(Localizer.T("Something went wrong, but Odyle keeps running. The details are in the log:\n{0}", Log.FilePath), "Odyle", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private bool TryInstall(StagedUpdate staged, bool tray)
    {
        Log.Info($"Installing the downloaded Odyle {staged.Version}.");
        try { Updater.Apply(staged); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { Updater.LastFailure = error.Message; Log.Error($"Odyle {staged.Version} could not be installed", error); return false; }
        Restart(tray); return true;
    }
    // Starts the executable in the application folder and ends this process. The
    // single-instance guard is released first so the new instance is not turned away.
    // A start in the tray (with Windows) restarts in the tray.
    public void Restart(bool tray)
    {
        Release();
        try { Updater.Relaunch(tray); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Updater.LastFailure = error.Message; Log.Error("Odyle could not start again", error); }
        Shutdown();
    }
    private void Release()
    {
        showWait?.Unregister(null); showWait = null; showSignal?.Dispose(); showSignal = null;
        quitWait?.Unregister(null); quitWait = null; quitSignal?.Dispose(); quitSignal = null; pairPipe?.Stop(); pairPipe = null; instance?.Dispose(); instance = null;
    }
    protected override void OnExit(ExitEventArgs e) { if (primary) Log.Info("Odyle stops."); Release(); base.OnExit(e); }
}
