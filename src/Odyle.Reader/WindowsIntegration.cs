using System;
using System.IO;
using System.Security;
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
