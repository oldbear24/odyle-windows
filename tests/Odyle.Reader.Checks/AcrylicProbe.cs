using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Odyle.Reader;

// The acrylic background (Settings → Window) as the screen shows it: Windows draws the blur, so only a capture of
// the desktop sees it. The card stands over a stand-in for the game in three states: active, behind the game's focus
// (the blur has to stay), and faded while the game is in front; then once more with acrylic off for comparison.
static partial class ReaderChecks
{
    static async Task AcrylicProbe(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory); App.Headless = false;
        static void Settle() { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); Thread.Sleep(400); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); }
        var scene = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x4A, 0x7A), System.Windows.Media.Color.FromRgb(0xC8, 0x7A, 0x3A), 35);
        var game = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = 200, Top = 120, Width = 560, Height = 560, Background = scene,
            Content = new TextBlock { Text = "AION 2", FontSize = 96, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        game.Show();
        // A connected card with a character and a party, as in the screenshots.
        window.Link.Api = new Odyle.Core.PartyApi("http://127.0.0.1:8090"); Field("accountName").SetValue(window, "Sample account");
        var stamp = DateTime.UtcNow.AddSeconds(-40).ToString("yyyy-MM-dd HH:mm:ss.fffZ");
        Field("characters").SetValue(window, new[] { new Odyle.Core.Character("c1", "u", "TestHero", "Lumiel", "GLOBAL_EU", 390, true, true, stamp) });
        Field("rooms").SetValue(window, new[] { new Odyle.Core.Room("r1", "Evening", 40, "owner", true, ["c1"]) });
        Field("currentCharacterId").SetValue(window, "c1"); Field("accountId").SetValue(window, "u"); Field("snapshots").SetValue(window, new[] { PartySnapshot(inGame: true) });
        Call(window, "ShowConnected", true); Call(window, "RenderHero"); Call(window, "RenderParty");
        Call(window, "SetAcrylic", true, 60, false);
        window.Show(); window.Left = 330; window.Top = 150; window.Topmost = true; window.Activate(); Settle();
        var handle = new WindowInteropHelper(game).Handle;
        Capture(handle, Path.Combine(directory, "acrylic-active.png"));
        game.Activate(); Settle();
        Capture(handle, Path.Combine(directory, "acrylic-game-in-front.png"));
        Field("gameInFront").SetValue(window, (Func<bool>)(() => true)); Call(window, "SetFadeInGame", true, false); Settle();
        Capture(handle, Path.Combine(directory, "acrylic-faded.png"));
        Call(window, "SetFadeInGame", false, false);
        Call(window, "SetAcrylic", true, 25, false); Settle();
        Capture(handle, Path.Combine(directory, "acrylic-25.png"));
        Call(window, "SetAcrylic", false, 60, false); Settle();
        Capture(handle, Path.Combine(directory, "acrylic-off.png"));
        // Compact mode's floating chips (variant C): only the chips cover the game, with acrylic on or off.
        Call(window, "SetAcrylic", true, 60, false); Call(window, "SetCompact", true, false);
        game.Topmost = true; window.Topmost = false; window.Topmost = true; Settle();
        Capture(handle, Path.Combine(directory, "compact-floating.png"));
        // The same over a dark scene, as over a dark app on the desktop.
        game.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x19, 0x19, 0x19)); Settle();
        Capture(handle, Path.Combine(directory, "compact-floating-dark.png"));
        // Alone: nobody else online in your rooms leaves your pill by itself.
        Field("snapshots").SetValue(window, Array.Empty<Odyle.Core.RoomSnapshot>()); Call(window, "RenderParty"); Settle();
        Capture(handle, Path.Combine(directory, "compact-floating-alone.png"));
        game.Background = scene; game.Topmost = false;
        Call(window, "SetCompact", false, false); Call(window, "SetAcrylic", false, 60, false); Settle();
        // Settings → Window with the toggle on and its opacity slider.
        Call(window, "SetAcrylic", true, 60, false); Call(window, "OpenSettings");
        var settings = Settings(window); settings.Left = 900; settings.Top = 120; Settle();
        var toggle = (FrameworkElement)settings.FindName("AcrylicToggle"); var scroller = (ScrollViewer)settings.FindName("Scroller");
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + toggle.TranslatePoint(new Point(0, 0), scroller).Y - 160); Settle();
        Capture(new WindowInteropHelper(settings).Handle, Path.Combine(directory, "acrylic-settings.png"));
        settings.Hide(); Call(window, "SetAcrylic", false, 60, false);
        window.Topmost = false; window.Hide(); game.Close();
        Console.WriteLine("Acrylic probe captured to " + directory);
        await Task.CompletedTask;
    }
    // The screen around a window (System.Drawing through reflection, as FadePreview: the checks reference WPF only).
    static void Capture(IntPtr around, string path)
    {
        GetWindowRect(around, out var rect);
        var drawing = Type.GetType("System.Drawing.Bitmap, System.Drawing.Common")!; var graphics = Type.GetType("System.Drawing.Graphics, System.Drawing.Common")!;
        var size = Activator.CreateInstance(Type.GetType("System.Drawing.Size, System.Drawing.Primitives")!, rect.Right - rect.Left, rect.Bottom - rect.Top)!;
        var bitmap = Activator.CreateInstance(drawing, rect.Right - rect.Left, rect.Bottom - rect.Top)!;
        var g = graphics.GetMethod("FromImage")!.Invoke(null, [bitmap])!;
        graphics.GetMethod("CopyFromScreen", [typeof(int), typeof(int), typeof(int), typeof(int), size.GetType()])!.Invoke(g, [rect.Left, rect.Top, 0, 0, size]);
        drawing.GetMethod("Save", [typeof(string)])!.Invoke(bitmap, [path]);
        ((IDisposable)g).Dispose(); ((IDisposable)bitmap).Dispose();
    }
}
