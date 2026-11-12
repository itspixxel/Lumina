using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Recording;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

/// <summary>Plays a recording and trims it; saves the trim back, or exports MP4 / GIF copies.</summary>
public partial class VideoEditorWindow : ChromeWindow
{
    private string _path;
    private double _duration;
    private bool _playing, _wasPlaying, _busy;
    private double _playFrom;
    /// <summary>The recording has its system audio and microphone on separate tracks, so each can be muted.</summary>
    private bool _sourceTracks;
    /// <summary>The sound switches: all sound (one-track recordings), or each source.</summary>
    private bool _soundOn = true, _systemOn = true, _micOn = true;
    /// <summary>
    /// With one source muted, plays the other on its own in step with the video (which is muted then): a player
    /// can't mute one track of a file. <see cref="_sourceTrack"/> is the track it has, 0 for none.
    /// </summary>
    private readonly MediaPlayer _sourceAudio = new();
    private int _sourceTrack, _sourceVersion;
    private string? _previewDir;
    private readonly CancellationTokenSource _closing = new();

    private VideoEditorWindow(string path)
    {
        InitializeComponent();
        _path = path;
        UpdateTitle();
        Timeline.SeekRequested += t => Seek(t);
        Timeline.TrimChanged += UpdateTrimUi;
        Timeline.Dragging += dragging =>
        {
            if (dragging)
            {
                _wasPlaying = _playing;
                Pause();
            }
            else if (_wasPlaying)
            {
                Play();
            }
        };
        _sourceAudio.MediaOpened += (_, _) => SyncSourceAudio(force: true);
        Closed += (_, _) =>
        {
            _closing.Cancel();
            CompositionTarget.Rendering -= Tick;
            Player.Close();
            ClearPreviews();
        };
        Load();
    }

