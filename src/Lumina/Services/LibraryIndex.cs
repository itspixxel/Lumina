using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Lumina.Editor;

namespace Lumina.Services;

public enum GalleryFilter { All, Screenshots, Recordings, Edited }

public enum GallerySort { Newest, Oldest, Name, Largest }

/// <summary>What the gallery knows about one screenshot or recording.</summary>
public sealed class LibraryEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public bool IsVideo { get; set; }
    /// <summary>Pixel size of a screenshot; 0 when unknown (recordings, unreadable files).</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The app that was captured, recorded at save time. Survives edits and renames.</summary>
    public string? App { get; set; }
    /// <summary>Text typed into the capture's markup, so it can be searched.</summary>
    public string? MarkupText { get; set; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public DateTime Modified => ModifiedUtc.ToLocalTime();
    [JsonIgnore] public bool IsEdited => MarkupText != null;
}

/// <summary>
/// Every screenshot and recording in Lumina's two output folders, with details cached in library.json so a
/// refresh only re-reads files that changed. Watches both folders and raises <see cref="Changed"/> when
/// files are added, removed or rewritten, by Lumina or anything else.
/// </summary>
public static class LibraryIndex
{
    private sealed class CacheFile
    {
        public int Version { get; set; }
        public List<LibraryEntry> Entries { get; set; } = new();
        /// <summary>Source apps for files Lumina saved but hasn't scanned yet.</summary>
        public Dictionary<string, string> PendingApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private const int CacheVersion = 1;
    private static string CachePath => System.IO.Path.Combine(AppPaths.DataDir, "library.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static Dictionary<string, LibraryEntry>? _entries;
    private static Dictionary<string, string> _pendingApps = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dirty;

    private static readonly List<FileSystemWatcher> Watchers = new();
    private static string[] _watched = Array.Empty<string>();
    private static Timer? _debounce;

    /// <summary>Raised on the UI thread, shortly after files in either folder change.</summary>
    public static event Action? Changed;

    /// <summary>Lumina's folders, de-duplicated (screenshots and recordings can share one).</summary>
    public static string[] Folders =>
        new[] { App.Settings.OutputDirectory, App.Settings.RecordingsDirectory }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => System.IO.Path.GetFullPath(d).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>All files, newest first. Unchanged files come from the cache; new or changed ones are read.</summary>
    public static async Task<List<LibraryEntry>> ScanAsync()
    {
        string imageDir = App.Settings.OutputDirectory, videoDir = App.Settings.RecordingsDirectory;
        EnsureWatching();
        await ScanGate.WaitAsync();
        try
        {
            return await Task.Run(() => Scan(imageDir, videoDir));
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private static List<LibraryEntry> Scan(string imageDir, string videoDir)
    {
        LoadCache();
        static IEnumerable<FileInfo> Files(string dir, Func<string, bool> match)
        {
            try
            {
                return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles().Where(f => match(f.Name)) : Enumerable.Empty<FileInfo>();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Listing {dir}");
                return Enumerable.Empty<FileInfo>();
            }
        }

        var files = Files(imageDir, OutputService.IsImageFile)
            .Concat(Files(videoDir, OutputService.IsVideoFile))
            .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<LibraryEntry>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            seen.Add(file.FullName);
            LibraryEntry? cached;
            lock (Gate) _entries!.TryGetValue(file.FullName, out cached);
            if (cached != null && cached.Size == file.Length && cached.ModifiedUtc == file.LastWriteTimeUtc)
            {
                result.Add(cached);
                continue;
            }
            var entry = Read(file, cached);
            lock (Gate)
            {
                _entries![file.FullName] = entry;
                _dirty = true;
            }
            result.Add(entry);
        }

        lock (Gate)
        {
            // Forget files that are gone, but only from folders we just listed (a missing folder keeps its entries
            // in case it's a drive that's unplugged for now).
            var gone = _entries!.Keys.Where(p => !seen.Contains(p) && InFolder(p, imageDir, videoDir)).ToList();
            foreach (var path in gone) _entries.Remove(path);
            if (gone.Count > 0) _dirty = true;
        }
        SaveCache();
        return result.OrderByDescending(e => e.ModifiedUtc).ToList();
    }

    private static bool InFolder(string path, params string[] dirs) =>
        dirs.Any(d => Directory.Exists(d) && string.Equals(System.IO.Path.GetDirectoryName(path)?.TrimEnd('\\'),
            System.IO.Path.GetFullPath(d).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a new or changed file's details, keeping what can't be re-derived (its source app).</summary>
    private static LibraryEntry Read(FileInfo file, LibraryEntry? previous)
    {
        var entry = new LibraryEntry
        {
            Path = file.FullName,
            Size = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            IsVideo = OutputService.IsVideoFile(file.Name),
            App = previous?.App,
        };
        lock (Gate)
        {
            if (_pendingApps.Remove(file.FullName, out var app))
            {
                entry.App = app;
                _dirty = true;
            }
        }
        if (!entry.IsVideo)
        {
            try
            {
                // Reads just the header: no pixels are decoded.
                using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                entry.Width = frame.PixelWidth;
                entry.Height = frame.PixelHeight;
            }
            catch
            {
                // Still being written, or not a readable image.
            }
            entry.MarkupText = ProjectStore.ReadText(file.FullName);
        }
        return entry;
    }

    /// <summary>Remembers which app a file Lumina is saving came from.</summary>
    public static void SetApp(string path, string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return;
        path = System.IO.Path.GetFullPath(path);
        LoadCache();
        lock (Gate)
        {
            if (_entries!.TryGetValue(path, out var entry)) entry.App = app;
            else _pendingApps[path] = app;
            _dirty = true;
        }
        SaveCache();
    }

    /// <summary>Carries a file's details over when Lumina renames it.</summary>
    public static void Move(string from, string to)
    {
        from = System.IO.Path.GetFullPath(from);
        to = System.IO.Path.GetFullPath(to);
        LoadCache();
        lock (Gate)
        {
            if (_entries!.Remove(from, out var entry))
            {
                entry.Path = to;
                _entries[to] = entry;
                _dirty = true;
            }
        }
        SaveCache();
    }

    /// <summary>Forces a file to be re-read on the next scan (its markup changed without the file changing).</summary>
    public static void Invalidate(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        LoadCache();
        lock (Gate)
        {
            if (_entries!.TryGetValue(path, out var entry)) entry.Size = -1;
        }
    }

    // ---------- Cache file ----------

    private static void LoadCache()
    {
        lock (Gate)
        {
            if (_entries != null) return;
            _entries = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CachePath)) return;
                var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath), JsonOptions);
                if (cache == null || cache.Version != CacheVersion) return;
                foreach (var e in cache.Entries) _entries[e.Path] = e;
                _pendingApps = new(cache.PendingApps, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Loading library cache; rebuilding it");
            }
        }
    }

