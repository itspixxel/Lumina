using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

/// <summary>Settings apply as you change them and are written to disk when the window closes.</summary>
public partial class SettingsWindow : ChromeWindow
{
    private readonly AppSettings _s = App.Settings;
    private bool _loading = true;

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

        PatternHelp.Text = "Tokens: " + FileNameFormatter.TokenHelp;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Lumina {version?.ToString(3)}" + (AppPaths.IsPortable ? " (portable)" : "");
        DataFolderText.Text = AppPaths.DataDir;

        UpdatePatternPreview();
        UpdateQualityPanel();
        PreviewSecondsText.Text = $"{_s.PreviewSeconds} s";
        QualityText.Text = $"{_s.JpegQuality}";
        _loading = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _s.ShowMagnifier = ShowMagnifier.IsChecked == true;
        _s.CaptureCursor = CaptureCursor.IsChecked == true;
        _s.HideWindowsDuringCapture = HideWindows.IsChecked == true;
        _s.CopyToClipboard = CopyToClipboard.IsChecked == true;
        _s.AutoSave = AutoSave.IsChecked == true;
        _s.ShowQuickPreview = ShowQuickPreview.IsChecked == true;
        _s.OpenEditorAfterCapture = OpenEditor.IsChecked == true;
        _s.PreviewSeconds = (int)PreviewSeconds.Value;

        string dir = OutputDirectory.Text.Trim();
        if (dir.Length > 0)
        {
            try { _s.OutputDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)); }
            catch (Exception ex) { Log.Error(ex, "Invalid output folder"); }
        }
        if (!string.IsNullOrWhiteSpace(FileNamePattern.Text)) _s.FileNamePattern = FileNamePattern.Text.Trim();
        _s.ImageFormat = ImageFormat.SelectedIndex == 1 ? ImageFormatKind.Jpeg : ImageFormatKind.Png;
        _s.JpegQuality = (int)JpegQuality.Value;

        bool startup = StartWithWindows.IsChecked == true;
        if (startup != _s.StartWithWindows) StartupService.Apply(startup);
        _s.StartWithWindows = startup;

        SettingsService.Save(_s);
        App.ApplyShortcuts();
        App.Instance.Controller.NotifySaved(); // output folder may have changed; refresh the home list
        base.OnClosed(e);
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
