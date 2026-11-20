using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        // Opening the editor always skipped the preview, so the two switches are really one choice.
        AfterScreenshot.SelectedIndex = _s.OpenEditorAfterCapture ? 1 : _s.ShowQuickPreview ? 0 : 2;
        SetRowShown(PreviewSecondsRow, AfterScreenshot.SelectedIndex == 0, animate: false);
        PreviewSeconds.Value = _s.PreviewSeconds;
        OutputDirectory.Text = _s.OutputDirectory;
        FileNamePattern.Text = _s.FileNamePattern;
        ImageFormat.SelectedIndex = _s.ImageFormat == ImageFormatKind.Jpeg ? 1 : 0;
        JpegQuality.Value = _s.JpegQuality;
        StartWithWindows.IsChecked = _s.StartWithWindows;
        AutoUpdate.IsChecked = _s.AutoUpdate;
        LoadRecording();
        LoadSharing();

        PatternHelp.Text = "Tokens: " + FileNameFormatter.TokenHelp;
        VersionText.Text = $"Lumina {Updater.Current.ToString(3)}" + (AppPaths.IsPortable ? " (portable)" : "");
        DataFolderText.Text = AppPaths.DataDir;
        SyncUpdate();
        Updater.StateChanged += SyncUpdate;
        Closed += (_, _) => Updater.StateChanged -= SyncUpdate;

        UpdatePatternPreview();
        UpdateQualityPanel();
        PreviewSecondsText.Text = $"{_s.PreviewSeconds} s";
        QualityText.Text = $"{_s.JpegQuality}";
        foreach (var page in PageList) page.Margin = new Thickness(0, 0, 0, 28); // room between sections in search results
        ShowPage(_lastPage);
        Appearance.Changed += () => Changed(this, EventArgs.Empty);
        Loaded += (_, _) => RowLines.Update(Pages);
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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Keyboard.FocusedElement is ComboBox or ComboBoxItem) return;
            e.Handled = true;
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        };
    }

    // ---------- Pages and search ----------

    /// <summary>The page shown last, so Settings reopens where it was left (for this run of Lumina).</summary>
    private static string _lastPage = "Shortcuts";
    private string _page = _lastPage;
    /// <summary>Rows hidden because of another setting (the device list while that sound is off); search leaves them hidden.</summary>
    private readonly HashSet<FrameworkElement> _stateHidden = new();

    private IEnumerable<StackPanel> PageList => Pages.Children.OfType<StackPanel>().Where(p => p.Tag is string);

    /// <summary>Opens a page by its tag (Shortcuts, Capture, Recording, AfterCapture, Output, Sharing, Appearance, General, About).</summary>
    public void ShowPage(string tag)
    {
        if (PageList.All(p => (string)p.Tag != tag)) tag = "Shortcuts";
        _page = _lastPage = tag;
        foreach (var nav in Nav.Children.OfType<RadioButton>())
            if ((string)nav.Tag == tag && nav.IsChecked != true) nav.IsChecked = true;
        if (SearchBox.Text.Length == 0)
            foreach (var page in PageList) page.Visibility = (string)page.Tag == tag ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (SearchBox.Text.Length > 0) SearchBox.Clear(); // picking a section ends the search
        ShowPage(tag);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplySearch();
    }

    /// <summary>
    /// Shows every row, on any page, whose words (with its page and group names) contain all the search words.
    /// An empty search goes back to the selected page.
    /// </summary>
    private void ApplySearch()
    {
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool searching = words.Length > 0;
        bool Matches(string text) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        var rowStyle = (Style)FindResource("SettingsRow");
        int found = 0;

        foreach (var page in PageList)
        {
            string pageTitle = page.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
            bool pageHas = false;
            foreach (var group in RowLines.Descendants(page).OfType<StackPanel>().Where(g => g.Tag as string == "group").ToList())
            {
                string groupTitle = group.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
                var rows = RowLines.Descendants(group).OfType<Border>().Where(b => b.Style == rowStyle).ToList();
                bool groupHas;
                if (rows.Count == 0)
                {
                    groupHas = !searching || Matches($"{pageTitle} {groupTitle} {TextOf(group)}");
                }
                else
                {
                    groupHas = false;
                    foreach (var row in rows)
                    {
                        bool shown = !_stateHidden.Contains(row) && (!searching || Matches($"{pageTitle} {groupTitle} {TextOf(row)}"));
                        row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                        if (shown && !InsideCollapsed(row, group)) groupHas = true;
                    }
                }
                group.Visibility = groupHas ? Visibility.Visible : Visibility.Collapsed;
                if (groupHas)
                {
                    pageHas = true;
                    found++;
                }
            }
            page.Visibility = searching ? (pageHas ? Visibility.Visible : Visibility.Collapsed)
                                        : (string)page.Tag == _page ? Visibility.Visible : Visibility.Collapsed;
        }
        // While results come from several sections, no one section is selected.
        if (searching)
        {
            foreach (var nav in Nav.Children.OfType<RadioButton>()) nav.IsChecked = false;
        }
        else if (Nav.Children.OfType<RadioButton>().All(n => n.IsChecked != true)) ShowPage(_page);
        NoResults.Visibility = searching && found == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsTitle.Text = $"No settings match '{SearchBox.Text.Trim()}'";
        RowLines.Update(Pages);
        PageScroller.ScrollToTop();
    }

    /// <summary>Hidden by its own panel (Appearance's "all colors" list) rather than by search.</summary>
    private static bool InsideCollapsed(DependencyObject row, DependencyObject stop)
    {
        for (var d = LogicalTreeHelper.GetParent(row); d != null && d != stop; d = LogicalTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return true;
        return false;
    }

    /// <summary>All the words in a part of the window: labels, hints, and the choices in its dropdowns.</summary>
    private static string TextOf(DependencyObject root)
    {
        var parts = new List<string>();
        void Walk(DependencyObject d)
        {
            switch (d)
            {
                case TextBlock t: parts.Add(t.Text); break;
                case ContentControl { Content: string s }: parts.Add(s); break;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(child);
        }
        Walk(root);
        return string.Join(' ', parts);
    }

    /// <summary>Shows or hides a row because of another setting, and keeps search from bringing it back.</summary>
    private void SetRowShown(FrameworkElement row, bool shown, bool animate = true)
    {
        if (shown) _stateHidden.Remove(row);
        else _stateHidden.Add(row);
        if (SearchBox.Text.Length > 0) ApplySearch();
        else if (animate && !_loading) Fx.Reveal(row, shown, offsetY: -4);
        else row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        RowLines.Update(Pages);
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
        _s.OpenEditorAfterCapture = AfterScreenshot.SelectedIndex == 1;
        _s.ShowQuickPreview = AfterScreenshot.SelectedIndex == 0;
        _s.PreviewSeconds = (int)PreviewSeconds.Value;

        string before = _s.OutputDirectory, beforeRecordings = _s.RecordingsDirectory;
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
        SaveSharing();

        bool startup = StartWithWindows.IsChecked == true;
        if (startup != _s.StartWithWindows) StartupService.Apply(startup);
        _s.StartWithWindows = startup;
        _s.AutoUpdate = AutoUpdate.IsChecked == true;

        SettingsService.Save(_s);
        if (_s.OutputDirectory != before || _s.RecordingsDirectory != beforeRecordings)
            App.Instance.Controller.NotifySaved(); // refresh the home list
    }

    // ---------- Sharing ----------

    private void LoadSharing()
    {
        Select(ShareExpiry, ShareService.ExpiryHours.ToString());
        bool own = ShareService.UsesOwnServer;
        ShareServerKind.SelectedIndex = own ? 1 : 0;
        ShareServer.Text = _s.ShareServer ?? "";
        ShareToken.Password = _s.ShareToken ?? "";
        if (ShareService.DefaultServer == null) ((ComboBoxItem)ShareServerKind.Items[0]).Content = "Lumina's server (coming soon)";
        SyncSharing();
    }

    private void SaveSharing()
    {
        if (ShareExpiry.SelectedItem is ComboBoxItem { Tag: string hours } && int.TryParse(hours, out int h)) _s.ShareExpiryHours = h;
        string address = ShareServer.Text.Trim();
        if (ShareServerKind.SelectedIndex == 1 && address.Length > 0)
        {
            if (!address.Contains("://")) address = "https://" + address;
            _s.ShareServer = address.TrimEnd('/');
        }
        else
        {
            _s.ShareServer = null;
        }
        _s.ShareToken = string.IsNullOrWhiteSpace(ShareToken.Password) ? null : ShareToken.Password.Trim();
    }

    private void SyncSharing()
    {
        bool own = ShareServerKind.SelectedIndex == 1;
        SetRowShown(OwnServerPanel, own);
        ShareServerHint.Text = own
            ? "Your own Lumina share server, set up below."
            : ShareService.DefaultServer is { } server
                ? $"Files go to {new Uri(server).Host} and are deleted when they expire."
                : "Lumina's own server isn't live yet. Until then, Get link works with your own server.";
    }

    private void ShareServerKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OwnServerPanel != null) SyncSharing();
    }

    private void ShareToken_Changed(object sender, RoutedEventArgs e) => Changed(sender, e);

    private void ShareGuide_Click(object sender, RoutedEventArgs e) => Updater.OpenPage(ShareService.SetupGuide);

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
        // A device list only shows while its sound is being recorded.
        SetRowShown(SystemDeviceRow, RecordSystemAudio.IsChecked == true);
        SetRowShown(MicDeviceRow, RecordMicrophone.IsChecked == true);
    }

    private void AudioCheck_Click(object sender, RoutedEventArgs e) => SyncAudioRows();

    private void SyncFfmpeg()
    {
        bool installing = Ffmpeg.IsInstalling;
        bool available = Ffmpeg.IsAvailable;
        FfmpegStatus.Text = installing ? Ffmpeg.InstallStage : available ? Ffmpeg.Describe() : "Needed for recording - free, " + Ffmpeg.DownloadSizeText;
        FfmpegStatus.ToolTip = FfmpegStatus.Text;
        FfmpegStatus.SetResourceReference(TextBlock.ForegroundProperty, available || installing ? "Text2Brush" : "DangerBrush");
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

    private void OpenRecordingsFolder_Click(object sender, RoutedEventArgs e) => OutputService.OpenFolder(RecordingsDirectory.Text);

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

    private void AfterScreenshot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) SetRowShown(PreviewSecondsRow, AfterScreenshot.SelectedIndex == 0);
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OutputService.OpenFolder(OutputDirectory.Text);

    // ---------- Updates ----------

    private bool _updateAvailable;

    private void SyncUpdate()
    {
        var state = Updater.State;
        UpdateStatus.Text = Updater.Status;
        UpdateStatus.Visibility = Updater.Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = state switch
        {
            UpdateState.Ready => "Restart now",
            UpdateState.Failed when Updater.NeedsManualDownload => "Open GitHub",
            _ when _updateAvailable => "Update now",
            _ => "Check for updates",
        };
        UpdateButton.Style = (Style)FindResource(state == UpdateState.Ready || _updateAvailable ? "AccentButton" : typeof(Button));
        UpdateButton.IsEnabled = state is not (UpdateState.Checking or UpdateState.Downloading);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.State == UpdateState.Ready)
        {
            Commit();
            Updater.Restart();
        }
        else if (Updater.State == UpdateState.Failed && Updater.NeedsManualDownload)
        {
            Updater.OpenPage(Updater.ReleasesPage);
        }
        else
        {
            // A check from here only installs when asked: the first click finds, the second downloads.
            _updateAvailable = await Updater.CheckAsync(install: _updateAvailable) && Updater.State != UpdateState.Ready;
            SyncUpdate();
        }
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "Open data folder"); }
    }
}
