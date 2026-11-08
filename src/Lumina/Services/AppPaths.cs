using System;
using System.IO;

namespace Lumina.Services;

/// <summary>
/// Where Lumina keeps its own data. Dropping an empty file named "portable" next to Lumina.exe
/// keeps everything in a "data" folder beside the exe instead of %AppData%.
/// </summary>
public static class AppPaths
{
    public static bool IsPortable { get; private set; }
    public static string DataDir { get; } = ResolveDataDir();
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "lumina.log");
    public static string ProjectsDir => Ensure(Path.Combine(DataDir, "projects"));
    public static string TempDir => Ensure(Path.Combine(Path.GetTempPath(), "Lumina"));

    private static string ResolveDataDir()
    {
        string exeDir = AppContext.BaseDirectory;
        string dir;
        if (File.Exists(Path.Combine(exeDir, "portable")))
        {
            IsPortable = true;
            dir = Path.Combine(exeDir, "data");
        }
        else
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumina");
        }
        return Ensure(dir);
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