    /// <param name="copied">Whether the recording was just auto-copied (null when no copy was attempted), to report it.</param>
    public static void Open(string path, bool? copied = null)
    {
        var existing = Application.Current.Windows.OfType<VideoEditorWindow>()
            .FirstOrDefault(w => string.Equals(w._path, path, StringComparison.OrdinalIgnoreCase));
        var window = existing ?? new VideoEditorWindow(path);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (copied is bool ok)
            window.Dispatcher.BeginInvoke(() => window.Toast.Show(ok ? "Copied to clipboard" : "Couldn't copy, the clipboard is busy"),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The recording this window is showing.</summary>
    public string FilePath => _path;

    private void UpdateTitle() => Title = Path.GetFileName(_path) + " - Lumina";

    private void Load()
    {
        LoadingText.Visibility = Visibility.Visible;
        Player.IsMuted = true; // silent while it opens
        _sourceTrack = 0;
        _sourceVersion++;
        ClearPreviews();
        Player.Source = new Uri(_path);
        Player.Play(); // MediaOpened only fires once playback is requested
        _ = CorrectLevelsAsync();
        _ = FindSourceTracksAsync();
    }

    /// <summary>Shows a switch for each audio source when the recording has them on separate tracks.</summary>
    private async Task FindSourceTracksAsync()
    {
        bool found = Ffmpeg.IsAvailable && await Ffmpeg.HasSourceTracksAsync(_path);
        _sourceTracks = found;
        _systemOn = _micOn = true;
        UpdateSoundUi();
        AudioButton.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        SourceButtons.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        SyncMute();
        UpdateTrimUi();
    }

    private async Task CorrectLevelsAsync() => Player.Effect = await VideoLevels.CorrectionAsync();

    // ---------- Playback ----------

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        Player.Position = TimeSpan.Zero;
        SyncMute();
        LoadingText.Visibility = Visibility.Collapsed;
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        Timeline.Duration = _duration;
        Timeline.Start = 0;
        Timeline.End = _duration;
        Timeline.Position = 0;
        AudioButton.IsEnabled = Player.HasAudio;
        if (!Player.HasAudio) _soundOn = false;
        UpdateSoundUi();
        UpdateTrimUi();
        UpdateTime();
        _ = LoadThumbnailsAsync();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        LoadingText.Text = "This video can't be played here, but you can still save or export it.";
        Log.Error(e.ErrorException, "Video playback");
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => Pause();

    private void Play()
    {
        if (_duration <= 0) return;
        double pos = Player.Position.TotalSeconds;
        if (pos < Timeline.Start || pos >= Timeline.End - 0.05) Seek(Timeline.Start);
        Player.Play();
        _playing = true;
        _playFrom = Player.Position.TotalSeconds;
        SyncMute();
        SyncSourceAudio(force: true);
        CompositionTarget.Rendering -= Tick;
        CompositionTarget.Rendering += Tick;
        SyncPlayUi();
    }

    /// <summary>
    /// Sound only while playing: seeking a paused MediaElement (scrubbing, arrow keys) plays a blip of audio
    /// at every step otherwise.
    /// </summary>
    private void SyncMute()
    {
        Player.IsMuted = !_playing || !SoundOn || _sourceTrack != 0;
        _sourceAudio.IsMuted = !_playing;
    }

    private bool SoundOn => _sourceTracks ? _systemOn || _micOn : _soundOn;

    /// <summary>Keeps the source audio playing in step with the video; <paramref name="force"/> lines it up now.</summary>
    private void SyncSourceAudio(bool force)
    {
        if (_sourceTrack == 0) return;
        if (!_playing)
        {
            _sourceAudio.Pause();
            return;
        }
        if (force || (_sourceAudio.Position - Player.Position).Duration() > TimeSpan.FromMilliseconds(200))
            _sourceAudio.Position = Player.Position;
        if (force) _sourceAudio.Play();
    }

    private void Pause()
    {
        Player.Pause();
        _sourceAudio.Pause();
        _playing = false;
        SyncMute();
        CompositionTarget.Rendering -= Tick;
        SyncPlayUi();
    }

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void SyncPlayUi()
    {
        PlayIcon.Data = (Geometry)FindResource(_playing ? "IconPause" : "IconPlay");
        PlayIcon.Fill = _playing ? null : Brushes.White;
        PlayIcon.Stroke = _playing ? Brushes.White : null;
        PlayIcon.StrokeThickness = 3;
        PlayIcon.Margin = new Thickness(_playing ? 0 : 3, 0, 0, 0);
        PlayButton.ToolTip = _playing ? "Pause (Space)" : "Play (Space)";
        // The big play badge waits for the video to actually move before fading: fading it while the player is
        // still seeking redraws that square over a frame that isn't there yet, which flashes black.
        if (!_playing) Fx.SetShow(BigPlay, true);
    }

    private void Tick(object? sender, EventArgs e)
    {
        double pos = Player.Position.TotalSeconds;
        if (Fx.GetShow(BigPlay) && pos > _playFrom + 0.05) Fx.SetShow(BigPlay, false);
        if (pos >= Timeline.End)
        {
            Pause();
            Seek(Timeline.End);
            return;
        }
        Timeline.Position = pos;
        UpdateTime();
        SyncSourceAudio(force: false);
    }

    private void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, _duration);
        Player.Position = TimeSpan.FromSeconds(seconds);
        if (_playing) SyncSourceAudio(force: true);
        Timeline.Position = seconds;
        UpdateTime();
    }

