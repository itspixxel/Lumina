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
    /// <summary>Global capture shortcuts. Null until migrated from the old Win+Shift+S / Print Screen switches.</summary>
    public List<HotkeyBinding>? Hotkeys { get; set; }

    // Old shortcut switches, read once to migrate and then dropped from the file.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? HookWinShiftS { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? HookPrintScreen { get; set; }

    // Capture
    /// <summary>Hide Lumina's own windows while capturing so they don't end up in the shot.</summary>
    public bool HideWindowsDuringCapture { get; set; } = true;
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

    // Recording
    /// <summary>An ffmpeg.exe the user picked. Null uses the copy Lumina installed (or one on PATH).</summary>
    public string? FfmpegPath { get; set; }
    public string RecordingsDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Lumina");
    public int RecordingFps { get; set; } = 30;
    public bool RecordCursor { get; set; } = true;
    public int RecordingCountdown { get; set; } = 3;
    public bool RecordSystemAudio { get; set; } = true;
    /// <summary>Output device to record from (WASAPI endpoint id). Null follows the Windows default.</summary>
    public string? SystemAudioDevice { get; set; }
    public bool RecordMicrophone { get; set; }
    public string? MicrophoneDevice { get; set; }
    public bool HardwareEncoding { get; set; } = true;
    /// <summary>Put finished recordings and trim exports on the clipboard as files.</summary>
    public bool CopyRecordingToClipboard { get; set; } = true;

    // General
    /// <summary>The welcome / recording setup card has been shown once.</summary>
    public bool SetupShown { get; set; }
    public int SettingsVersion { get; set; }
    public string Theme { get; set; } = "System";
    public bool StartWithWindows { get; set; }

    // Editor
    /// <summary>Custom colors picked in the editor, most recent first.</summary>
    public List<string> RecentColors { get; set; } = new();

    internal AppSettings Migrate()
    {
        if (Hotkeys == null)
        {
            Hotkeys = new();
            if (HookWinShiftS != false) Hotkeys.Add(HotkeyBinding.WinShiftS);
            if (HookPrintScreen == true) Hotkeys.Add(HotkeyBinding.PrintScreen);
        }
        Hotkeys.RemoveAll(h => h.Key == 0);
        HookWinShiftS = HookPrintScreen = null;
        if (SettingsVersion < 2)
        {
            // Recording arrived: give it Snipping Tool's shortcut unless that's already taken.
            var record = HotkeyBinding.WinShiftR;
            if (!Hotkeys.Exists(h => h.SameKeys(record))) Hotkeys.Add(record);
            SettingsVersion = 2;
        }
        return this;
    }
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
                if (settings != null) return settings.Migrate();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading settings; using defaults");
        }
        return new AppSettings().Migrate();
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
