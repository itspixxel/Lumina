using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lumina.Capture;
using Lumina.Services;
using Lumina.Views;

namespace Lumina.Recording;

/// <summary>
/// Runs a screen recording end to end: pick an area, count down, record, save, open the trimmer. Recordings
/// started from the command line skip the picker and hand the saved path back instead.
/// </summary>
public sealed class RecordingController
{
    private ScreenRecorder? _recorder;
    private RecordingToolbar? _toolbar;
    private RecordingFrame? _frame;
    private string? _appName;
    private bool _busy;
    private ScriptedRun? _scripted;
    private readonly List<Window> _hidden = new();

    /// <summary>A recording started from the command line, waiting for its saved path.</summary>
    private sealed class ScriptedRun(bool openEditor)
    {
        public bool OpenEditor { get; } = openEditor;
        public TaskCompletionSource<string> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Raised on the UI thread when recording starts or stops.</summary>
    public event Action? StateChanged;

    public bool IsRecording => _recorder != null;
    /// <summary>Starting, stopping or saving a recording.</summary>
    public bool IsBusy => _busy;
    public bool IsPaused => _recorder?.IsPaused ?? false;
    public TimeSpan Elapsed => _recorder?.Elapsed ?? TimeSpan.Zero;

    /// <summary>The record shortcut starts a recording, or stops the one running.</summary>
    public void Toggle()
    {
        if (_recorder != null) Stop();
        else Start();
    }

