using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Editor;

namespace Lumina.Services;

public static class OutputService
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Picks a unique file path in the output folder using the user's naming pattern.</summary>
    public static string ReservePath(string? appName)
    {
        var s = App.Settings;
        Directory.CreateDirectory(s.OutputDirectory);
        string name = FileNameFormatter.Format(s.FileNamePattern, appName, s.Counter, DateTime.Now);
        if (s.FileNamePattern.Contains("{n}"))
        {
            s.Counter++;
            SettingsService.Save(s);
        }

        string ext = s.ImageFormat == ImageFormatKind.Jpeg ? ".jpg" : ".png";
        string path = Path.Combine(s.OutputDirectory, name + ext);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(s.OutputDirectory, $"{name}_{i}{ext}");

        // A stale edit project for a reused name would otherwise resurface old annotations.
        ProjectStore.Delete(path);
        return path;
    }

    /// <summary>Picks a unique .mp4 path in the recordings folder, named with the same pattern as captures.</summary>
    public static string ReserveVideoPath(string? appName)
    {
        var s = App.Settings;
        Directory.CreateDirectory(s.RecordingsDirectory);
        string name = FileNameFormatter.Format(s.FileNamePattern, appName, s.Counter, DateTime.Now);
        if (s.FileNamePattern.Contains("{n}"))
        {
            s.Counter++;
            SettingsService.Save(s);
        }
        string path = Path.Combine(s.RecordingsDirectory, name + ".mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(s.RecordingsDirectory, $"{name}_{i}.mp4");
        return path;
    }

    public static Task SaveAsync(BitmapSource image, string path) => StaTask.Run(() => Encode(image, path));

    public static void Encode(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder;
        if (ext is ".jpg" or ".jpeg")
        {
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(App.Settings.JpegQuality, 1, 100) };
            image = Flatten(image);
        }
        else if (ext == ".bmp")
        {
            encoder = new BmpBitmapEncoder();
            image = Flatten(image);
        }
        else
        {
            encoder = new PngBitmapEncoder();
        }

        encoder.Frames.Add(BitmapFrame.Create(image));
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            encoder.Save(fs);
        File.Move(tmp, path, true);
    }

    /// <summary>Removes alpha for formats that cannot store it, compositing onto white.</summary>
    private static BitmapSource Flatten(BitmapSource image)
    {
        if (image.Format == PixelFormats.Pbgra32)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawImage(image, rect);
            }
            var rtb = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            image = rtb;
        }
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    public static BitmapSource LoadImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var copy = new WriteableBitmap(frame);
        copy.Freeze();
        return copy;
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                OpenOutputFolder();
        }
        catch (Exception ex) { Log.Error(ex, "Show in folder"); }
    }

    /// <summary>Opens Explorer with every given file selected (files must share a folder).</summary>
    public static void ShowInFolder(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count <= 1)
        {
            ShowInFolder(existing.FirstOrDefault() ?? "");
            return;
        }

        var items = new List<IntPtr>();
        IntPtr folder = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(Path.GetDirectoryName(existing[0])!, IntPtr.Zero, out folder, 0, out _) != 0) throw new IOException("Folder not found");
            foreach (var path in existing)
                if (SHParseDisplayName(path, IntPtr.Zero, out var item, 0, out _) == 0) items.Add(item);
            int hr = SHOpenFolderAndSelectItems(folder, (uint)items.Count, items.ToArray(), 0);
            if (hr != 0) throw new IOException($"Explorer returned 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Show files in folder");
            ShowInFolder(existing[0]);
        }
        finally
        {
            foreach (var item in items) Marshal.FreeCoTaskMem(item);
            if (folder != IntPtr.Zero) Marshal.FreeCoTaskMem(folder);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);

    public static void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(App.Settings.OutputDirectory);
            Process.Start(new ProcessStartInfo(App.Settings.OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error(ex, "Open output folder"); }
    }

    public static void Recycle(string path)
    {
        try
        {
            if (File.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            ProjectStore.Delete(path);
        }
        catch (Exception ex) { Log.Error(ex, "Recycle file"); }
    }
}