    private void UpdateTime()
    {
        TimeText.Text = Format(Timeline.Position);
        DurationText.Text = " / " + Format(_duration);
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    private bool IsTrimmed => _duration > 0 && (Timeline.Start > 0.05 || Timeline.End < _duration - 0.05);
    private bool KeepAudio => _soundOn && Player.HasAudio;
    private bool IsModified => IsTrimmed || (Player.HasAudio && (_sourceTracks ? !(_systemOn && _micOn) : !KeepAudio));

    private void UpdateTrimUi()
    {
        bool trimmed = IsTrimmed;
        TrimBadge.Visibility = trimmed ? Visibility.Visible : Visibility.Collapsed;
        ResetTrim.Visibility = trimmed ? Visibility.Visible : Visibility.Collapsed;
        TrimText.Text = $"{Format(Timeline.Start)} to {Format(Timeline.End)} ({Format(Timeline.End - Timeline.Start)})";
        bool modified = IsModified;
        SaveButton.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !_busy;
        SavedLabel.Visibility = modified ? Visibility.Collapsed : Visibility.Visible;
        SavedLabel.ToolTip = _path;
        Dispatcher.BeginInvoke(UpdateBarLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BottomBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBarLayout();

    /// <summary>Puts the actions under the times when one row can't fit both.</summary>
    private void UpdateBarLayout()
    {
        double Natural(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize.Width;
        }
        bool wrap = Natural(TimeGroup) + Natural(Actions) + 16 > BottomBar.ActualWidth;
        Grid.SetRow(Actions, wrap ? 1 : 0);
        Grid.SetColumn(Actions, wrap ? 0 : 1);
        Grid.SetColumnSpan(Actions, wrap ? 2 : 1);
        Actions.Margin = wrap ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
        TimeGroup.InvalidateMeasure();
        Actions.InvalidateMeasure();
    }

    private async Task LoadThumbnailsAsync()
    {
        if (_duration <= 0 || !Ffmpeg.IsAvailable) return;
        const int count = 14;
        string dir = Path.Combine(AppPaths.TempDir, "thumbs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            double rate = count / _duration;
            await Ffmpeg.RunJobAsync(
                $"-i {Ffmpeg.Quote(_path)} -vf \"fps={Ffmpeg.Seconds(rate)},scale=-2:96\" -frames:v {count} -q:v 5 {Ffmpeg.Quote(Path.Combine(dir, "t%02d.jpg"))}",
                0, null, _closing.Token);
            var thumbs = new List<BitmapSource>();
            foreach (var file in Directory.GetFiles(dir, "t*.jpg").OrderBy(f => f))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                thumbs.Add(bmp);
            }
            Timeline.SetThumbnails(thumbs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Video thumbnails");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    // ---------- Export ----------

    /// <summary>Writes the current clip (trim + sound choice) as an MP4.</summary>
    private async Task ExportMp4Async(string output)
    {
        if (!IsModified)
        {
            if (!string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => File.Copy(_path, output, true));
            return;
        }
        double start = Timeline.Start, length = Timeline.End - Timeline.Start;
        string encoder = App.Settings.HardwareEncoding ? await Ffmpeg.PickEncoderAsync() : "libx264";
        string audio = AudioArgs();
        await Ffmpeg.RunJobAsync(
            $"-ss {Ffmpeg.Seconds(start)} -i {Ffmpeg.Quote(_path)} -t {Ffmpeg.Seconds(length)} {Ffmpeg.EncoderArgs(encoder)} {Ffmpeg.Yuv709Args} {audio} -movflags +faststart {Ffmpeg.Quote(output)}",
            length, new Progress<double>(ShowProgress), _closing.Token);
    }

    /// <summary>FFmpeg options for the sound the MP4 keeps.</summary>
    private string AudioArgs()
    {
        if (!_sourceTracks) return KeepAudio ? "-c:a aac -b:a 192k" : "-an";
        if (_systemOn && _micOn) return $"-map 0:v -map 0:a -c:a aac -b:a 192k {Ffmpeg.SourceTrackArgs}"; // keep all three tracks
        if (!_systemOn && !_micOn) return "-an";
        // One source left: it becomes the only track.
        return $"-map 0:v -map 0:a:{(_systemOn ? 1 : 2)} -c:a aac -b:a 192k -metadata:s:a:0 handler_name=SoundHandler";
    }

    private async Task ExportGifAsync(string output)
    {
        double start = Timeline.Start, length = Timeline.End - Timeline.Start;
        int width = GifSize.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int w) ? w : 720;
        int fps = width is > 0 and <= 480 ? 12 : 15;
        string scale = width > 0 ? $"scale='min({width},iw)':-1:flags=lanczos," : "";
        await Ffmpeg.RunJobAsync(
            $"-ss {Ffmpeg.Seconds(start)} -i {Ffmpeg.Quote(_path)} -t {Ffmpeg.Seconds(length)} " +
            $"-vf \"fps={fps},{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -loop 0 {Ffmpeg.Quote(output)}",
            length, new Progress<double>(ShowProgress), _closing.Token);
    }

    /// <summary>Runs an export with the actions disabled and progress showing.</summary>
    private async Task<bool> RunAsync(string label, Func<Task> work)
    {
        if (_busy) return false;
        if (!Ffmpeg.IsAvailable)
        {
            RecordingSetupWindow.ShowSetup(firstRun: false);
            return false;
        }
        _busy = true;
        Pause();
        Actions.IsEnabled = false;
        Toast.ShowProgress(label);
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, label);
            Toast.Hide();
            LuminaDialog.Alert(this, "Export failed", ex.Message);
            return false;
        }
        finally
        {
            _busy = false;
            Actions.IsEnabled = true;
            UpdateTrimUi();
        }
    }

    private void ShowProgress(double p) => Toast.SetProgress(p);

    /// <summary>Reports a finished job; <paramref name="path"/> is the file it wrote, if any.</summary>
    private void Done(string text, string? path)
    {
        if (path == null)
        {
            Toast.Show(text);
            return;
        }
        if (App.Settings.CopyToClipboard && ClipboardService.CopyFile(path)) text += ", copied";
        Toast.Show(text, "Show in folder", () => OutputService.ShowInFolder(path));
        App.Instance.Controller.NotifySaved(); // refresh the gallery
    }

    private static string Unique(string path)
    {
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name}_{i}{ext}");
        return path;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>Applies the trim to the recording itself.</summary>
    private async Task SaveAsync()
    {
        if (!IsModified || _busy) return;
        string tmp = Path.Combine(AppPaths.TempDir, $"save-{Guid.NewGuid():N}.mp4");
        if (!await RunAsync("Saving...", () => ExportMp4Async(tmp))) return;

        // The player holds the file open; let go, swap in the trimmed version, and reload.
        Player.Close();
        Player.Source = null;
        // Either file can stay busy for a moment: the player lets go of the recording in the background, and antivirus
        // scans the new file. Windows reports some of that as "access denied" rather than a sharing violation.
        bool replaced = false;
        Exception? last = null;
        for (int attempt = 0; attempt < 40 && !replaced; attempt++)
        {
            try
            {
                File.Move(tmp, _path, true);
                replaced = true;
                if (attempt > 0) Log.Info($"Saved the recording on try {attempt + 1}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(100);
            }
        }
        if (!replaced)
        {
            Log.Error(last!, "Replacing the recording");
            string fallback = Unique(_path);
            try
            {
                File.Move(tmp, fallback);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Saving the recording under a new name");
                Load();
                Toast.Show("Couldn't save, the file is in use. Try again in a moment");
                return;
            }
            _path = fallback;
            UpdateTitle();
        }
        _duration = 0;
        Timeline.SetThumbnails(Array.Empty<BitmapSource>());
        Load();
        Done(replaced ? "Saved" : $"Saved as {Path.GetFileName(_path)}", _path);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save recording as",
            Filter = "MP4 video|*.mp4|Animated GIF|*.gif",
            FileName = Path.GetFileNameWithoutExtension(_path) + (IsTrimmed ? "_clip" : ""),
            InitialDirectory = Path.GetDirectoryName(_path),
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        string output = dialog.FileName;
        bool gif = Path.GetExtension(output).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync();
            return;
        }
        if (await RunAsync(gif ? "Making GIF..." : "Saving...", () => gif ? ExportGifAsync(output) : ExportMp4Async(output)))
            Done($"Saved {Path.GetFileName(output)}", output);
    }

    private async void Gif_Click(object sender, RoutedEventArgs e)
    {
        string output = Unique(Path.ChangeExtension(_path, ".gif"));
        if (await RunAsync("Making GIF...", () => ExportGifAsync(output)))
            Done($"Saved {Path.GetFileName(output)}", output);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    /// <summary>Puts the clip on the clipboard as a file, ready to paste into chats or Explorer.</summary>
    private async Task CopyAsync()
    {
        string file = _path;
        if (IsModified)
        {
            string dir = Path.Combine(AppPaths.TempDir, "clips");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, Path.GetFileNameWithoutExtension(_path) + "_clip.mp4");
            if (!await RunAsync("Preparing clip...", () => ExportMp4Async(file))) return;
        }
        Done(ClipboardService.CopyFile(file) ? "Copied to clipboard" : "Clipboard is busy, try again", null);
    }

    // ---------- UI ----------

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Video_MouseDown(object sender, MouseButtonEventArgs e) => TogglePlay();

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        Timeline.Start = 0;
        Timeline.End = _duration;
        UpdateTrimUi();
    }

    private void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        _soundOn = !_soundOn;
        UpdateSoundUi();
        SyncMute();
        UpdateTrimUi();
    }

