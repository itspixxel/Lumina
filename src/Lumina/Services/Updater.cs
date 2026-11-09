using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lumina.Services;

public enum UpdateState { Idle, Checking, UpToDate, Downloading, Ready, Failed }

/// <summary>
/// Keeps Lumina current from its GitHub releases. A release ships as a zip holding a single Lumina.exe, so updating
/// means swapping that one file: Windows lets a running exe be renamed, so the new one is put in place straight away
/// (the old one becomes Lumina.exe.old and is deleted on the next run) and Lumina restarts into it once it's idle.
/// </summary>
public static class Updater
{
    private const string Repo = "itspixxel/Lumina";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(1);

    private static DispatcherTimer? _timer;
    private static bool _working;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or <see cref="Status"/> changes.</summary>
    public static event Action? StateChanged;

    public static UpdateState State { get; private set; }
    /// <summary>A line for Settings describing the last check, download or failure.</summary>
    public static string Status { get; private set; } = "";
    /// <summary>The version waiting in Lumina.exe for a restart, once <see cref="State"/> is Ready.</summary>
    public static Version? ReadyVersion { get; private set; }
    /// <summary>Lumina's folder isn't writable, so new versions have to be downloaded by hand.</summary>
    public static bool NeedsManualDownload { get; private set; }

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Which zip this build came from: the self-contained one, or the small one that needs the .NET runtime.</summary>
    private static bool SelfContained =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SelfContained")?.Value is not ("false" or "False");

    /// <summary>Read once at startup: after the swap, this process's own file is the .old one.</summary>
    private static readonly string? ExePath = Environment.ProcessPath;
    private static string? OldExePath => ExePath + ".old";

    /// <summary>Starts the background checks. Development builds never update themselves.</summary>
    public static void Start(bool justUpdated)
    {
#if DEBUG
        return;
#else
        if (justUpdated) AnnounceUpdated();
        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += (_, _) => _ = TickAsync();
        _timer.Start();
#endif
    }

    private static async Task TickAsync()
    {
        _timer!.Interval = State == UpdateState.Ready ? IdlePoll : CheckInterval;
        DeleteOldExe();
        if (State == UpdateState.Ready)
        {
            if (IsIdle()) Restart();
            return;
        }
        if (App.Settings.AutoUpdate) await CheckAsync(install: true);
    }

    /// <summary>
    /// Looks for a newer release and, when <paramref name="install"/> is set, downloads it and puts it in place.
    /// Returns true when a newer version was found.
    /// </summary>
    public static async Task<bool> CheckAsync(bool install)
    {
        if (_working || State == UpdateState.Ready) return State == UpdateState.Ready;
        _working = true;
        Set(UpdateState.Checking, "Checking for updates...");
        try
        {
            using var http = CreateClient();
            var release = await FindLatestAsync(http);
            if (release == null || release.Version <= Current)
            {
                Set(UpdateState.UpToDate, $"Up to date, checked {DateTime.Now:t}");
                return false;
            }
            Log.Info($"Update available: {release.Version} ({release.AssetName})");
            if (!install)
            {
                Set(UpdateState.Idle, $"Lumina {release.Version.ToString(3)} is available");
                return true;
            }
            await InstallAsync(http, release);
            ReadyVersion = release.Version;
            Set(UpdateState.Ready, $"Lumina {release.Version.ToString(3)} is ready, restart to finish");
            if (_timer != null) _timer.Interval = IdlePoll;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update check");
            NeedsManualDownload = ex is UnauthorizedAccessException;
            Set(UpdateState.Failed, ex is UnauthorizedAccessException
                ? "Lumina can't write to its own folder, so it can't update itself. Download new versions from GitHub."
                : "Couldn't check for updates. Lumina will try again later.");
            return false;
        }
        finally
        {
            _working = false;
        }
    }

    // ---------- Finding a release ----------

