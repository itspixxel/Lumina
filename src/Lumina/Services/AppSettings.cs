using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumina.Services;

public enum ImageFormatKind { Png, Jpeg }

public sealed class AppSettings
{
    // Shortcuts
    public bool HookWinShiftS { get; set; } = true;
    public bool HookPrintScreen { get; set; }

    // Capture
    public bool ShowMagnifier { get; set; } = true;
    public bool CaptureCursor { get; set; }

    // After capture
    public bool CopyToClipboard { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public bool ShowQuickPreview { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; }
    public int PreviewSeconds { get; set; } = 6;

    // Output
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lumina");
    public string FileNamePattern { get; set; } = "Lumina_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;
    public int JpegQuality { get; set; } = 90;
    public int Counter { get; set; } = 1;

    // General
    public string Theme { get; set; } = "System";
    public bool StartWithWindows { get; set; }

    // Editor
    /// <summary>Custom colors picked in the editor, most recent first.</summary>
    public List<string> RecentColors { get; set; } = new();
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options);
                if (settings != null) return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading settings; using defaults");
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving settings");
        }
    }
}
