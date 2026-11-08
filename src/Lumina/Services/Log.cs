using System;
using System.IO;

namespace Lumina.Services;

public static class Log
{
    private static readonly object Gate = new();

    public static void Error(Exception ex, string context) => Write($"ERROR {context}: {ex}");

    public static void Info(string message) => Write($"INFO  {message}");

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000) file.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
