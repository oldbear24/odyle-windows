using System.Security.Principal;
using System.Threading;
using System.Windows;
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
            instance = new Mutex(true, "Local\\Odyle-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
            if (!created) { MessageBox.Show("Odyle is already running. Open it from the system tray.", "Odyle"); Shutdown(); return; }
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
