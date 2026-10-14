using System;
using System.IO;
using Odyle.Core;

namespace Odyle.Reader;
// %LOCALAPPDATA%\Odyle\logs\odyle.log: start-up, connection and reader changes, errors,
// updates and decoder diagnostics, about a megabyte plus the previous file. Settings opens
// the folder. Account tokens, device keys and pairing codes are never written.
public static class Log
{
    private static RotatingLog file = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odyle", "logs", "odyle.log"));
    public static string FilePath => file.FilePath;
    public static string Folder => Path.GetDirectoryName(file.FilePath)!;
    // Checks point the log at a temporary folder so they never touch the user's.
    public static void UseFolder(string folder) => file = new(Path.Combine(folder, "odyle.log"));
    public static void Info(string message) => file.Write("INFO", message);
    public static void Warn(string message) => file.Write("WARN", message);
    public static void Error(string message, Exception? error = null) => file.Write("ERROR", error is null ? message : message + ": " + error);
}
