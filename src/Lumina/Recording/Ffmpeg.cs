using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lumina.Services;

namespace Lumina.Recording;

/// <summary>
/// Finds, installs and runs FFmpeg. Lumina doesn't ship it: the setup card (first launch, Settings, or the
/// Record button) downloads a build into the data folder, or the user points at their own ffmpeg.exe.
/// </summary>
public static class Ffmpeg
{
    /// <summary>gyan.dev's "essentials" build is the usual Windows choice; BtbN's GitHub build is the fallback.</summary>
    private static readonly string[] DownloadUrls =
    {
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
    };

    public const string DownloadSizeText = "about 115 MB";

    private static string InstallDir => Path.Combine(AppPaths.DataDir, "ffmpeg");
    public static string InstalledPath => Path.Combine(InstallDir, "ffmpeg.exe");

    private static CancellationTokenSource? _install;
    private static Task<string>? _encoder;

    /// <summary>Raised on the UI thread when FFmpeg appears, disappears, or install progress moves.</summary>
    public static event Action? StateChanged;

    public static bool IsInstalling => _install != null;
    /// <summary>0 to 1 while downloading, null when the size isn't known yet.</summary>
    public static double? InstallProgress { get; private set; }
    public static string InstallStage { get; private set; } = "";

    /// <summary>The ffmpeg.exe to use, or null when there isn't one.</summary>
    public static string? ExePath => Resolve();
    public static bool IsAvailable => Resolve() != null;

