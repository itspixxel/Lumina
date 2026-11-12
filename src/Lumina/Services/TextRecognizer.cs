using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

// The WinRT APIs here are marked as Windows 10+; .NET 10 itself needs Windows 10, so every PC running Lumina has them.
#pragma warning disable CA1416

namespace Lumina.Services;

/// <summary>
/// Reads the text in a screenshot with the OCR engine built into Windows. Runs entirely on this PC, in the
/// languages installed in Windows; when none of them supports OCR, <see cref="IsAvailable"/> is false.
/// </summary>
public static class TextRecognizer
{
    private static readonly Lazy<OcrEngine?> Engine = new(() =>
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting Windows OCR");
            return null;
        }
    });

    public static bool IsAvailable => Engine.Value != null;

    /// <summary>The language text is read in, e.g. "English (United States)"; null when OCR isn't available.</summary>
    public static string? LanguageName => Engine.Value?.RecognizerLanguage.DisplayName;

    /// <summary>
    /// The text in an image, one line per line of text and "" when there is none. Null when the file can't be
    /// read right now (missing, locked or still being written) or OCR isn't available.
    /// </summary>
    public static async Task<string?> ReadAsync(string path)
    {
        if (Engine.Value is not { } engine) return null;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        // The engine misses a lot of UI-sized text at screen resolution (a whole window title bar and menu can
        // come back empty), so read at twice the size, within the engine's size limit. On real screenshots that
        // finds about a quarter more words; going larger finds no more.
        uint max = OcrEngine.MaxImageDimension, w = decoder.PixelWidth, h = decoder.PixelHeight;
        double scale = Math.Min(2.0, (double)max / Math.Max(1, Math.Max(w, h)));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)(w * scale)),
            ScaledHeight = Math.Max(1, (uint)(h * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        var result = await engine.RecognizeAsync(bitmap);
        var text = new StringBuilder();
        foreach (var line in result.Lines)
        {
            string words = string.Join(' ', line.Words.Select(word => word.Text));
            if (words.Length == 0) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(words);
        }
        return text.ToString();
    }
}
