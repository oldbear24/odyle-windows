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
    // A second start signals the running one to bring its window forward, then ends.
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showWait;
    private bool primary;
    private DateTimeOffset failuresSince, lastNotice = DateTimeOffset.MinValue;
    private int failures;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Headless) return;
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
        instance = new Mutex(true, "Local\\Odyle-" + user, out primary);
        // Already running: a start by hand brings that window forward; a start with Windows just ends.
        if (!primary) { if (!tray) ShowRunning(user); Shutdown(); return; }
        Log.Info($"Odyle {Updater.Current} starts{(tray ? " in the tray" : "")}: {Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")} package, {Environment.OSVersion}, language {Localizer.Language} ({Localizer.Culture.Name}).");
        // A newer version downloaded and verified by an earlier run replaces this
        // build before any window opens; the new version then starts in its place.
        if (Updater.IsReleaseBuild && Updater.Staged() is { } staged && TryInstall(staged, tray)) return;
        Updater.CleanUp(); Autostart.Repair();
        var window = new MainWindow(); MainWindow = window;
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Odyle-Show-" + user);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);
        if (!tray) window.Show();
        _ = window.StartAsync(tray);
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
    private void Release() { showWait?.Unregister(null); showWait = null; showSignal?.Dispose(); showSignal = null; instance?.Dispose(); instance = null; }
    protected override void OnExit(ExitEventArgs e) { if (primary) Log.Info("Odyle stops."); Release(); base.OnExit(e); }
}
