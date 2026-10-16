using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Odyle.Reader;
// The super compact strip (#29, Odyle.Core.CompactStrip): MainWindow fills it from the card and
// switches between the two. It never takes the focus from the game, stays out of Alt+Tab and,
// with click-through on, lets the mouse through to the game below.
public partial class CompactWindow : Window
{
    // A double-click asks for the full card; a move, to remember the place.
    public event Action? Expand, Moved;
    private bool clickThrough;
    public CompactWindow() { InitializeComponent(); Translation.Apply(this); }
    public void Fill(string value, string cubes, Brush dot, string line, string tip)
    {
        ValueText.Text = value; CubesText.Text = cubes; Dot.Fill = dot; LineText.Text = line;
        LineText.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed; Strip.ToolTip = tip;
    }
    public bool ClickThrough { get => clickThrough; set { clickThrough = value; ApplyStyle(); } }
    private void Strip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Expand?.Invoke(); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    private void Strip_LocationChanged(object? sender, EventArgs e) => Moved?.Invoke();
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); ApplyStyle(); }
    // WS_EX_NOACTIVATE keeps the game focused, WS_EX_TOOLWINDOW keeps the strip out of Alt+Tab, and
    // WS_EX_TRANSPARENT on this layered window passes clicks through.
    private void ApplyStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        const int ExStyle = -20; const long Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
        var style = GetWindowLongPtr(handle, ExStyle).ToInt64() | ToolWindow | NoActivate;
        style = clickThrough ? style | Transparent : style & ~Transparent;
        SetWindowLongPtr(handle, ExStyle, new IntPtr(style));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
