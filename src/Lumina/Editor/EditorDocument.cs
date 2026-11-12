using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Services;

namespace Lumina.Editor;

public sealed record DocumentState(List<Annotation> Annotations, Int32Rect? Crop);

/// <summary>Shared rendering helpers: caches of pixelated and blurred regions of the original image.</summary>
public sealed class RenderContext
{
    private readonly Dictionary<(Int32Rect, int, bool), BitmapSource> _cache = new();
    private BitmapSource? _blurredFull;

    public RenderContext(BitmapSource source) => Source = source;

    public BitmapSource Source { get; }

    public Int32Rect ClampToImage(Rect r)
    {
        int x1 = (int)Math.Max(0, Math.Floor(r.X)), y1 = (int)Math.Max(0, Math.Floor(r.Y));
        int x2 = (int)Math.Min(Source.PixelWidth, Math.Ceiling(r.Right)), y2 = (int)Math.Min(Source.PixelHeight, Math.Ceiling(r.Bottom));
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    /// <summary>Block size in image pixels for a mosaic; strength 0 picks one from the region size.</summary>
    public static int MosaicBlock(Int32Rect r, double strength) => strength <= 0
        // Big blocks: small ones can leave text partly legible.
        ? Math.Clamp(Math.Min(r.Width, r.Height) / 5, 8, 40)
        : (int)Math.Round(4 + (strength - 1) * 5);

    public static int BlurRadius(Int32Rect r, double strength) => strength <= 0
        ? Math.Clamp(Math.Min(r.Width, r.Height) / 8, 6, 24)
        : (int)Math.Round(3 + (strength - 1) * 4);

    public BitmapSource GetPixelated(Int32Rect r, double strength)
    {
        int block = MosaicBlock(r, strength);
        return Cached((r, block, false), () =>
        {
            int w = Math.Max(1, (int)Math.Ceiling(r.Width / (double)block));
            int h = Math.Max(1, (int)Math.Ceiling(r.Height / (double)block));
            return new TransformedBitmap(new CroppedBitmap(Source, r), new ScaleTransform(w / (double)r.Width, h / (double)r.Height));
        });
    }

    /// <summary>A Gaussian-like blur of the region, computed at reduced size and drawn back up smoothly.</summary>
    public BitmapSource GetBlurred(Int32Rect r, double strength)
    {
        int radius = BlurRadius(r, strength);
        return Cached((r, radius, true), () => Blur(new CroppedBitmap(Source, r), radius));
    }

    /// <summary>The whole image, softly blurred (spotlight backgrounds).</summary>
    public BitmapSource GetBlurredFull() => _blurredFull ??= Freeze(Blur(Source, 12));

    private BitmapSource Cached((Int32Rect, int, bool) key, Func<BitmapSource> make)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (_cache.Count > 48) _cache.Clear();
        var result = make();
        result.Freeze();
        _cache[key] = result;
        return result;
    }

    private static BitmapSource Blur(BitmapSource source, int radius)
    {
        // Work on a downscaled copy: a blur this wide doesn't need full resolution, and it keeps it fast.
        double down = Math.Max(1, radius / 3.0);
        int w = Math.Max(1, (int)Math.Round(source.PixelWidth / down)), h = Math.Max(1, (int)Math.Round(source.PixelHeight / down));
        BitmapSource small = new TransformedBitmap(source, new ScaleTransform(w / (double)source.PixelWidth, h / (double)source.PixelHeight));
        small = new FormatConvertedBitmap(small, PixelFormats.Pbgra32, null, 0);
        w = small.PixelWidth;
        h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);
        int r = Math.Max(1, (int)Math.Round(radius / down));
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 3; pass++) // three box blurs ≈ Gaussian
        {
            BoxBlur(px, tmp, w, h, r, horizontal: true);
            BoxBlur(tmp, px, w, h, r, horizontal: false);
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
    }

    /// <summary>Sliding-window box blur along one axis; edges clamp, so nothing bleeds in from outside.</summary>
    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4, lineStep = horizontal ? w * 4 : 4;
        double norm = 1.0 / (2 * r + 1);
        for (int line = 0; line < lines; line++)
        {
            int origin = line * lineStep;
            for (int c = 0; c < 4; c++)
            {
                int Index(int i) => origin + Math.Clamp(i, 0, length - 1) * step + c;
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += src[Index(i)];
                for (int i = 0; i < length; i++)
                {
                    dst[origin + i * step + c] = (byte)(sum * norm + 0.5);
                    sum += src[Index(i + r + 1)] - src[Index(i - r)];
                }
            }
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

public sealed class EditorDocument
{
    public EditorDocument(BitmapSource original)
    {
        Original = original;
        Context = new RenderContext(original);
    }

    public BitmapSource Original { get; }
    public RenderContext Context { get; }
    public List<Annotation> Annotations { get; private set; } = new();
    public Int32Rect? Crop { get; set; }

    public int PixelWidth => Original.PixelWidth;
    public int PixelHeight => Original.PixelHeight;
    public Int32Rect EffectiveCrop => Crop ?? new Int32Rect(0, 0, PixelWidth, PixelHeight);
    public bool IsPristine => Annotations.Count == 0 && Crop == null;

    public DocumentState Snapshot() => new(Annotations.Select(a => a.Clone()).ToList(), Crop);

    public void Restore(DocumentState state)
    {
        Annotations = state.Annotations.Select(a => a.Clone()).ToList();
        Crop = state.Crop;
    }

    /// <summary>Draws the image and all annotations in image pixel coordinates.</summary>
    public void DrawContent(DrawingContext dc)
    {
        var full = new Rect(0, 0, PixelWidth, PixelHeight);
        dc.DrawImage(Original, full);

        foreach (var p in Annotations.OfType<PixelateAnnotation>()) p.Render(dc, Context);

        var spots = Annotations.OfType<SpotlightAnnotation>().Where(s => s.W >= 2 && s.H >= 2).ToList();
        if (spots.Count > 0)
        {
            Geometry outside = new RectangleGeometry(full);
            foreach (var s in spots)
                outside = Geometry.Combine(outside, s.Shape, GeometryCombineMode.Exclude, null, 0.05, ToleranceType.Absolute);
            // The newest spotlight's look applies to the whole backdrop.
            var look = spots[^1];
            if (look.BlurOutside)
            {
                var blurred = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(blurred, BitmapScalingMode.Linear); // smooth upscale of the reduced-size blur
                using (var g = blurred.Open()) g.DrawImage(Context.GetBlurredFull(), full);
                dc.PushClip(outside);
                dc.DrawDrawing(blurred);
                dc.Pop();
            }
            var dim = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(look.Dim, 0, 0.95) * 255), 0, 0, 0));
            dim.Freeze();
            dc.PushClip(outside);
            dc.DrawRectangle(dim, null, full);
            dc.Pop();
        }

        foreach (var a in Annotations)
            if (a is not PixelateAnnotation and not SpotlightAnnotation)
                a.Render(dc, Context);
    }

    /// <summary>Flattens the edit into a bitmap at the original resolution.</summary>
    public BitmapSource RenderFinal()
    {
        if (IsPristine) return Original;
        var crop = EffectiveCrop;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            DrawContent(dc);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void LoadState(List<Annotation> annotations, Int32Rect? crop)
    {
        Annotations = annotations;
        Crop = crop;
    }
}

