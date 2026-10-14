using Odyle.Core;

// Start with Windows, the start in the tray and where the window reopens.
static class DesktopChecks
{
    public static void Run()
    {
        const string exe = @"C:\Users\Player\AppData\Local\Programs\Odyle\Odyle.exe";
        Check(StartupCommand.For(exe) == "\"" + exe + "\" --tray" && StartupCommand.ValueName == "Odyle", "the Run entry starts this executable in the tray");
        Check(StartupCommand.PathOf(StartupCommand.For(exe)) == exe && StartupCommand.PathOf(@"C:\Odyle\Odyle.exe --tray") == @"C:\Odyle\Odyle.exe" && StartupCommand.PathOf("\"broken") is null && StartupCommand.PathOf(" ") is null, "the executable of a Run entry");
        Check(StartupCommand.InTray(["--TRAY"]) && !StartupCommand.InTray([]) && !StartupCommand.InTray(["--update-probe"]), "the tray argument");
        Check(StartupCommand.ShowsWindow(tray: false, paired: true) && StartupCommand.ShowsWindow(tray: true, paired: false) && !StartupCommand.ShowsWindow(tray: true, paired: true), "a paired start with Windows stays in the tray");
        // Two screens side by side; the right one is shorter.
        ScreenArea[] screens = [new(0, 0, 1920, 1040), new(1920, 0, 3200, 760)];
        Check(Placement.OnScreen(100, 100, screens) && Placement.OnScreen(2000, 700, screens) && Placement.OnScreen(-30, 0, screens), "a position whose caption is on a screen is restored");
        Check(!Placement.OnScreen(2000, 900, screens) && !Placement.OnScreen(5000, 100, screens) && !Placement.OnScreen(-2000, 100, screens) && !Placement.OnScreen(100, -100, screens), "a position on a disconnected screen is not");
        Check(Placement.Clamp(1800, 900, 280, 300, screens[0]) == (1640, 740) && Placement.Clamp(100, 100, 280, 300, screens[0]) == (100, 100) && Placement.Clamp(-30, -5, 280, 300, screens[0]) == (0, 0) && Placement.Clamp(10, 10, 2500, 1200, screens[0]) == (0, 0), "a window is kept on its screen");
        Console.WriteLine("Start with Windows command, tray start, saved window position and keep-on-screen passed.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Desktop check failed: " + message); }
}
