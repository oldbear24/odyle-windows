using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Odyle.Reader;
// Account, room sharing and reader setup. MainWindow owns the state and wires the
// controls; closing only hides the window until the app quits. Frameless like the card:
// dragged by its header, closed with its ✕.
public partial class SettingsWindow : Window
{
    internal bool Exiting;
    public SettingsWindow() { InitializeComponent(); Translation.Apply(this); }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11 draws a border around the frameless window; Windows 10 does not, so it draws its own.
        if (Environment.OSVersion.Version.Build < 22000) RootBorder.BorderThickness = new Thickness(1);
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!Exiting) { e.Cancel = true; Hide(); } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
