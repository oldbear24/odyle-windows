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
    // Windows 11: rounded corners for the frameless card (it draws its own header).
    internal static void RoundCorners(Window window)
    {
        try { var handle = new WindowInteropHelper(window).Handle; int round = 2; _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }
    // Compact mode's floating chips (variant C): the window itself has no frame, border, corners or shadow of its own,
    // only its chips show over the game; back to the card's rounded corners and dark border otherwise.
    internal static void Floating(Window window, bool floating)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int corners = floating ? 1 : 2, border = floating ? unchecked((int)0xFFFFFFFE) : 0x00293024;
            _ = DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int)); _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }
    // The see-through background (Settings → Window, as Windows Terminal's acrylic): Windows blurs what is
    // behind the window, and the window's own background is drawn over it at the chosen opacity. Through
    // SetWindowCompositionAttribute's accent, which, unlike the DWM system backdrops, stays while another
    // window (the game) has the focus. Acrylic lags behind the pointer on some Windows builds while the
    // window is dragged, so a plain blur stands in while it moves (Hook). Windows 10 1803 (build 17134) or later.
    // Clear: no blur and no colour behind the window at all, so what the window leaves transparent shows the game
    // as it is (compact mode's floating chips).
    internal sealed class Backdrop
    {
        private const int WcaAccentPolicy = 19, AccentDisabled = 0, AccentClear = 2, AccentBlurBehind = 3, AccentAcrylic = 4;
        private bool on, clear, moving;
        internal static bool Supported => Environment.OSVersion.Version is { Major: >= 10, Build: >= 17134 };
        internal void Use(IntPtr window, bool acrylic, bool transparent = false)
        {
            clear = transparent && Supported; on = acrylic && Supported && !clear;
            Apply(window);
        }
        private void Apply(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            // The tint stays nearly clear: the window's background brush carries the colour and the opacity.
            // Clear takes its colour from GradientColor only with flag 2; without it Windows fills in the system accent colour.
            var policy = new AccentPolicy { AccentState = clear ? AccentClear : !on ? AccentDisabled : moving ? AccentBlurBehind : AccentAcrylic, AccentFlags = clear ? 2 : 0, GradientColor = clear ? 0u : 0x01130F0B };
            var size = Marshal.SizeOf<AccentPolicy>(); var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new CompositionData { Attribute = WcaAccentPolicy, Data = memory, Size = size };
                _ = SetWindowCompositionAttribute(window, ref data);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { on = false; }
            finally { Marshal.FreeHGlobal(memory); }
        }
        // WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE: the plain blur while the window is dragged.
        internal IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (on && message is 0x0231 or 0x0232) { moving = message == 0x0231; Apply(window); }
            return IntPtr.Zero;
        }
        [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);
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
