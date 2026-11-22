using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Odyle.Core;

namespace Odyle.Reader;
// The Win32 calls the window makes (W#90: moved out of MainWindow): the dark frame, the window in front, the
// layered style that fades the card, its client size, and the compact mode shortcut.
internal static class NativeWindows
{
    // Windows 11: dark title bar and a border that matches the window.
    internal static void DarkFrame(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            int dark = 1, caption = 0x0013160F, border = 0x00293024;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch { }
    }
    // The process whose window is in front; false when no window is.
    internal static bool ForegroundProcess(out uint processId)
    {
        processId = 0;
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(front, out processId);
        return true;
    }
    // The window's client area in device pixels.
    internal static bool ClientSize(IntPtr window, out int width, out int height)
    {
        width = height = 0;
        if (!GetClientRect(window, out var client)) return false;
        width = client.Right - client.Left; height = client.Bottom - client.Top;
        return true;
    }
    // Has the whole window drawn again.
    internal static void Repaint(IntPtr window) => InvalidateRect(window, IntPtr.Zero, false);
    internal static void ReleaseIcon(IntPtr handle) => DestroyIcon(handle);

    internal const int GwlExStyle = -20, WsExLayered = 0x80000; private const uint LwaAlpha = 0x2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);

    // Fading while the game is in front (W#18): a layered window, so the game shows through. One per window: it lets
    // through the style change it makes itself (Hook).
    internal sealed class Layering
    {
        private bool setting;
        internal static bool IsLayered(IntPtr window) => window != IntPtr.Zero && (GetWindowLong(window, GwlExStyle) & WsExLayered) != 0;
        // The window's opacity: layered while fading is on (255 is fully opaque), plain otherwise.
        internal void SetAlpha(IntPtr window, byte alpha, bool layered)
        {
            if (window == IntPtr.Zero) return;
            var style = GetWindowLong(window, GwlExStyle);
            setting = true;
            try
            {
                if (!layered) { if ((style & WsExLayered) != 0) _ = SetWindowLong(window, GwlExStyle, style & ~WsExLayered); return; }
                if ((style & WsExLayered) == 0) _ = SetWindowLong(window, GwlExStyle, style | WsExLayered);
            }
            finally { setting = false; }
            SetLayeredWindowAttributes(window, 0, alpha, LwaAlpha);
        }
        // WPF drops WS_EX_LAYERED from a window without AllowsTransparency while the style changes
        // (WM_STYLECHANGING). The change the fading makes itself is let through as it is. Added to the
        // window's HwndSource when its handle exists (MainWindow.OnSourceInitialized).
        internal IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x7C && setting && wParam.ToInt64() == GwlExStyle) handled = true;
            return IntPtr.Zero;
        }
    }

    // The compact mode shortcut, registered for the whole session (RegisterHotKey) on a message-only window.
    internal sealed class Shortcut(Action pressed) : IDisposable
    {
        private const int Id = 0x4F44;
        private HwndSource? window;
        // Registers hotkey in place of the one before ("" or an unknown one: none). False, with the Win32 error,
        // when another app holds it already.
        internal bool Use(string hotkey, out int error)
        {
            error = 0;
            if (window is null)
            {
                window = new HwndSource(new HwndSourceParameters("Odyle shortcut") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                window.AddHook((IntPtr _, int message, IntPtr id, IntPtr _, ref bool handled) =>
                {
                    if (message == 0x0312 && id.ToInt32() == Id) { handled = true; pressed(); }
                    return IntPtr.Zero;
                });
            }
            UnregisterHotKey(window.Handle, Id);
            if (CompactStrip.Parse(hotkey) is not { } keys || RegisterHotKey(window.Handle, Id, keys.Modifiers, keys.Key)) return true;
            error = Marshal.GetLastWin32Error();
            return false;
        }
        public void Dispose()
        {
            if (window is null) return;
            UnregisterHotKey(window.Handle, Id); window.Dispose(); window = null;
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