    private sealed record Release(Version Version, string AssetName, string AssetUrl, string? Sha256);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lumina/{Current.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The newest published release (pre-releases included, as every build so far is an alpha) with a zip for this flavour.</summary>
    private static async Task<Release?> FindLatestAsync(HttpClient http)
    {
        await using var stream = await http.GetStreamAsync($"https://api.github.com/repos/{Repo}/releases?per_page=15");
        using var doc = await JsonDocument.ParseAsync(stream);
        string suffix = SelfContained ? "-win-x64.zip" : "-win-x64-framework.zip";
        Release? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            if (ParseTag(r.GetProperty("tag_name").GetString()) is not { } version) continue;
            if (best != null && version <= best.Version) continue;
            foreach (var asset in r.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                best = new Release(version, name, asset.GetProperty("browser_download_url").GetString()!,
                    digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null);
                break;
            }
        }
        return best;
    }

    /// <summary>Turns "v0.4.0-alpha" into 0.4.0.</summary>
    private static Version? ParseTag(string? tag)
    {
        if (tag == null) return null;
        string core = tag.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    // ---------- Installing ----------

    private static async Task InstallAsync(HttpClient http, Release release)
    {
        string exe = ExePath ?? throw new InvalidOperationException("Lumina's own path is unknown.");
        string dir = Path.GetDirectoryName(exe)!;
        string zip = Path.Combine(AppPaths.TempDir, $"update-{Guid.NewGuid():N}.zip");
        string staged = Path.Combine(dir, "Lumina.exe.new");
        try
        {
            // Fail early (and say why) if the folder isn't writable, e.g. under Program Files.
            File.WriteAllBytes(staged, Array.Empty<byte>());

            Set(UpdateState.Downloading, $"Downloading Lumina {release.Version.ToString(3)}...");
            using (var response = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(zip);
                await source.CopyToAsync(target);
            }

            await Task.Run(() =>
            {
                if (release.Sha256 != null)
                {
                    using var file = File.OpenRead(zip);
                    string hash = Convert.ToHexString(SHA256.HashData(file));
                    if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The download didn't match its checksum.");
                }
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("Lumina.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The download didn't contain Lumina.exe.");
                entry.ExtractToFile(staged, true);
            });

            var info = FileVersionInfo.GetVersionInfo(staged);
            if (!Version.TryParse(info.FileVersion, out var fileVersion) || Normalize(fileVersion) <= Current)
                throw new InvalidDataException($"The downloaded Lumina.exe isn't newer ({info.FileVersion}).");

            // Swap: the running exe can be renamed but not overwritten.
            DeleteOldExe();
            File.Move(exe, OldExePath!, true);
            try
            {
                File.Move(staged, exe);
            }
            catch
            {
                File.Move(OldExePath!, exe); // put things back so Lumina still starts
                throw;
            }
            Log.Info($"Installed Lumina {release.Version}; waiting to restart");
        }
        finally
        {
            try { File.Delete(zip); } catch { /* temp gets cleaned eventually */ }
            try { File.Delete(staged); } catch { /* moved, or never created */ }
        }
    }

    private static void DeleteOldExe()
    {
        try
        {
            if (OldExePath is { } old && File.Exists(old)) File.Delete(old);
        }
        catch
        {
            // The previous process may still be exiting; next time.
        }
    }

    // ---------- Restarting ----------

    /// <summary>Nothing on screen and nothing in progress, so a restart won't interrupt anyone.</summary>
    private static bool IsIdle() =>
        !App.Instance.Recorder.IsRecording && !App.Instance.Recorder.IsBusy && !App.Instance.Controller.IsBusy
        && !Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible);

    /// <summary>Starts the new Lumina.exe and quits. It waits for this instance to exit before taking over.</summary>
    public static void Restart()
    {
        if (State != UpdateState.Ready || ExePath is not { } exe) return;
        bool home = Application.Current.Windows.OfType<Views.HomeWindow>().Any(w => w.IsVisible);
        try
        {
            Process.Start(new ProcessStartInfo(exe, home ? "--updated" : "--background --updated") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restarting after update");
            return;
        }
        App.Quit();
    }

    private static void AnnounceUpdated()
    {
        Log.Info($"Updated to {Current}");
        App.Tray?.Notify($"Lumina updated to {Current.ToString(3)}", "Click to see what's new.", () => OpenPage(ReleasesPage));
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "Open release page"); }
    }

    private static void Set(UpdateState state, string status)
    {
        State = state;
        Status = status;
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) StateChanged?.Invoke();
        else app.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }
}
