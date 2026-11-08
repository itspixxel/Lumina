using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lumina.Capture;
using Lumina.Services;
using static Lumina.Native.NativeMethods;

namespace Lumina.Recording;

/// <summary>Time spent recording, not counting pauses. Shared by the video and audio threads.</summary>
internal sealed class RecordingClock
{
    private readonly Stopwatch _watch = new();
    private readonly object _lock = new();

    public TimeSpan Elapsed
    {
        get { lock (_lock) return _watch.Elapsed; }
    }

    public bool IsRunning
    {
        get { lock (_lock) return _watch.IsRunning; }
    }

    public void Start() { lock (_lock) _watch.Start(); }
    public void Pause() { lock (_lock) _watch.Stop(); }
}

public sealed record RecordingOptions(
    int Fps,
    bool Cursor,
    bool SystemAudio,
    string? SystemDevice,
    bool Microphone,
    string? MicDevice,
    bool HardwareEncoding);

/// <summary>
/// Records part of the screen to MP4. Lumina grabs the frames itself (GDI, physical pixels, so mixed-DPI
/// monitors line up with the selection) and pipes them into FFmpeg; audio goes to WAV files on the side and
/// is mixed in when recording stops. Pausing simply stops feeding frames, so there's nothing to stitch.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly Int32Rect _area;
    // Where on screen frames come from: the area, wherever the recording frame has been moved to.
    private Int32Rect _source;
    private readonly object _sourceLock = new();
    private readonly RecordingOptions _options;
    private readonly RecordingClock _clock = new();
    private readonly string _workDir;
    private readonly string _videoPath;
    private Process? _ffmpeg;
    private Task<string>? _ffmpegErrors;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _stopped;
    private AudioCapture? _system, _mic;
    private Exception? _failure;
    private long _frames, _repeats;
    private readonly System.Collections.Concurrent.BlockingCollection<FrameBuffer> _queue = new(2);
    private readonly System.Collections.Concurrent.BlockingCollection<FrameBuffer> _free = new();

    /// <summary>Raised (on a worker thread) if recording breaks, e.g. FFmpeg exits.</summary>
    public event Action<Exception>? Failed;

    public ScreenRecorder(Int32Rect area, RecordingOptions options)
    {
        // H.264 in 4:2:0 needs even dimensions.
        _area = new Int32Rect(area.X, area.Y, Math.Max(2, area.Width & ~1), Math.Max(2, area.Height & ~1));
        _source = _area;
        _options = options;
        _workDir = Path.Combine(AppPaths.TempDir, "rec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        _videoPath = Path.Combine(_workDir, "video.mp4");
    }

    /// <summary>The video's size, and where recording started.</summary>
    public Int32Rect Area => _area;

    /// <summary>The part of the screen being recorded right now. It can move but keeps the video's size.</summary>
    public Int32Rect Source
    {
        get { lock (_sourceLock) return _source; }
        set
        {
            var r = new Int32Rect(value.X, value.Y, _area.Width, _area.Height);
            lock (_sourceLock) _source = r;
        }
    }
    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsPaused { get; private set; }
    /// <summary>Frames written, and how many of those repeated the previous one because a grab ran late.</summary>
    public (long Frames, long Repeats) Stats => (_frames, _repeats);
    public bool HasSystemAudio => _system != null;
    public bool HasMicrophone => _mic != null;
    /// <summary>Audio sources that were asked for but couldn't be opened.</summary>
    public List<string> AudioProblems { get; } = new();

    public bool SystemMuted
    {
        get => _system?.Muted ?? false;
        set { if (_system != null) _system.Muted = value; }
    }

    public bool MicMuted
    {
        get => _mic?.Muted ?? false;
        set { if (_mic != null) _mic.Muted = value; }
    }

    public async Task StartAsync()
    {
        string encoder = _options.HardwareEncoding ? await Ffmpeg.PickEncoderAsync() : "libx264";
        _ffmpeg = StartEncoder(encoder);
        await Task.Delay(150);
        if (_ffmpeg.HasExited && encoder != "libx264")
        {
            // The GPU encoder passed the probe but won't take this size (e.g. too small); use x264.
            Log.Info($"{encoder} refused {_area.Width}x{_area.Height}: {Ffmpeg.Tail(await _ffmpegErrors!)}");
            _ffmpeg.Dispose();
            _ffmpeg = StartEncoder("libx264");
        }

        if (_options.SystemAudio) _system = OpenAudio(AudioFlow.Output, _options.SystemDevice, "system.wav", "System audio");
        if (_options.Microphone) _mic = OpenAudio(AudioFlow.Input, _options.MicDevice, "mic.wav", "Microphone");

        timeBeginPeriod(1);
        _clock.Start();
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Lumina.Recorder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private Process StartEncoder(string encoder)
    {
        string args = "-hide_banner -loglevel error " +
                      $"-f rawvideo -pix_fmt bgr0 -video_size {_area.Width}x{_area.Height} -framerate {_options.Fps} -i - " +
                      $"{Ffmpeg.EncoderArgs(encoder)} -pix_fmt yuv420p -g {_options.Fps * 2} -movflags +faststart -y {Ffmpeg.Quote(_videoPath)}";
        var p = Ffmpeg.Start(args, redirectInput: true);
        _ffmpegErrors = p.StandardError.ReadToEndAsync();
        return p;
    }

    private AudioCapture? OpenAudio(AudioFlow flow, string? device, string file, string label)
    {
        var capture = new AudioCapture(flow, device, _clock, Path.Combine(_workDir, file));
        try
        {
            capture.Start();
            return capture;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Opening {label}");
            AudioProblems.Add(label);
            return null;
        }
    }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _clock.Pause();
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _clock.Start();
    }

    // ---------- Frames ----------

    /// <summary>A screen-sized DIB that frames are grabbed into, and how many times the writer should send it.</summary>
    private sealed class FrameBuffer
    {
        public IntPtr Dc, Dib, Old, Bits;
        public int Count;
    }

    /// <summary>
    /// Grabs frames on this thread while <see cref="WriteLoop"/> feeds FFmpeg on another, through a small ring of
    /// buffers, so the grab and the pipe write overlap instead of adding up.
    /// </summary>
    private void CaptureLoop()
    {
        int w = _area.Width, h = _area.Height, fps = _options.Fps;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        var buffers = new List<FrameBuffer>();
        for (int i = 0; i < 3; i++)
        {
            var b = new FrameBuffer { Dc = CreateCompatibleDC(screenDc) };
            b.Dib = CreateDIBSection(screenDc, ref header, 0, out b.Bits, IntPtr.Zero, 0);
            b.Old = SelectObject(b.Dc, b.Dib);
            buffers.Add(b);
            _free.Add(b);
        }
        var writer = new Thread(WriteLoop) { IsBackground = true, Name = "Lumina.Recorder.Writer", Priority = ThreadPriority.AboveNormal };
        writer.Start();
        try
        {
            while (!_stop)
            {
                if (!_clock.IsRunning)
                {
                    Thread.Sleep(5);
                    continue;
                }

                // Frames owed so far; a late grab is made up by repeating it, which keeps the video in sync with the audio.
                long due = (long)(_clock.Elapsed.TotalSeconds * fps) + 1;
                if (_frames >= due)
                {
                    double next = (double)_frames / fps - _clock.Elapsed.TotalSeconds;
                    Thread.Sleep(Math.Max(1, (int)(next * 1000) - 1));
                    continue;
                }
                if (!_free.TryTake(out var buffer, 50)) continue; // the writer is behind

                var src = Source;
                BitBlt(buffer.Dc, 0, 0, w, h, screenDc, src.X, src.Y, SRCCOPY);
                if (_options.Cursor) ScreenCapture.DrawCursor(buffer.Dc, src.X, src.Y);
                GdiFlush();

                due = (long)(_clock.Elapsed.TotalSeconds * fps) + 1;
                buffer.Count = (int)Math.Max(1, due - 1 - _frames);
                _frames += buffer.Count;
                _repeats += buffer.Count - 1;
                _queue.Add(buffer);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            _queue.CompleteAdding();
            writer.Join();
            foreach (var b in buffers) FreeBuffer(b);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void FreeBuffer(FrameBuffer b)
    {
        if (b.Dc == IntPtr.Zero) return;
        SelectObject(b.Dc, b.Old);
        DeleteObject(b.Dib);
        DeleteDC(b.Dc);
        b.Dc = IntPtr.Zero;
    }


    private unsafe void WriteLoop()
    {
        int bytes = _area.Width * _area.Height * 4;
        var input = _ffmpeg!.StandardInput.BaseStream;
        bool broken = false;
        foreach (var buffer in _queue.GetConsumingEnumerable())
        {
            if (!broken)
            {
                try
                {
                    var frame = new ReadOnlySpan<byte>((void*)buffer.Bits, bytes);
                    for (int i = 0; i < buffer.Count; i++) input.Write(frame);
                }
                catch (Exception ex)
                {
                    broken = true; // keep draining so the capture thread never blocks on a full queue
                    Fail(ex);
                }
            }
            _free.Add(buffer);
        }
    }

    private void Fail(Exception ex)
    {
        if (_stop || _failure != null) return;
        _failure = ex;
        _stop = true;
        Log.Error(ex, "Recording");
        Failed?.Invoke(ex);
    }

    // ---------- Finishing ----------

    /// <summary>Stops, finishes encoding, mixes in the audio, and moves the result to <paramref name="outputPath"/>.</summary>
    public async Task FinishAsync(string outputPath)
    {
        StopCapturing();
        Log.Info($"Recorded {_area.Width}x{_area.Height} @ {_options.Fps} fps: {_frames} frames, {_repeats} repeated");
        try
        {
            if (_ffmpeg == null) throw new InvalidOperationException("Recording never started.");
            try { _ffmpeg.StandardInput.Close(); } catch { /* encoder already gone */ }
            await _ffmpeg.WaitForExitAsync();
            string errors = await _ffmpegErrors!;
            if (_ffmpeg.ExitCode != 0 || !File.Exists(_videoPath))
                throw new InvalidOperationException(_failure != null ? Ffmpeg.Tail(errors) : "FFmpeg couldn't encode the recording.\n" + Ffmpeg.Tail(errors));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var tracks = new List<string>();
            if (_system is { HasAudio: true }) tracks.Add(_system.Path);
            if (_mic is { HasAudio: true }) tracks.Add(_mic.Path);

            if (tracks.Count == 0)
            {
                File.Move(_videoPath, outputPath, true);
                return;
            }

            var args = new StringBuilder($"-i {Ffmpeg.Quote(_videoPath)}");
            foreach (var t in tracks) args.Append($" -i {Ffmpeg.Quote(t)}");
            if (tracks.Count == 1)
                args.Append(" -map 0:v -map 1:a");
            else
                args.Append(" -filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]\" -map 0:v -map \"[a]\"");
            args.Append($" -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart {Ffmpeg.Quote(outputPath)}");
            await Ffmpeg.RunJobAsync(args.ToString(), 0, null);
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>Stops and throws everything away.</summary>
    public async Task DiscardAsync()
    {
        _stop = true;
        if (_ffmpeg != null)
        {
            try { _ffmpeg.Kill(true); } catch { /* already gone */ }
            try { await _ffmpeg.WaitForExitAsync(); } catch { /* ignore */ }
        }
        StopCapturing();
        Cleanup();
    }

    private void StopCapturing()
    {
        if (_stopped) return;
        _stopped = true;
        _stop = true;
        _clock.Pause();
        _thread?.Join(5000);
        _system?.Stop();
        _mic?.Stop();
        timeEndPeriod(1);
    }

    private void Cleanup()
    {
        _ffmpeg?.Dispose();
        try { Directory.Delete(_workDir, true); } catch { /* a file is still held; temp is cleaned eventually */ }
    }
}