    private void SystemButton_Click(object sender, RoutedEventArgs e)
    {
        _systemOn = !_systemOn;
        SourcesChanged();
    }

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        _micOn = !_micOn;
        SourcesChanged();
    }

    private async void SourcesChanged()
    {
        UpdateSoundUi();
        UpdateTrimUi();
        SyncMute();
        await PreviewSourcesAsync();
        SyncMute();
    }

    /// <summary>Icons and tooltips for the sound switches: crossed out and dimmed when muted.</summary>
    private void UpdateSoundUi()
    {
        void Show(Button button, System.Windows.Shapes.Path icon, bool on, string onIcon, string offIcon, string tip)
        {
            icon.Data = (Geometry)FindResource(on ? onIcon : offIcon);
            button.Foreground = (Brush)FindResource(on ? "TextBrush" : "Text2Brush");
            button.ToolTip = tip;
        }
        Show(AudioButton, AudioIcon, _soundOn, "IconSpeaker", "IconSpeakerOff",
            !Player.HasAudio ? "This recording has no sound" : _soundOn ? "Sound on, click to leave it out of saved MP4s (M)" : "Sound off, click to keep it (M)");
        Show(SystemButton, SystemIcon, _systemOn, "IconSpeaker", "IconSpeakerOff",
            _systemOn ? "System audio on, click to mute it" : "System audio muted, click to keep it");
        Show(MicButton, MicIcon, _micOn, "IconMic", "IconMicOff",
            _micOn ? "Microphone on, click to mute it (M)" : "Microphone muted, click to keep it (M)");
    }

    /// <summary>
    /// With just one source on, gets its track ready to play alongside the video: copied out of the recording, not
    /// re-encoded, so it only takes a moment.
    /// </summary>
    private async Task PreviewSourcesAsync()
    {
        int track = _systemOn == _micOn ? 0 : _systemOn ? 1 : 2; // both on (or both off): the video's own sound
        if (track == _sourceTrack) return;
        int version = ++_sourceVersion;
        if (track == 0)
        {
            _sourceTrack = 0;
            _sourceAudio.Close();
            return;
        }
        _previewDir ??= Path.Combine(AppPaths.TempDir, "preview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_previewDir);
        string source = Path.Combine(_previewDir, $"track{track}.m4a");
        if (!File.Exists(source))
        {
            string tmp = Path.Combine(_previewDir, $"track{track}-{version}.m4a");
            try
            {
                await Ffmpeg.RunJobAsync($"-i {Ffmpeg.Quote(_path)} -map 0:a:{track} -c copy {Ffmpeg.Quote(tmp)}", 0, null, _closing.Token);
                File.Move(tmp, source, true);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) Log.Error(ex, "Preparing the audio preview");
                return;
            }
        }
        if (version != _sourceVersion) return; // switched again meanwhile
        _sourceTrack = track;
        _sourceAudio.Open(new Uri(source)); // lined up with the video once it opens
    }

    private void ClearPreviews()
    {
        _sourceAudio.Close();
        if (_previewDir == null) return;
        try
        {
            Directory.Delete(_previewDir, true);
        }
        catch
        {
            // Still open in the player; it's in the temp folder either way.
        }
        _previewDir = null;
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e) => OutputService.ShowInFolder(_path);

    private void Delete_Click(object sender, RoutedEventArgs e) => Delete();

    /// <summary>Asks, then moves the recording to the Recycle Bin, or deletes it for good with Shift held.</summary>
    private void Delete()
    {
        bool permanent = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool sure = permanent
            ? LuminaDialog.ConfirmPermanentDelete(this, Path.GetFileName(_path))
            : LuminaDialog.Show(this, "Delete this recording?", "It goes to the Recycle Bin, so you can still restore it.",
                DialogKind.Warning, null,
                new DialogButton("Cancel", "cancel", Cancel: true),
                new DialogButton("Delete", "delete", Danger: true)) == "delete";
        if (!sure) return;
        Player.Close();
        Player.Source = null;
        string path = _path;
        Close();
        Dispatcher.BeginInvoke(() =>
        {
            if (permanent) OutputService.DeletePermanently(path);
            else OutputService.Recycle(path);
            App.Instance.Controller.NotifySaved();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SetInPoint()
    {
        Timeline.Start = Math.Min(Timeline.Position, Math.Max(0, Timeline.End - 0.2));
        UpdateTrimUi();
    }

    private void SetOutPoint()
    {
        Timeline.End = Math.Max(Timeline.Position, Math.Min(_duration, Timeline.Start + 0.2));
        UpdateTrimUi();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is ComboBox or ComboBoxItem) return;
        var mods = Keyboard.Modifiers;
        double step = mods.HasFlag(ModifierKeys.Shift) ? 5 : 1.0 / 30;
        switch (e.Key)
        {
            case Key.Space when mods == ModifierKeys.None:
                TogglePlay();
                break;
            case Key.Left:
                Pause();
                Seek(Timeline.Position - step);
                break;
            case Key.Right:
                Pause();
                Seek(Timeline.Position + step);
                break;
            case Key.Home:
                Seek(Timeline.Start);
                break;
            case Key.End:
                Seek(Timeline.End);
                break;
            case Key.I when mods == ModifierKeys.None:
                SetInPoint();
                break;
            case Key.O when mods == ModifierKeys.None:
                SetOutPoint();
                break;
            case Key.S when mods == ModifierKeys.Control:
                e.Handled = true;
                await SaveAsync();
                return;
            case Key.C when mods == ModifierKeys.Control:
                e.Handled = true;
                await CopyAsync();
                return;
            case Key.S when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                SaveAs_Click(this, e);
                break;
            case Key.G when mods == ModifierKeys.Control:
                Gif_Click(this, e);
                break;
            case Key.E when mods == ModifierKeys.Control:
                OutputService.ShowInFolder(_path);
                break;
            case Key.M when mods == ModifierKeys.None && _sourceTracks:
                MicButton_Click(this, e);
                break;
            case Key.M when mods == ModifierKeys.None && AudioButton.IsEnabled:
                AudioButton_Click(this, e);
                break;
            case Key.R when mods == ModifierKeys.None && IsTrimmed:
                ResetTrim_Click(this, e);
                break;
            case Key.Delete when !mods.HasFlag(ModifierKeys.Alt):
                Delete();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