    private static string? Resolve()
    {
        if (App.Settings.FfmpegPath is { Length: > 0 } custom && File.Exists(custom)) return custom;
        if (File.Exists(InstalledPath)) return InstalledPath;
        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Where the current ffmpeg.exe came from, for Settings.</summary>
    public static string Describe()
    {
        var path = Resolve();
        if (path == null) return "Not installed";
        if (string.Equals(path, InstalledPath, StringComparison.OrdinalIgnoreCase)) return "Installed by Lumina";
        if (string.Equals(path, App.Settings.FfmpegPath, StringComparison.OrdinalIgnoreCase)) return path;
        return "Found on this PC - " + path;
    }

    /// <summary>Checks that a file really is a working ffmpeg.exe. Returns its version line, or null.</summary>
    public static async Task<string?> VerifyAsync(string exe)
    {
        try
        {
            var result = await RunAsync(exe, "-hide_banner -version", TimeSpan.FromSeconds(10));
            if (result.ExitCode != 0) return null;
            var first = result.Output.Split('\n').FirstOrDefault()?.Trim();
            return first != null && first.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> UseCustomAsync(string exe)
    {
        if (await VerifyAsync(exe) == null) return false;
        App.Settings.FfmpegPath = exe;
        SettingsService.Save(App.Settings);
        _encoder = null;
        Raise();
        return true;
    }

    public static void ClearCustom()
    {
        App.Settings.FfmpegPath = null;
        SettingsService.Save(App.Settings);
        _encoder = null;
        Raise();
    }

    // ---------- Install ----------

    /// <summary>Downloads and unpacks FFmpeg. Only one install runs at a time; progress comes through <see cref="StateChanged"/>.</summary>
    public static async Task<bool> InstallAsync()
    {
        if (_install != null) return false;
        var cts = _install = new CancellationTokenSource();
        InstallProgress = null;
        InstallStage = "Connecting...";
        Raise();
        Exception? last = null;
        try
        {
            foreach (var url in DownloadUrls)
            {
                try
                {
                    await DownloadAndExtractAsync(url, cts.Token);
                    _encoder = null;
                    InstallStage = "Ready";
                    return true;
                }
                catch (OperationCanceledException)
                {
                    InstallStage = "Cancelled";
                    return false;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Error(ex, $"FFmpeg download from {url}");
                }
            }
            InstallStage = "Download failed. Check your connection and try again." + (last != null ? $" ({last.Message})" : "");
            return false;
        }
        finally
        {
            _install = null;
            cts.Dispose();
            Raise();
        }
    }

    public static void CancelInstall() => _install?.Cancel();

    private static async Task DownloadAndExtractAsync(string url, CancellationToken ct)
    {
        string zip = Path.Combine(AppPaths.TempDir, $"ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Lumina");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(zip);
                var buffer = new byte[1 << 16];
                long done = 0;
                var tick = Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (tick.ElapsedMilliseconds > 120)
                    {
                        tick.Restart();
                        InstallProgress = total > 0 ? (double)done / total.Value : null;
                        InstallStage = total > 0
                            ? $"Downloading... {done / 1048576} of {total.Value / 1048576} MB"
                            : $"Downloading... {done / 1048576} MB";
                        Raise();
                    }
                }
            }

            InstallProgress = 1;
            InstallStage = "Unpacking...";
            Raise();
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.Entries.FirstOrDefault(e =>
                                e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The download didn't contain ffmpeg.exe.");
                Directory.CreateDirectory(InstallDir);
                string tmp = InstalledPath + ".tmp";
                entry.ExtractToFile(tmp, true);
                File.Move(tmp, InstalledPath, true);
            }, ct);

            if (await VerifyAsync(InstalledPath) == null)
            {
                File.Delete(InstalledPath);
                throw new InvalidDataException("The downloaded ffmpeg.exe doesn't run.");
            }
        }
        finally
        {
            try { File.Delete(zip); } catch { /* still locked; temp gets cleaned eventually */ }
        }
    }

    private static void Raise()
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) StateChanged?.Invoke();
        else app.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }

    // ---------- Encoding ----------

    /// <summary>
    /// The H.264 encoder to record with: the GPU's (NVIDIA, Intel, AMD) when it works, otherwise x264.
    /// Probed once per FFmpeg install, in the background.
    /// </summary>
    public static Task<string> PickEncoderAsync() => _encoder ??= ProbeEncoderAsync();

    private static async Task<string> ProbeEncoderAsync()
    {
        var exe = Resolve();
        if (exe == null) return "libx264";
        foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            try
            {
                var result = await RunAsync(exe,
                    $"-hide_banner -loglevel error -f lavfi -i color=black:s=640x360:d=0.2 -c:v {encoder} -pix_fmt yuv420p -f null -",
                    TimeSpan.FromSeconds(8));
                if (result.ExitCode == 0)
                {
                    Log.Info($"Recording encoder: {encoder}");
                    return encoder;
                }
            }
            catch
            {
                // Not available on this machine.
            }
        }
        Log.Info("Recording encoder: libx264");
        return "libx264";
    }

    /// <summary>Encoder options for a screen recording at good quality.</summary>
    public static string EncoderArgs(string encoder) => encoder switch
    {
        "h264_nvenc" => "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0",
        "h264_qsv" => "-c:v h264_qsv -preset medium -global_quality 23",
        "h264_amf" => "-c:v h264_amf -quality balanced -rc cqp -qp_i 22 -qp_p 22",
        _ => "-c:v libx264 -preset veryfast -crf 21 -tune zerolatency",
    };

    /// <summary>
    /// Converts to 4:2:0 with the BT.709 matrix and tags the stream to match. Left alone, FFmpeg converts with
    /// BT.601 and writes no tags, while players decode HD as BT.709, so colours come out shifted and dull.
    /// Untagged input (recordings made before this) is read as BT.601, so re-encoding those corrects them too.
    /// The tags go on the frames (setparams) because encoders take them from there, not from -color_* options.
    /// </summary>
    public const string Yuv709Args =
        "-vf \"scale=out_color_matrix=bt709:out_range=tv,format=yuv420p," +
        "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709\"";

    // ---------- Running ----------

    public sealed record RunResult(int ExitCode, string Output, string Error);

    /// <summary>Starts ffmpeg with no window. Callers own the process.</summary>
    public static Process Start(string args, bool redirectInput = false, bool redirectOutput = false)
    {
        var exe = Resolve() ?? throw new FileNotFoundException("FFmpeg isn't installed.");
        return Start(exe, args, redirectInput, redirectOutput);
    }

    private static Process Start(string exe, string args, bool redirectInput, bool redirectOutput)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (redirectOutput) psi.StandardOutputEncoding = Encoding.UTF8;
        return Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
    }

    private static async Task<RunResult> RunAsync(string exe, string args, TimeSpan timeout)
    {
        using var p = Start(exe, args, false, true);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw new TimeoutException("FFmpeg took too long.");
        }
        return new RunResult(p.ExitCode, await output, await error);
    }

    /// <summary>
    /// Runs a job (trim, export...) reporting progress as a 0 to 1 fraction of <paramref name="duration"/>.
    /// Throws with FFmpeg's own error text if it fails.
    /// </summary>
    public static async Task RunJobAsync(string args, double duration, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var p = Start($"-hide_banner -loglevel error -nostats -progress pipe:1 -y {args}", redirectInput: false, redirectOutput: true);
        var error = p.StandardError.ReadToEndAsync(ct);
        using var reg = ct.Register(() =>
        {
            try { p.Kill(true); } catch { /* already gone */ }
        });
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
        {
            if (progress == null || duration <= 0 || !line.StartsWith("out_time_us=")) continue;
            if (long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                progress.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
        }
        await p.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new InvalidOperationException(Tail(await error));
    }

    /// <summary>The last few lines of FFmpeg's error output, for messages.</summary>
    public static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "FFmpeg stopped unexpectedly." : string.Join("\n", lines.TakeLast(3));
    }

    // ---------- Audio tracks ----------

    /// <summary>Names (MP4 handler names) of the two source tracks a recording with both system audio and microphone carries after its mix.</summary>
    public const string SystemTrackTitle = "System audio", MicTrackTitle = "Microphone";

    /// <summary>Output options that label audio tracks 0 to 2 as the mix and its two sources, with only the mix played by default.</summary>
    public static string SourceTrackArgs =>
        $"-metadata:s:a:0 handler_name=\"System audio + microphone\" -metadata:s:a:1 handler_name=\"{SystemTrackTitle}\" -metadata:s:a:2 handler_name=\"{MicTrackTitle}\"" +
        " -disposition:a:0 default -disposition:a:1 0 -disposition:a:2 0";

    /// <summary>
    /// Whether a recording carries its system audio and microphone as separate tracks (after the mix), so each can be
    /// muted on its own. Recordings from before 0.5, or with only one source, have just one track.
    /// </summary>
    public static async Task<bool> HasSourceTracksAsync(string path)
    {
        var titles = new List<string?>();
        try
        {
            // With no output, ffmpeg just describes the input (on stderr) and exits.
            using var p = Start($"-hide_banner -i {Quote(path)}");
            string info = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            bool inAudio = false;
            foreach (var raw in info.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Stream #"))
                {
                    inAudio = line.Contains(": Audio:");
                    if (inAudio) titles.Add(null);
                }
                else if (inAudio && line.StartsWith("handler_name") && line.IndexOf(':') is int colon and > 0)
                {
                    titles[^1] = line[(colon + 1)..].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading a recording's audio tracks");
            return false;
        }
        return titles.Count == 3 && titles[1] == SystemTrackTitle && titles[2] == MicTrackTitle;
    }

    public static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Quote(string path) => "\"" + path + "\"";
}
