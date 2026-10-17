using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Odyle.Core;

namespace Odyle.Reader;
// The per-user Run key. Checks swap it for one in memory, so they never touch the real one.
public interface IRunKey { string? Get(string name); void Set(string name, string value); void Delete(string name); }
public sealed class RegistryRunKey : IRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Get(string name) { using var key = Registry.CurrentUser.OpenSubKey(Path); return key?.GetValue(name) as string; }
    public void Set(string name, string value) { using var key = Registry.CurrentUser.CreateSubKey(Path); key.SetValue(name, value, RegistryValueKind.String); }
    public void Delete(string name) { using var key = Registry.CurrentUser.OpenSubKey(Path, writable: true); key?.DeleteValue(name, throwOnMissingValue: false); }
}
// Settings → Start with Windows: "<Odyle.exe>" --tray in the Run key, the value the installer writes too.
public static class Autostart
{
    public static IRunKey Key { get; set; } = new RegistryRunKey();
    public static string? Executable { get; set; } = Environment.ProcessPath;
    public static bool Enabled { get { try { return Key.Get(StartupCommand.ValueName) is { Length: > 0 }; } catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { return false; } } }
    public static void Set(bool on)
    {
        if (!on) { Key.Delete(StartupCommand.ValueName); return; }
        Key.Set(StartupCommand.ValueName, StartupCommand.For(Executable ?? throw new InvalidOperationException(Localizer.T("The application path is unknown."))));
    }
    // An entry left pointing at an executable that is gone (the folder moved) starts this one instead.
    public static void Repair()
    {
        try
        {
            if (Key.Get(StartupCommand.ValueName) is not { Length: > 0 } command || Executable is not { } exe || StartupCommand.PathOf(command) is { } path && File.Exists(path)) return;
            Key.Set(StartupCommand.ValueName, StartupCommand.For(exe)); Log.Info("The Start with Windows entry now points at " + exe + ".");
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { Log.Warn("The Start with Windows entry could not be checked: " + error.Message); }
    }
}
// odyle:// links (Open in Odyle in the dashboard's pairing dialog) start "<Odyle.exe>" --pair "<link>" for this
// Windows account (HKCU, no administrator rights). A release build keeps the entry pointing at itself; the
// installer writes the same entry and removes it on uninstall.
public static class PairProtocol
{
    private const string Path = @"Software\Classes\odyle";
    public static string CommandFor(string executable) => $"\"{executable}\" {StartupCommand.PairArgument} \"%1\"";
    public static void Register(string? executable)
    {
        if (executable is null) return;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Path)) { key.SetValue("", "URL:Odyle pairing"); key.SetValue("URL Protocol", ""); }
            using var open = Registry.CurrentUser.CreateSubKey(Path + @"\shell\open\command");
            if (open.GetValue("") as string == CommandFor(executable)) return;
            open.SetValue("", CommandFor(executable)); Log.Info("Open in Odyle links now start " + executable + ".");
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { Log.Warn("Open in Odyle links could not be registered: " + error.Message); }
    }
}
// A second start with --pair hands its link to the running Odyle through a pipe that only this
// Windows account can open (PipeOptions.CurrentUserOnly), then ends.
public sealed class PairPipe
{
    private readonly CancellationTokenSource stop = new();
    public static string NameFor(string user) => "Odyle-Pair-" + user;
    public PairPipe(string user, Action<string> received) => _ = Task.Run(() => ListenAsync(NameFor(user), received, stop.Token));
    public void Stop() => stop.Cancel();
    private static async Task ListenAsync(string name, Action<string> received, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var buffer = new char[2049]; var read = await reader.ReadBlockAsync(buffer.AsMemory(), token);
                if (read is > 0 and <= 2048) received(new string(buffer, 0, read).Trim());
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Warn("The Open in Odyle pipe failed: " + error.Message); try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; } }
        }
    }
    // From a second start: true once the running Odyle took the link.
    public static bool Send(string user, string link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", NameFor(user), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000); var bytes = Encoding.UTF8.GetBytes(link); client.Write(bytes, 0, bytes.Length); client.Flush(); return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException) { Log.Warn("The running Odyle did not take the link: " + error.Message); return false; }
    }
}
// Npcap is installed separately: its library in System32\Npcap, or its registry key. Odyle may
// not bundle it; the installer and the reader row link to its download page.
public static class Npcap
{
    public const string DownloadPage = "https://npcap.com/#download";
    public static Func<bool> Probe { get; set; } = Installed;
    // Installed with "Restrict Npcap driver's access to Administrators only" (its AdminOnly option):
    // an Odyle running without administrator rights then finds no capture interface.
    public static Func<bool> Restricted { get; set; } = AdminOnly;
    public static bool AdminOnly()
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap\Parameters"); return key?.GetValue("AdminOnly") is int value && value != 0; }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { return false; }
    }
    // The installed version from Npcap's uninstall entry, for Copy diagnostics.
    public static string? Version()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try { using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view); using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst"); if (key?.GetValue("DisplayVersion") is string { Length: > 0 } version) return version; }
            catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { }
        }
        return null;
    }
    public static bool Installed()
    {
        try { if (File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"))) return true; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try { using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view); using var key = root.OpenSubKey(@"SOFTWARE\Npcap"); if (key is not null) return true; }
            catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { }
        }
        return false;
    }
}
// The game build Steam installed (SteamManifest), for the log, the heartbeat and the patch-day
// trace: Steam's path from the per-user registry, its library list and AION 2's app manifest.
// Only Steam's own files are read; the game's process is never opened. Null for a game
// installed outside Steam, or when Steam's files cannot be read.
public static class GameClient
{
    public static string? Version()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string { Length: > 0 } steam) return null;
            var steamapps = Path.Combine(steam.Replace('/', Path.DirectorySeparatorChar), "steamapps");
            var libraries = new List<string> { steamapps };
            var list = Path.Combine(steamapps, "libraryfolders.vdf");
            if (File.Exists(list)) libraries.AddRange(SteamManifest.Libraries(File.ReadAllText(list)).Select(library => Path.Combine(library, "steamapps")));
            foreach (var library in libraries)
            {
                var manifest = Path.Combine(library, SteamManifest.ManifestName);
                if (File.Exists(manifest) && SteamManifest.BuildId(File.ReadAllText(manifest)) is { } build) return SteamManifest.Label(build);
            }
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
