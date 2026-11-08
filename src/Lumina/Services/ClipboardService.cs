using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Lumina.Services;

public static class ClipboardService
{
    /// <summary>
    /// Puts the image on the clipboard as both a bitmap (for older apps) and PNG (keeps transparency
    /// in browsers, Discord, Office, etc.). PNG encoding happens off the UI thread.
    /// </summary>
    public static async Task<bool> CopyImageAsync(BitmapSource image)
    {
        byte[] png;
        try
        {
            png = await StaTask.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encoding clipboard PNG");
            png = Array.Empty<byte>();
        }

        var data = new DataObject();
        data.SetImage(image);
        if (png.Length > 0) data.SetData("PNG", new MemoryStream(png), false);

        return Set(() => Clipboard.SetDataObject(data, true));
    }

    /// <summary>Puts a file on the clipboard, as Explorer's Copy does, so it pastes into chats and folders.</summary>
    public static bool CopyFile(string path) => Set(() => Clipboard.SetFileDropList(new StringCollection { path }));

    private static bool Set(Action set)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                set();
                return true;
            }
            catch (COMException)
            {
                // Another app has the clipboard open; retry briefly.
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
