using System.Security.Principal;
using System.Threading;
using System.Windows;
namespace Odyle.Reader;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\Odyle-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) { MessageBox.Show("Odyle is already running. Open it from the system tray.", "Odyle"); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
