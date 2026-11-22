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
    private bool primary, windowBuilt;
    private DateTimeOffset failuresSince, lastNotice = DateTimeOffset.MinValue;
    private int failures;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Headless) return;
        // An Open in Odyle link (--pair) comes first and takes no other switch (StartupCommand.Accepted).
        var accepted = StartupCommand.Accepted(e.Args);
        if (accepted.Count != e.Args.Length) Log.Warn("A start with an Open in Odyle link carried other arguments; the start ignores them all.");
        // --build-info <file>: what this build is, for the publish script; nothing else starts.
        if (StartupCommand.BuildInfoPath(accepted) is { } infoPath)
        {
            try { System.IO.File.WriteAllText(infoPath, StartupCommand.BuildInfo(Updater.Current, Updater.Released, Updater.SelfContained, Updater.Runtime)); }
            finally { Shutdown(); }
            return;
        }
        // The language chosen in settings, else the Windows display language; numbers
        // and dates follow it (Localizer.Culture).
        var preferences = PreferencesStore.Load();
        Localizer.Use(preferences.Language);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = Localizer.Culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Localizer.Culture.IetfLanguageTag)));
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.IsTerminating ? "Unhandled exception, Odyle stops" : "Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        // --tray (Start with Windows) opens only the tray icon.
        var tray = StartupCommand.InTray(accepted); var user = WindowsIdentity.GetCurrent().User!.Value;
        // The uninstaller runs --quit before it removes the files, whatever the pairing and also when it
        // runs silently; it waits for --quit and --unpair to end, so they end whatever goes wrong, without a message box.
        if (StartupCommand.Quits(accepted))
        {
            try { Log.Info("Closing a running Odyle (--quit)."); QuitRunning(user); }
            catch (Exception error) { Log.Error("Closing a running Odyle failed (--quit)", error); }
            finally { Shutdown(); }
            return;
        }
        if (StartupCommand.Unpairs(accepted))
        {
            try { Unpair(user); }
            catch (Exception error) { Log.Error("Disconnecting this PC failed (--unpair)", error); }
            finally { Shutdown(); }
            return;
        }
        instance = new Mutex(true, "Local\\Odyle-" + user, out primary);
        // Already running: a start by hand brings that window forward; a start with Windows just ends; an
        // Open in Odyle link (--pair) goes to the running Odyle, which asks before pairing.
        var pairLink = StartupCommand.PairLinkOf(accepted);
        // A jump list task (--do) goes to the running Odyle the same way.
        var action = StartupCommand.ActionOf(accepted);
        var handed = pairLink ?? (action is null ? null : StartupCommand.PipeActionPrefix + action);
        if (!primary) { if (handed is not null) { AllowSetForegroundWindow(-1); if (!PairPipe.Send(user, handed)) ShowRunning(user); } else if (!tray) ShowRunning(user); Shutdown(); return; }
        Log.Info($"Odyle {Updater.Current} starts{(tray ? " in the tray" : "")}: {Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")} package, {Environment.OSVersion}, language {Localizer.Language} ({Localizer.Culture.Name}).");
        // A build an update just installed counts its starts until one proves healthy; after two failed
        // ones the build before it is put back and started instead (Updater, start health).
        if (Updater.IsReleaseBuild && Updater.BeginStart(Updater.Executable!, Updater.Current) == Updater.StartHealth.RolledBack) { Restart(tray); return; }
        // A newer version downloaded and verified by an earlier run replaces this
        // build before any window opens; the new version then starts in its place.
        if (Updater.IsReleaseBuild && Updater.Staged() is { } staged && TryInstall(staged, tray)) return;
        try { StartWindow(accepted, tray, user, pairLink, action, preferences); }
        // A build an update just installed that cannot build its window ends, so its next start can count
        // it, or, the second time, starts the build before it again. Any other start keeps running as before.
        catch (Exception error) when (Updater.StartPending)
        {
            Log.Error($"Odyle {Updater.Current} could not start", error);
            if (Updater.FailedStart(Updater.Executable!, Updater.Current)) Restart(tray);
            else { Release(); Shutdown(); }
        }
    }
    private void StartWindow(System.Collections.Generic.IReadOnlyList<string> accepted, bool tray, string user, string? pairLink, string? action, Preferences preferences)
    {
        Updater.CleanUp(); Autostart.Repair();
        if (Updater.IsReleaseBuild) PairProtocol.Register(Environment.ProcessPath);
        var window = new MainWindow(true, preferences); MainWindow = window;
        // --decoder-trace: the patch-day trace, for re-deriving the decoder after a game patch.
        if (StartupCommand.Traces(accepted)) window.StartTrace();
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Show-" + user);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Quit-" + user);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => window.Dispatcher.BeginInvoke(window.QuitOnRequest), null, Timeout.Infinite, executeOnlyOnce: true);
        pairPipe = new PairPipe(user, text => window.Dispatcher.BeginInvoke(() => { if (StartupCommand.PipeAction(text) is { } task) window.Do(task); else window.OfferPairing(text); }));
        UseJumpList();
        if (!tray || pairLink is not null) window.Show();
        _ = window.StartAsync(tray);
        if (pairLink is not null) window.Dispatcher.BeginInvoke(() => window.OfferPairing(pairLink), DispatcherPriority.ApplicationIdle);
        else if (action is not null) window.Dispatcher.BeginInvoke(() => window.Do(action), DispatcherPriority.ApplicationIdle);
        // Healthy once it ran 30 seconds with its window, or quit in order before that (OnExit).
        windowBuilt = true;
        if (Updater.StartPending)
        {
            var healthy = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            healthy.Tick += (_, _) => { healthy.Stop(); Updater.ConfirmStart(); };
            healthy.Start();
        }
    }
    // The taskbar button's right-click menu, also on a pinned button while Odyle is not running: each
    // task starts this executable with --do, which a running Odyle takes over (MainWindow.Do).
    private void UseJumpList()
    {
        if (Environment.ProcessPath is not { } exe) return;
        System.Windows.Shell.JumpTask Task(string action, string title) => new() { Title = Localizer.T(title), Arguments = $"{StartupCommand.ActionArgument} {action}", ApplicationPath = exe, IconResourcePath = exe, IconResourceIndex = 0 };
        var list = new System.Windows.Shell.JumpList([Task("compact", "Compact mode"), Task("tasks", "Show tasks"), Task("reader", "Start or stop the reader"), Task("dashboard", "Open the dashboard"), Task("settings", "Settings")], false, false);
        try { System.Windows.Shell.JumpList.SetJumpList(this, list); list.Apply(); }
        catch (Exception error) when (error is InvalidOperationException or COMException or UnauthorizedAccessException) { Log.Warn("The jump list could not be set: " + error.Message); }
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
        QuitRunning(user);
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
    // Asks a running Odyle of this user to quit (it marks its reading stale) and waits until it ended,
    // so the uninstaller can remove its files. An Odyle from this folder still running 10 seconds after
    // it let go of the single-instance guard (or after 15 seconds without an answer) is ended: an
    // uninstall must not leave its executable running and locked.
    private static void QuitRunning(string user)
    {
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
        foreach (var process in SameExecutable())
            using (process)
                try
                {
                    if (process.WaitForExit(TimeSpan.FromSeconds(10))) continue;
                    Log.Warn($"The running Odyle (process {process.Id}) did not end; it is ended now.");
                    process.Kill(); process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    Log.Warn($"The running Odyle (process {process.Id}) could not be ended: {error.Message}");
                }
    }
    // The other running processes of this very executable; an Odyle started from another folder is left alone.
    private static System.Collections.Generic.List<System.Diagnostics.Process> SameExecutable()
    {
        var found = new System.Collections.Generic.List<System.Diagnostics.Process>();
        if (Environment.ProcessPath is not { } self) return found;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(self)))
        {
            var keep = false;
            try { keep = process.Id != Environment.ProcessId && string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            if (keep) found.Add(process); else process.Dispose();
        }
        return found;
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
    protected override void OnExit(ExitEventArgs e) { if (primary) Log.Info("Odyle stops."); if (windowBuilt) Updater.ConfirmStart(); Release(); base.OnExit(e); }
}
