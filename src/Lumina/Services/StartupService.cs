using System;
using System.IO;
using Microsoft.Win32;

namespace Lumina.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Lumina";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath is string exe)
                key.SetValue(ValueName, $"\"{exe}\" --background");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Updating start-with-Windows");
        }
    }

    /// <summary>
    /// Matches the setting to the registry, which the installer may have written (or the user removed in Task
    /// Manager). If the entry points at a Lumina.exe that no longer exists, for example after moving from the zip
    /// build to the installed one, it's pointed at this copy instead. Returns true when the setting changed.
    /// </summary>
    public static bool Sync(AppSettings settings)
    {
        bool enabled;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            string? command = key?.GetValue(ValueName) as string;
            enabled = command != null;
            if (enabled && TargetOf(command!) is { } target && !File.Exists(target)) Apply(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading start-with-Windows");
            return false;
        }
        if (enabled == settings.StartWithWindows) return false;
        settings.StartWithWindows = enabled;
        return true;
    }

    /// <summary>The exe in a Run command like "C:\path\Lumina.exe" --background.</summary>
    private static string? TargetOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
