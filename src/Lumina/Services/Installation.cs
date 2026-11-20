using System;
using System.IO;
using Microsoft.Win32;

namespace Lumina.Services;

/// <summary>
/// Housekeeping for copies installed with Lumina's setup (installer\Lumina.iss). The updater swaps Lumina.exe in
/// place, so the Apps &amp; features entry the installer wrote would otherwise keep showing the version it installed.
/// </summary>
public static class Installation
{
    /// <summary>Inno Setup's uninstall key for Lumina's AppId. Must match the AppId in installer\Lumina.iss.</summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{54F4DC59-5994-4EBD-84E9-70F32C0109CF}_is1";

    /// <summary>True when this copy of Lumina is the one the installer put in place.</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>Notes whether this copy was installed, and if so brings its Apps &amp; features version up to date.</summary>
    public static void Sync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return;
            IsInstalled = SameFolder(location, AppContext.BaseDirectory);
            if (!IsInstalled) return;

            string version = Updater.Current.ToString(3);
            if (key.GetValue("DisplayVersion") as string != version)
            {
                key.SetValue("DisplayVersion", version);
                Log.Info($"Updated the Apps & features entry to {version}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Syncing the install entry");
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