/// <summary>
/// Keeps the untouched original plus annotations for each saved file, so markup stays editable after
/// saving. Stored in Lumina's data folder, keyed by the output file's path, to keep the output folder clean.
/// </summary>
public static class ProjectStore
{
    private sealed class Project
    {
        public string? OutputPath { get; set; }
        public List<Annotation> Annotations { get; set; } = new();
        public int[]? Crop { get; set; }
        public DateTime Modified { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static string Key(string path) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())));

    private static string JsonPath(string outputPath) => Path.Combine(AppPaths.ProjectsDir, Key(outputPath) + ".json");
    private static string OriginalPath(string outputPath) => Path.Combine(AppPaths.ProjectsDir, Key(outputPath) + ".png");

    public static void Save(string outputPath, EditorDocument doc)
    {
        try
        {
            if (doc.IsPristine)
            {
                Delete(outputPath);
                return;
            }
            string original = OriginalPath(outputPath);
            if (!File.Exists(original)) OutputService.Encode(doc.Original, original);
            var crop = doc.Crop is Int32Rect c ? new[] { c.X, c.Y, c.Width, c.Height } : null;
            var project = new Project { OutputPath = outputPath, Annotations = doc.Annotations, Crop = crop, Modified = DateTime.Now };
            File.WriteAllText(JsonPath(outputPath), JsonSerializer.Serialize(project, Options));
            LibraryIndex.Invalidate(outputPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving edit project");
        }
    }

    public static EditorDocument? TryLoad(string outputPath)
    {
        try
        {
            string json = JsonPath(outputPath), original = OriginalPath(outputPath);
            if (!File.Exists(json) || !File.Exists(original)) return null;
            var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(json), Options);
            if (project == null) return null;
            var doc = new EditorDocument(OutputService.LoadImage(original));
            Int32Rect? crop = project.Crop is { Length: 4 } c ? new Int32Rect(c[0], c[1], c[2], c[3]) : null;
            doc.LoadState(project.Annotations, crop);
            return doc;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading edit project");
            return null;
        }
    }

    /// <summary>
    /// The text in a capture's markup, joined with spaces: "" when it has markup but no text, null when it has
    /// no markup at all. Reads the JSON without building the annotations.
    /// </summary>
    public static string? ReadText(string outputPath)
    {
        try
        {
            string json = JsonPath(outputPath);
            if (!File.Exists(json)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (!doc.RootElement.TryGetProperty("Annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
                return "";
            var texts = annotations.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("Text", out var t) && t.ValueKind == JsonValueKind.String)
                .Select(a => a.GetProperty("Text").GetString()!.Trim())
                .Where(t => t.Length > 0);
            return string.Join(" ", texts);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading markup text");
            return null;
        }
    }

    /// <summary>Keeps a capture's markup attached to it when the file is renamed.</summary>
    public static void Move(string fromPath, string toPath)
    {
        try
        {
            if (File.Exists(JsonPath(fromPath))) File.Move(JsonPath(fromPath), JsonPath(toPath), true);
            if (File.Exists(OriginalPath(fromPath))) File.Move(OriginalPath(fromPath), OriginalPath(toPath), true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Moving edit project");
        }
    }

    public static void Delete(string outputPath)
    {
        try
        {
            File.Delete(JsonPath(outputPath));
            File.Delete(OriginalPath(outputPath));
            LibraryIndex.Invalidate(outputPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deleting edit project");
        }
    }
}
