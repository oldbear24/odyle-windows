using System;
using System.Globalization;
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
    // the single-instance guard, whose message box would otherwise wait for a click.
    public static bool Headless { get; set; }
    private Mutex? instance;
    private DateTimeOffset failuresSince, lastNotice = DateTimeOffset.MinValue;
    private int failures;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!Headless)
        {
            // The language chosen in settings, else the Windows display language; numbers
            // and dates follow it (Localizer.Culture).
            Localizer.Use(PreferencesStore.Load().Language);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = Localizer.Culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Localizer.Culture.IetfLanguageTag)));
            DispatcherUnhandledException += OnUnhandled;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.IsTerminating ? "Unhandled exception, Odyle stops" : "Unhandled exception", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
            instance = new Mutex(true, "Local\\Odyle-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
            if (!created) { MessageBox.Show(Localizer.T("Odyle is already running. Open it from the system tray."), "Odyle"); Shutdown(); return; }
            Log.Info($"Odyle {Updater.Current} starts: {Updater.Runtime}, {(Updater.SelfContained ? "self-contained" : "small")} package, {Environment.OSVersion}, language {Localizer.Language} ({Localizer.Culture.Name}).");
            // A newer version downloaded and verified by an earlier run replaces this
            // build before any window opens; the new version then starts in its place.
            if (Updater.IsReleaseBuild && Updater.Staged() is { } staged && TryInstall(staged)) return;
            Updater.CleanUp();
        }
        base.OnStartup(e);
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
    private bool TryInstall(StagedUpdate staged)
    {
        Log.Info($"Installing the downloaded Odyle {staged.Version}.");
        try { Updater.Apply(staged); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { Updater.LastFailure = error.Message; Log.Error($"Odyle {staged.Version} could not be installed", error); return false; }
        Restart(); return true;
    }
    // Starts the executable in the application folder and ends this process. The
    // single-instance guard is released first so the new instance is not turned away.
    public void Restart()
    {
        instance?.Dispose(); instance = null;
        try { Updater.Relaunch(); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Updater.LastFailure = error.Message; Log.Error("Odyle could not start again", error); }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e) { if (!Headless) Log.Info("Odyle stops."); instance?.Dispose(); base.OnExit(e); }
}