    private static void SaveCache()
    {
        string json;
        lock (Gate)
        {
            if (!_dirty || _entries == null) return;
            json = JsonSerializer.Serialize(new CacheFile
            {
                Version = CacheVersion,
                Entries = _entries.Values.ToList(),
                PendingApps = _pendingApps,
            }, JsonOptions);
            _dirty = false;
        }
        try
        {
            string tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving library cache");
        }
    }

    // ---------- Watching ----------

    /// <summary>Watches the current folders, re-pointing the watchers when they change in Settings.</summary>
    private static void EnsureWatching()
    {
        var folders = Folders.Where(Directory.Exists).ToArray();
        lock (Watchers)
        {
            if (folders.SequenceEqual(_watched, StringComparer.OrdinalIgnoreCase)) return;
            foreach (var w in Watchers) w.Dispose();
            Watchers.Clear();
            foreach (var dir in folders)
            {
                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                    };
                    watcher.Created += OnFileEvent;
                    watcher.Changed += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += OnFileEvent;
                    watcher.EnableRaisingEvents = true;
                    Watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Watching {dir}");
                }
            }
            _watched = folders;
        }
    }

    private static void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        bool relevant = OutputService.IsImageFile(e.Name ?? "") || OutputService.IsVideoFile(e.Name ?? "")
                        || e is RenamedEventArgs r && (OutputService.IsImageFile(r.OldName ?? "") || OutputService.IsVideoFile(r.OldName ?? ""));
        if (!relevant) return;
        // Saves arrive as bursts of events (temp file, rename, size updates); wait for the burst to settle.
        lock (Watchers)
        {
            _debounce ??= new Timer(_ => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke()));
            _debounce.Change(400, Timeout.Infinite);
        }
    }
}
