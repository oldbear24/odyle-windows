using System;
using System.Globalization;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using Odyle.Core;
namespace Odyle.Reader;
public partial class App : System.Windows.Application
{
    // The UI checks build the application beside a possibly running Odyle; they skip
    // the single-instance guard, whose message box would otherwise wait for a click.
    public static bool Headless { get; set; }
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!Headless)
        {
            // The language chosen in settings, else the Windows display language; numbers
            // and dates follow it (Localizer.Culture).
            Localizer.Use(PreferencesStore.Load().Language);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = Localizer.Culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Localizer.Culture.IetfLanguageTag)));
            instance = new Mutex(true, "Local\\Odyle-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
            if (!created) { MessageBox.Show(Localizer.T("Odyle is already running. Open it from the system tray."), "Odyle"); Shutdown(); return; }
            // A newer version downloaded and verified by an earlier run replaces this
            // build before any window opens; the new version then starts in its place.
            if (Updater.IsReleaseBuild && Updater.Staged() is { } staged && TryInstall(staged)) return;
            Updater.CleanUp();
        }
        base.OnStartup(e);
    }
    private bool TryInstall(StagedUpdate staged)
    {
        try { Updater.Apply(staged); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { Updater.LastFailure = error.Message; return false; }
        Restart(); return true;
    }
    // Starts the executable in the application folder and ends this process. The
    // single-instance guard is released first so the new instance is not turned away.
    public void Restart()
    {
        instance?.Dispose(); instance = null;
        try { Updater.Relaunch(); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Updater.LastFailure = error.Message; }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
