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

/// <summary>Runs a screen recording end to end: pick an area, count down, record, save, open the trimmer.</summary>
public sealed class RecordingController
{
    private ScreenRecorder? _recorder;
    private RecordingToolbar? _toolbar;
    private RecordingFrame? _frame;
    private string? _appName;
    private bool _busy;
    private readonly List<Window> _hidden = new();

    /// <summary>Raised on the UI thread when recording starts or stops.</summary>
    public event Action? StateChanged;

    public bool IsRecording => _recorder != null;
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
            QuickPreviewWindow.CloseCurrent();
            HideOwnWindows();
            if (_hidden.Count > 0) await Task.Delay(200);

            var area = await CaptureSession.PickRecordingAreaAsync(s);
            if (area == null)
            {
                RestoreWindows();
                return;
            }
            if (s.RecordingCountdown > 0) await CountdownWindow.RunAsync(s.RecordingCountdown, area.Bounds, "RECORDING IN");
            else await Task.Delay(150); // let the overlay fade out of the first frame

            var recorder = new ScreenRecorder(area.Bounds, new RecordingOptions(
                Math.Clamp(s.RecordingFps, 5, 60), s.RecordCursor,
                s.RecordSystemAudio, s.SystemAudioDevice, s.RecordMicrophone, s.MicrophoneDevice, s.HardwareEncoding));
            recorder.Failed += ex => Application.Current.Dispatcher.BeginInvoke(() => OnFailed(recorder, ex));
            await recorder.StartAsync();

            _recorder = recorder;
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
        catch (Exception ex)
        {
            Log.Error(ex, "Start recording");
            CloseControls();
            _recorder = null;
            RestoreWindows();
            LuminaDialog.Alert(null, "Recording couldn't start", ex.Message);
        }
        finally
        {
            _busy = false;
        }
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
        _frame?.Dismiss();
        _frame = null;
        _toolbar?.ShowSaving();
        string? path = null;
        try
        {
            path = OutputService.ReserveVideoPath(_appName);
            await recorder.FinishAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Finish recording");
            path = null;
            LuminaDialog.Alert(null, "Recording couldn't be saved", ex.Message);
        }
        finally
        {
            CloseControls();
            _recorder = null;
            _busy = false;
            RestoreWindows();
            StateChanged?.Invoke();
        }
        if (path != null && File.Exists(path))
        {
            bool? copied = App.Settings.CopyToClipboard ? ClipboardService.CopyFile(path) : null;
            VideoEditorWindow.Open(path, copied);
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
                     .Where(w => w.IsVisible && w is HomeWindow or SettingsWindow or EditorWindow or VideoEditorWindow).ToList())
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
