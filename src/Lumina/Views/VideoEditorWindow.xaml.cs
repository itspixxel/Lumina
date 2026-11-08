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
    private string? _lastExport;
    private readonly CancellationTokenSource _closing = new();

    private VideoEditorWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Title = Path.GetFileName(path);
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
        Closed += (_, _) =>
        {
            _closing.Cancel();
            CompositionTarget.Rendering -= Tick;
            Player.Close();
        };
        Load();
    }

    public static void Open(string path)
    {
        var existing = Application.Current.Windows.OfType<VideoEditorWindow>()
            .FirstOrDefault(w => string.Equals(w._path, path, StringComparison.OrdinalIgnoreCase));
        var window = existing ?? new VideoEditorWindow(path);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Load()
    {
        LoadingText.Visibility = Visibility.Visible;
        Player.IsMuted = true; // silent while it opens
        Player.Source = new Uri(_path);
        Player.Play(); // MediaOpened only fires once playback is requested
    }

    // ---------- Playback ----------

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        Player.Position = TimeSpan.Zero;
        Player.IsMuted = AudioToggle.IsChecked != true;
        LoadingText.Visibility = Visibility.Collapsed;
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        Timeline.Duration = _duration;
        Timeline.Start = 0;
        Timeline.End = _duration;
        Timeline.Position = 0;
        AudioToggle.IsEnabled = Player.HasAudio;
        if (!Player.HasAudio)
        {
            AudioToggle.IsChecked = false;
            AudioText.Text = "No sound";
            AudioIcon.Data = (Geometry)FindResource("IconSpeakerOff");
        }
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
        CompositionTarget.Rendering -= Tick;
        CompositionTarget.Rendering += Tick;
        SyncPlayUi();
    }

    private void Pause()
    {
        Player.Pause();
        _playing = false;
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
        Fx.SetShow(BigPlay, !_playing);
    }

    private void Tick(object? sender, EventArgs e)
    {
        double pos = Player.Position.TotalSeconds;
        if (pos >= Timeline.End)
        {
            Pause();
            Seek(Timeline.End);
            return;
        }
        Timeline.Position = pos;
        UpdateTime();
    }

    private void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, _duration);
        Player.Position = TimeSpan.FromSeconds(seconds);
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
    private bool KeepAudio => AudioToggle.IsChecked == true && Player.HasAudio;
    private bool IsModified => IsTrimmed || (Player.HasAudio && !KeepAudio);

    private void UpdateTrimUi()
    {
        bool trimmed = IsTrimmed;
        TrimBadge.Visibility = trimmed ? Visibility.Visible : Visibility.Collapsed;
        ResetTrim.Visibility = trimmed ? Visibility.Visible : Visibility.Collapsed;
        TrimText.Text = $"{Format(Timeline.Start)} – {Format(Timeline.End)} · {Format(Timeline.End - Timeline.Start)}";
        SaveButton.IsEnabled = IsModified && !_busy;
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
        string audio = KeepAudio ? "-c:a aac -b:a 192k" : "-an";
        await Ffmpeg.RunJobAsync(
            $"-ss {Ffmpeg.Seconds(start)} -i {Ffmpeg.Quote(_path)} -t {Ffmpeg.Seconds(length)} {Ffmpeg.EncoderArgs(encoder)} -pix_fmt yuv420p {audio} -movflags +faststart {Ffmpeg.Quote(output)}",
            length, new Progress<double>(ShowProgress), _closing.Token);
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
        StatusPanel.Visibility = Visibility.Visible;
        ProgressTrack.Visibility = Visibility.Visible;
        StatusLink.Visibility = Visibility.Collapsed;
        ProgressFill.Width = 0;
        StatusText.Text = label;
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
            StatusPanel.Visibility = Visibility.Collapsed;
            LuminaDialog.Alert(this, "Export failed", ex.Message);
            return false;
        }
        finally
        {
            _busy = false;
            Actions.IsEnabled = true;
            ProgressTrack.Visibility = Visibility.Collapsed;
            UpdateTrimUi();
        }
    }

    private void ShowProgress(double p) => ProgressFill.Width = ProgressTrack.Width * Math.Clamp(p, 0, 1);

    private void Done(string text, string? path)
    {
        _lastExport = path;
        if (path != null && App.Settings.CopyToClipboard && ClipboardService.CopyFile(path)) text += " · copied";
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusLink.Visibility = path != null ? Visibility.Visible : Visibility.Collapsed;
        Fx.PlayEntrance(StatusPanel, 0, offsetY: 4);
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
        if (!await RunAsync("Saving…", () => ExportMp4Async(tmp))) return;

        // The player holds the file open; let go, swap in the trimmed version, and reload.
        Player.Close();
        Player.Source = null;
        bool replaced = false;
        for (int attempt = 0; attempt < 20 && !replaced; attempt++)
        {
            try
            {
                File.Move(tmp, _path, true);
                replaced = true;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
        if (!replaced)
        {
            string fallback = Unique(_path);
            File.Move(tmp, fallback);
            _path = fallback;
            Title = Path.GetFileName(_path);
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
        if (await RunAsync(gif ? "Making GIF…" : "Saving…", () => gif ? ExportGifAsync(output) : ExportMp4Async(output)))
            Done($"Saved {Path.GetFileName(output)}", output);
    }

    private async void Gif_Click(object sender, RoutedEventArgs e)
    {
        string output = Unique(Path.ChangeExtension(_path, ".gif"));
        if (await RunAsync("Making GIF…", () => ExportGifAsync(output)))
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
            if (!await RunAsync("Preparing clip…", () => ExportMp4Async(file))) return;
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

    private void AudioToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = AudioToggle.IsChecked == true;
        AudioText.Text = on ? "Sound on" : "Sound off";
        AudioIcon.Data = (Geometry)FindResource(on ? "IconSpeaker" : "IconSpeakerOff");
        Player.IsMuted = !on;
        UpdateTrimUi();
    }

    private void StatusLink_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExport != null) OutputService.ShowInFolder(_lastExport);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e) => OutputService.ShowInFolder(_path);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var answer = LuminaDialog.Show(this, "Delete this recording?", "It goes to the Recycle Bin, so you can still restore it.",
            DialogKind.Warning, null,
            new DialogButton("Cancel", "cancel", Cancel: true),
            new DialogButton("Delete", "delete", Danger: true));
        if (answer != "delete") return;
        Player.Close();
        Player.Source = null;
        string path = _path;
        Close();
        Dispatcher.BeginInvoke(() => OutputService.Recycle(path), System.Windows.Threading.DispatcherPriority.Background);
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
            default:
                return;
        }
        e.Handled = true;
    }
}