    public async void Start()
    {
        if (_busy || _recorder != null) return;
        if (!Ffmpeg.IsAvailable)
        {
            RecordingSetupWindow.ShowSetup(firstRun: false);
            return;
        }

        _busy = true;
        var s = App.Settings;
        try
        {
            await HideOwnWindowsAsync();
            var area = await CaptureSession.PickRecordingAreaAsync(s);
            if (area == null)
            {
                RestoreWindows();
                return;
            }
            await BeginAsync(area, s.RecordingCountdown, s.RecordSystemAudio, s.RecordMicrophone, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start recording");
            AbortStart();
            LuminaDialog.Alert(null, "Recording couldn't start", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Records <paramref name="command"/>'s area with no picker or clicks, for the command line. Completes with
    /// the saved file's path once the recording stops (after its time is up, on <c>--stop</c>, or from the
    /// toolbar), or fails with a message for the caller instead of showing a dialog.
    /// </summary>
    public async Task<string> RecordAsync(RecordCommand command)
    {
        if (_busy || _recorder != null) throw new CommandException("Lumina is already recording.");
        if (!Ffmpeg.IsAvailable) throw new CommandException("FFmpeg isn't set up yet. Start a recording in Lumina once to download it.");

        _busy = true;
        var s = App.Settings;
        var scripted = new ScriptedRun(command.OpenEditor);
        try
        {
            await HideOwnWindowsAsync();
            var area = await command.ResolveAsync();
            await BeginAsync(area, command.Countdown ? s.RecordingCountdown : 0,
                command.SystemAudio && s.RecordSystemAudio, command.Microphone, scripted);
        }
        catch (Exception ex)
        {
            if (ex is not CommandException) Log.Error(ex, "Start recording from the command line");
            AbortStart();
            throw;
        }
        finally
        {
            _busy = false;
        }
        if (command.Seconds is double seconds) _ = StopAfterAsync(_recorder!, TimeSpan.FromSeconds(seconds));
        return await scripted.Saved.Task;
    }

    /// <summary>Stops a recording started from the command line and completes with its saved path.</summary>
    public Task<string> StopScriptedAsync()
    {
        if (_scripted is not { } scripted) throw new CommandException("No recording started from the command line is running.");
        Stop();
        return scripted.Saved.Task;
    }

    private async Task StopAfterAsync(ScreenRecorder recorder, TimeSpan length)
    {
        var tick = TimeSpan.FromMilliseconds(50);
        while (_recorder == recorder)
        {
            var left = length - recorder.Elapsed; // pauses don't count
            if (left <= TimeSpan.Zero && !_busy)
            {
                Stop();
                return;
            }
            await Task.Delay(left > TimeSpan.Zero && left < tick ? left : tick);
        }
    }

    private async Task HideOwnWindowsAsync()
    {
        QuickPreviewWindow.CloseCurrent();
        HideOwnWindows();
        if (_hidden.Count > 0) await Task.Delay(200);
    }

    /// <summary>Counts down, starts the recorder and shows the recording frame and toolbar.</summary>
    private async Task BeginAsync(RecordArea area, int countdown, bool systemAudio, bool microphone, ScriptedRun? scripted)
    {
        var s = App.Settings;
        if (countdown > 0) await CountdownWindow.RunAsync(countdown, area.Bounds, "RECORDING IN");
        else await Task.Delay(150); // let the overlay fade out of the first frame

        var recorder = new ScreenRecorder(area.Bounds, new RecordingOptions(
            Math.Clamp(s.RecordingFps, 5, 144), s.RecordCursor,
            systemAudio, s.SystemAudioDevice, microphone, s.MicrophoneDevice, s.HardwareEncoding));
        recorder.Failed += ex => Application.Current.Dispatcher.BeginInvoke(() => OnFailed(recorder, ex));
        await recorder.StartAsync();

        _recorder = recorder;
        _scripted = scripted;
        _appName = area.AppName;
        _frame = new RecordingFrame(recorder);
        _frame.Show();
        _toolbar = new RecordingToolbar(this, recorder);
        _toolbar.Show();
        _toolbar.Begin();
        StateChanged?.Invoke();

        if (recorder.AudioProblems.Count > 0)
            Log.Info("Recording without: " + string.Join(", ", recorder.AudioProblems));
    }

    private void AbortStart()
    {
        CloseControls();
        _recorder = null;
        _scripted = null;
        RestoreWindows();
    }

    public void TogglePause()
    {
        if (_recorder is not { } r || _busy) return;
        if (r.IsPaused) r.Resume();
        else r.Pause();
        _toolbar?.SetPaused(r.IsPaused);
        _frame?.SetPaused(r.IsPaused);
    }

    public async void Stop()
    {
        if (_recorder is not { } recorder || _busy) return;
        _busy = true;
        var scripted = _scripted;
        _frame?.Dismiss();
        _frame = null;
        _toolbar?.ShowSaving();
        string? path = null;
        string? error = null;
        try
        {
            path = OutputService.ReserveVideoPath(_appName);
            LibraryIndex.SetApp(path, _appName);
            await recorder.FinishAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Finish recording");
            path = null;
            error = ex.Message;
            if (scripted == null) LuminaDialog.Alert(null, "Recording couldn't be saved", ex.Message);
        }
        finally
        {
            CloseControls();
            _recorder = null;
            _scripted = null;
            _busy = false;
            RestoreWindows();
            StateChanged?.Invoke();
        }
        bool saved = path != null && File.Exists(path);
        if (scripted != null)
        {
            if (saved) scripted.Saved.TrySetResult(path!);
            else scripted.Saved.TrySetException(new CommandException("The recording couldn't be saved" + (error != null ? ": " + error : ".")));
            if (!scripted.OpenEditor) return;
        }
        if (saved)
        {
            bool? copied = App.Settings.CopyToClipboard ? ClipboardService.CopyFile(path!) : null;
            VideoEditorWindow.Open(path!, copied);
        }
    }

    public async void Discard()
    {
        if (_recorder is not { } recorder || _busy) return;
        bool wasPaused = recorder.IsPaused;
        if (!wasPaused) TogglePause();
        var answer = LuminaDialog.Show(null, "Discard this recording?", "It won't be saved anywhere.", DialogKind.Warning, null,
            new DialogButton("Keep recording", "keep", Cancel: true),
            new DialogButton("Discard", "discard", Danger: true));
        if (answer != "discard" || _recorder != recorder)
        {
            if (!wasPaused && _recorder == recorder) TogglePause();
            return;
        }
        _busy = true;
        try
        {
            await recorder.DiscardAsync();
        }
        finally
        {
            CloseControls();
            _recorder = null;
            _scripted?.Saved.TrySetException(new CommandException("The recording was discarded."));
            _scripted = null;
            _busy = false;
            RestoreWindows();
            StateChanged?.Invoke();
        }
    }

    private void OnFailed(ScreenRecorder recorder, Exception ex)
    {
        if (_recorder != recorder) return;
        // FFmpeg died mid-recording; FinishAsync reports what it said and keeps whatever it can.
        Stop();
    }

    private void HideOwnWindows()
    {
        _hidden.Clear();
        if (!App.Settings.HideWindowsDuringCapture) return;
        foreach (var w in Application.Current.Windows.OfType<Window>()
                     .Where(w => w.IsVisible && w is HomeWindow or GalleryWindow or SettingsWindow or EditorWindow or VideoEditorWindow).ToList())
        {
            w.Hide();
            _hidden.Add(w);
        }
    }

    private void RestoreWindows()
    {
        foreach (var w in _hidden) w.Show();
        _hidden.Clear();
    }

    private void CloseControls()
    {
        _toolbar?.Dismiss();
        _toolbar = null;
        _frame?.Dismiss();
        _frame = null;
    }
}
