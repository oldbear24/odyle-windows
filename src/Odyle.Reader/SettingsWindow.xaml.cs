using System.ComponentModel;
using System.Windows;

namespace Odyle.Reader;
// Account, room sharing and reader setup. MainWindow owns the state and wires the
// controls; closing only hides the window until the app quits.
public partial class SettingsWindow : Window
{
    internal bool Exiting;
    public SettingsWindow() => InitializeComponent();
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!Exiting) { e.Cancel = true; Hide(); } }
}
