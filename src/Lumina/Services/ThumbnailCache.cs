using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Lumina.Recording;

namespace Lumina.Services;

/// <summary>
/// Gallery thumbnails. Large screenshots and every recording get a small JPEG kept in temp (keyed on path, size
/// and modified time, so an edited file gets a fresh one); recently shown thumbnails stay in memory.
/// </summary>
public static class ThumbnailCache
{
    public const int Width = 360;
    /// <summary>Screenshots wider than this are cached on disk; smaller ones decode about as fast as a JPEG.</summary>
    private const int DiskCacheMinWidth = 720;
    private const int MemoryLimit = 400;

    private static readonly SemaphoreSlim DecodeSlots = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
    private static readonly SemaphoreSlim FfmpegSlots = new(3);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> Memory = new();
    private static readonly LinkedList<(string Key, BitmapSource Image)> Recent = new();
    private static readonly Dictionary<string, Task<BitmapSource?>> Loading = new();

    private static string Dir => Path.Combine(AppPaths.TempDir, "thumbs");

    private static string KeyOf(LibraryEntry e) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{e.Path.ToLowerInvariant()}|{e.Size}|{e.ModifiedUtc.Ticks}")))[..16];

    /// <summary>The thumbnail if it's already in memory, so a tile can show it without a flash.</summary>
    public static BitmapSource? Peek(LibraryEntry entry)
    {
        lock (Gate)
        {
            if (!Memory.TryGetValue(KeyOf(entry), out var node)) return null;
            Recent.Remove(node);
            Recent.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>Loads (or makes) a thumbnail off the UI thread. Null when the file can't be read.</summary>
    public static Task<BitmapSource?> GetAsync(LibraryEntry entry)
    {
        string key = KeyOf(entry);
        lock (Gate)
        {
            if (Memory.TryGetValue(key, out var node)) return Task.FromResult<BitmapSource?>(node.Value.Image);
            if (Loading.TryGetValue(key, out var pending)) return pending;
            var task = Task.Run(() => LoadAsync(entry, key));
            Loading[key] = task;
            task.ContinueWith(t =>
            {
                lock (Gate)
                {
                    Loading.Remove(key);
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is { } image) Remember(key, image);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    private static void Remember(string key, BitmapSource image)
    {
        if (Memory.ContainsKey(key)) return;
        Memory[key] = Recent.AddFirst((key, image));
        while (Recent.Count > MemoryLimit)
        {
            Memory.Remove(Recent.Last!.Value.Key);
            Recent.RemoveLast();
        }
    }

    private static async Task<BitmapSource?> LoadAsync(LibraryEntry entry, string key)
    {
        string cached = Path.Combine(Dir, key + ".jpg");
        if (File.Exists(cached))
        {
            Touch(cached);
            return Decode(cached);
        }

        if (entry.IsVideo)
        {
            if (!Ffmpeg.IsAvailable) return null;
            await FfmpegSlots.WaitAsync();
            try
            {
                Directory.CreateDirectory(Dir);
                await Ffmpeg.RunJobAsync($"-i {Ffmpeg.Quote(entry.Path)} -frames:v 1 -vf scale={Width}:-2 -q:v 4 {Ffmpeg.Quote(cached)}", 0, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Thumbnail for {entry.Name}");
            }
            finally
            {
                FfmpegSlots.Release();
            }
            return File.Exists(cached) ? Decode(cached) : null;
        }

        await DecodeSlots.WaitAsync();
        try
        {
            var thumb = Decode(entry.Path);
            if (thumb != null && entry.Width > DiskCacheMinWidth) Save(thumb, cached);
            return thumb;
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Not IgnoreImageCache: with a stream source (no Uri) WPF throws on it. The stream also never hits that cache.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = Width;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Still being written, or not a readable image; skip the thumbnail.
            return null;
        }
    }

    private static void Save(BitmapSource thumb, string path)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(thumb));
            string tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Caching thumbnail");
        }
    }

    /// <summary>Marks a cached thumbnail as still in use (last-access times are often switched off in Windows).</summary>
    private static void Touch(string path)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // Only affects pruning.
        }
    }

    /// <summary>Clears out thumbnails nobody has needed for a month (their files were deleted or changed).</summary>
    public static void PruneInBackground() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var file in new DirectoryInfo(Dir).EnumerateFiles())
                if (file.LastWriteTimeUtc < cutoff) file.Delete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pruning thumbnails");
        }
    });
}
