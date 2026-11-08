using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Lumina.Recording;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

/// <summary>Settings apply as you change them and are written to disk a moment later (and on close).</summary>
public partial class SettingsWindow : ChromeWindow
{
    private readonly AppSettings _s = App.Settings;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading = true;
    private bool _dirty;

    public SettingsWindow()
    {
        InitializeComponent();

        ShowMagnifier.IsChecked = _s.ShowMagnifier;
        CaptureCursor.IsChecked = _s.CaptureCursor;
        HideWindows.IsChecked = _s.HideWindowsDuringCapture;
        CopyToClipboard.IsChecked = _s.CopyToClipboard;
        AutoSave.IsChecked = _s.AutoSave;
        ShowQuickPreview.IsChecked = _s.ShowQuickPreview;
        OpenEditor.IsChecked = _s.OpenEditorAfterCapture;
        PreviewSeconds.Value = _s.PreviewSeconds;
        OutputDirectory.Text = _s.OutputDirectory;
        FileNamePattern.Text = _s.FileNamePattern;
        ImageFormat.SelectedIndex = _s.ImageFormat == ImageFormatKind.Jpeg ? 1 : 0;
        JpegQuality.Value = _s.JpegQuality;
        StartWithWindows.IsChecked = _s.StartWithWindows;
        LoadRecording();

        PatternHelp.Text = "Tokens: " + FileNameFormatter.TokenHelp;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Lumina {version?.ToString(3)}" + (AppPaths.IsPortable ? " (portable)" : "");
        DataFolderText.Text = AppPaths.DataDir;

        UpdatePatternPreview();
        UpdateQualityPanel();
        PreviewSecondsText.Text = $"{_s.PreviewSeconds} s";
        QualityText.Text = $"{_s.JpegQuality}";
        _loading = false;

        // Any edit anywhere in the window counts as a change.
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(Changed));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(Changed));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(Changed));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Changed));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(Changed));
        _saveTimer.Tick += (_, _) => Commit();
        // The record overlay can switch audio devices while this window is open; pick that up when coming back.
        Activated += (_, _) => ReloadAudio();
    }

    private void Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        Commit();
        App.ApplyShortcuts();
        base.OnClosed(e);
    }

    /// <summary>Copies every control into the settings and writes them to disk.</summary>
    private void Commit()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;

        _s.ShowMagnifier = ShowMagnifier.IsChecked == true;
        _s.CaptureCursor = CaptureCursor.IsChecked == true;
        _s.HideWindowsDuringCapture = HideWindows.IsChecked == true;
        _s.CopyToClipboard = CopyToClipboard.IsChecked == true;
        _s.AutoSave = AutoSave.IsChecked == true;
        _s.ShowQuickPreview = ShowQuickPreview.IsChecked == true;
        _s.OpenEditorAfterCapture = OpenEditor.IsChecked == true;
        _s.PreviewSeconds = (int)PreviewSeconds.Value;

        string before = _s.OutputDirectory;
        string dir = OutputDirectory.Text.Trim();
        if (dir.Length > 0)
        {
            try { _s.OutputDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)); }
            catch (Exception ex) { Log.Error(ex, "Invalid output folder"); }
        }
        if (!string.IsNullOrWhiteSpace(FileNamePattern.Text)) _s.FileNamePattern = FileNamePattern.Text.Trim();
        _s.ImageFormat = ImageFormat.SelectedIndex == 1 ? ImageFormatKind.Jpeg : ImageFormatKind.Png;
        _s.JpegQuality = (int)JpegQuality.Value;

        SaveRecording();

        bool startup = StartWithWindows.IsChecked == true;
        if (startup != _s.StartWithWindows) StartupService.Apply(startup);
        _s.StartWithWindows = startup;

        SettingsService.Save(_s);
        if (_s.OutputDirectory != before) App.Instance.Controller.NotifySaved(); // refresh the home list
    }

    // ---------- Recording ----------

    private void LoadRecording()
    {
        Select(RecordingFps, _s.RecordingFps.ToString());
        Select(RecordingCountdown, _s.RecordingCountdown.ToString());
        RecordCursor.IsChecked = _s.RecordCursor;
        HardwareEncoding.IsChecked = _s.HardwareEncoding;
        RecordingsDirectory.Text = _s.RecordingsDirectory;
        FillAudio();
        SyncFfmpeg();
        Ffmpeg.StateChanged += SyncFfmpeg;
        Closed += (_, _) => Ffmpeg.StateChanged -= SyncFfmpeg;
    }

    private void FillAudio()
    {
        RecordSystemAudio.IsChecked = _s.RecordSystemAudio;
        RecordMicrophone.IsChecked = _s.RecordMicrophone;
        FillDevices(SystemDevice, AudioFlow.Output, _s.SystemAudioDevice);
        FillDevices(MicDevice, AudioFlow.Input, _s.MicrophoneDevice);
        SyncAudioRows();
    }

    /// <summary>Refreshes the device lists (something may have been plugged in) and any choice made elsewhere.</summary>
    private void ReloadAudio()
    {
        if (SystemDevice.IsDropDownOpen || MicDevice.IsDropDownOpen) return;
        Commit(); // keep anything still waiting to be saved
        _loading = true;
        FillAudio();
        _loading = false;
    }

    private void SaveRecording()
    {
        if (RecordingFps.SelectedItem is ComboBoxItem { Tag: string fps }) _s.RecordingFps = int.Parse(fps);
        if (RecordingCountdown.SelectedItem is ComboBoxItem { Tag: string countdown }) _s.RecordingCountdown = int.Parse(countdown);
        _s.RecordSystemAudio = RecordSystemAudio.IsChecked == true;
        _s.RecordMicrophone = RecordMicrophone.IsChecked == true;
        _s.SystemAudioDevice = (SystemDevice.SelectedItem as ComboBoxItem)?.Tag as string;
        _s.MicrophoneDevice = (MicDevice.SelectedItem as ComboBoxItem)?.Tag as string;
        _s.RecordCursor = RecordCursor.IsChecked == true;
        _s.HardwareEncoding = HardwareEncoding.IsChecked == true;
        string dir = RecordingsDirectory.Text.Trim();
        if (dir.Length > 0)
        {
            try { _s.RecordingsDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)); }
            catch (Exception ex) { Log.Error(ex, "Invalid recordings folder"); }
        }
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem { Tag: string t } c && t == tag) box.SelectedItem = c;
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    /// <summary>"Default" (follows Windows) first, then each active device. A saved device that's unplugged stays listed.</summary>
    internal static void FillDevices(ComboBox box, AudioFlow flow, string? selected)
    {
        box.Items.Clear();
        string label = AudioDevices.DefaultLabel(flow);
        box.Items.Add(new ComboBoxItem { Content = label, Tag = null, ToolTip = label });
        foreach (var device in AudioDevices.List(flow))
            box.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id, ToolTip = device.Name });
        box.SelectedIndex = 0;
        if (selected == null) return;
        foreach (ComboBoxItem item in box.Items)
            if (item.Tag as string == selected) box.SelectedItem = item;
        if (box.SelectedIndex == 0)
        {
            var missing = new ComboBoxItem { Content = "Unplugged device", Tag = selected, ToolTip = "Not connected right now; the default is used instead" };
            box.Items.Add(missing);
            box.SelectedItem = missing;
        }
    }

    private void SyncAudioRows()
    {
        SystemDevice.IsEnabled = RecordSystemAudio.IsChecked == true;
        MicDevice.IsEnabled = RecordMicrophone.IsChecked == true;
    }

    private void AudioCheck_Click(object sender, RoutedEventArgs e) => SyncAudioRows();

    private void SyncFfmpeg()
    {
        bool installing = Ffmpeg.IsInstalling;
        bool available = Ffmpeg.IsAvailable;
        FfmpegStatus.Text = installing ? Ffmpeg.InstallStage : available ? Ffmpeg.Describe() : "Needed for recording · free, " + Ffmpeg.DownloadSizeText;
        FfmpegStatus.ToolTip = FfmpegStatus.Text;
        FfmpegStatus.Foreground = (System.Windows.Media.Brush)FindResource(available || installing ? "Text2Brush" : "DangerBrush");
        FfmpegDownload.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        FfmpegDownload.Content = installing ? "Cancel" : "Download";
        FfmpegDownload.Style = (Style)FindResource(installing ? typeof(Button) : "AccentButton");
        FfmpegBrowse.IsEnabled = !installing;
        FfmpegReset.Visibility = _s.FfmpegPath != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FfmpegDownload_Click(object sender, RoutedEventArgs e)
    {
        if (Ffmpeg.IsInstalling) Ffmpeg.CancelInstall();
        else _ = Ffmpeg.InstallAsync();
    }

    private async void FfmpegBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        if (!await Ffmpeg.UseCustomAsync(dialog.FileName))
            LuminaDialog.Alert(this, "That doesn't look like FFmpeg", "Lumina couldn't run it as ffmpeg.exe. Pick the ffmpeg.exe from an FFmpeg build's bin folder.");
    }

    private void FfmpegReset_Click(object sender, RoutedEventArgs e) => Ffmpeg.ClearCustom();

    private void BrowseRecordings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where recordings are saved",
            InitialDirectory = Directory.Exists(RecordingsDirectory.Text) ? RecordingsDirectory.Text : null,
        };
        if (dialog.ShowDialog(this) == true) RecordingsDirectory.Text = dialog.FolderName;
    }

    private void OpenRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RecordingsDirectory.Text);
            Process.Start(new ProcessStartInfo(RecordingsDirectory.Text) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error(ex, "Open recordings folder"); }
    }

    private void UpdatePatternPreview()
    {
        string ext = ImageFormat.SelectedIndex == 1 ? ".jpg" : ".png";
        PatternPreview.Text = "Example: " + FileNameFormatter.Format(FileNamePattern.Text, "chrome", _s.Counter, DateTime.Now) + ext;
    }

    private void UpdateQualityPanel()
    {
        if (QualityPanel != null) QualityPanel.IsEnabled = ImageFormat.SelectedIndex == 1;
    }

    private void Pattern_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdatePatternPreview();
    }

    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateQualityPanel();
        UpdatePatternPreview();
    }

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText != null) QualityText.Text = $"{(int)e.NewValue}";
    }

    private void PreviewSeconds_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewSecondsText != null) PreviewSecondsText.Text = $"{(int)e.NewValue} s";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where captures are saved",
            InitialDirectory = Directory.Exists(OutputDirectory.Text) ? OutputDirectory.Text : null,
        };
        if (dialog.ShowDialog(this) == true) OutputDirectory.Text = dialog.FolderName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory.Text);
            Process.Start(new ProcessStartInfo(OutputDirectory.Text) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error(ex, "Open folder from settings"); }
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "Open data folder"); }
    }
}
