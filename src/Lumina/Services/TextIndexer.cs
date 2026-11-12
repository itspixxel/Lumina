using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Lumina.Services;

/// <summary>
/// Reads the text in every screenshot in the background, newest first, so the gallery can search it. Each file
/// is read once (and again only if it changes); the results live in the library index. Pauses while a capture
/// or recording is in progress, and picks up where it left off on the next launch.
/// </summary>
public static class TextIndexer
{
    private static readonly SemaphoreSlim Wake = new(0, 1);
    private static int _started;
    private static int _remaining;

    /// <summary>Raised on the UI thread, at most every couple of seconds, after new text has been read.</summary>
    public static event Action? TextRead;

    /// <summary>Screenshots still waiting to be read.</summary>
    public static int Remaining => _remaining;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        LibraryIndex.Changed += Signal; // new, changed or renamed files
        new Thread(() => RunAsync().GetAwaiter().GetResult())
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Lumina.TextIndexer",
        }.Start();
    }

    private static void Signal()
    {
        try
        {
            Wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already due to run again.
        }
    }

    private static async Task RunAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5)); // stay out of the way while the app starts
        if (!TextRecognizer.IsAvailable)
        {
            Log.Info("Windows OCR isn't available (no OCR language installed); screenshot text won't be searchable");
            return;
        }
        Log.Info($"Reading screenshot text in {TextRecognizer.LanguageName}");
        while (true)
        {
            try
            {
                await ReadPendingAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reading text in screenshots");
            }
            await Wake.WaitAsync();
        }
    }

    private static async Task ReadPendingAsync()
    {
        var pending = (await LibraryIndex.ScanAsync())
            .Where(e => !e.IsVideo && e.OcrText == null && e.Width > 0)
            .ToList(); // newest first
        _remaining = pending.Count;
        if (pending.Count == 0) return;

        var sinceSave = Stopwatch.StartNew();
        var sinceNotify = Stopwatch.StartNew();
        bool found = false;
        foreach (var entry in pending)
        {
            // Files changed (a new capture, most likely): start over so the newest are read first.
            if (Wake.CurrentCount > 0) break;
            while (IsBusy()) await Task.Delay(1000);

            string? text;
            try
            {
                text = await TextRecognizer.ReadAsync(entry.Path);
            }
            catch (Exception ex)
            {
                // Not an image the decoder understands; don't try it again.
                Log.Error(ex, $"Reading text in {entry.Name}");
                text = "";
            }
            Interlocked.Decrement(ref _remaining);
            if (text == null) continue; // gone or locked; it comes back when the file changes, or next launch
            LibraryIndex.SetText(entry, text);
            found |= text.Length > 0;

            if (sinceSave.Elapsed > TimeSpan.FromSeconds(10))
            {
                LibraryIndex.Save();
                sinceSave.Restart();
            }
            if (found && sinceNotify.Elapsed > TimeSpan.FromSeconds(2))
            {
                Notify();
                found = false;
                sinceNotify.Restart();
            }
        }
        LibraryIndex.Save();
        Notify();
    }

    /// <summary>A capture or recording is in progress: leave the CPU to it.</summary>
    private static bool IsBusy()
    {
        var app = Application.Current as App;
        return app != null && (app.Controller.IsBusy || app.Recorder.IsBusy || app.Recorder.IsRecording);
    }

    private static void Notify() => Application.Current?.Dispatcher.BeginInvoke(() => TextRead?.Invoke());
}
