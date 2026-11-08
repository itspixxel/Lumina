using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lumina.Capture;
using Lumina.Views;

namespace Lumina.Services;

/// <summary>Runs a capture end to end and applies the user's after-capture actions.</summary>
public sealed class CaptureController
{
    private bool _busy;

    /// <summary>Raised on the UI thread after a capture has been written to disk.</summary>
    public event Action? Captured;

    public async void Start(CaptureMode mode, int delaySeconds = 0, int settleMs = 0)
    {
        if (_busy) return;
        _busy = true;
        var hidden = new List<Window>();
        CaptureResult? result = null;
        try
        {
            QuickPreviewWindow.CloseCurrent();
            foreach (var w in Application.Current.Windows.OfType<HomeWindow>().Where(w => w.IsVisible))
            {
                w.Hide();
                hidden.Add(w);
            }
            // Give DWM time to finish hiding windows/menus so they don't end up in the shot.
            if (hidden.Count > 0) settleMs = Math.Max(settleMs, 200);
            if (settleMs > 0) await Task.Delay(settleMs);
            if (delaySeconds > 0) await CountdownWindow.RunAsync(delaySeconds);

            result = await CaptureSession.RunAsync(mode, App.Settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture");
            LuminaDialog.Alert(null, "Capture failed", ex.Message);
        }
        finally
        {
            foreach (var w in hidden) w.Show();
            _busy = false;
        }

        if (result != null) Handle(result);
    }

    private void Handle(CaptureResult result)
    {
        var s = App.Settings;
        string? path = null;
        Task? save = null;

        if (s.AutoSave)
        {
            try
            {
                path = OutputService.ReservePath(result.AppName);
                save = OutputService.SaveAsync(result.Image, path);
                save.ContinueWith(t =>
                {
                    if (t.IsFaulted) Log.Error(t.Exception!, "Saving capture");
                    else Application.Current.Dispatcher.BeginInvoke(() => Captured?.Invoke());
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Saving capture");
                path = null;
                save = null;
            }
        }

        if (s.CopyToClipboard) _ = ClipboardService.CopyImageAsync(result.Image);

        if (s.OpenEditorAfterCapture)
            EditorWindow.Open(result.Image, path, save);
        else if (s.ShowQuickPreview)
            QuickPreviewWindow.ShowCapture(result.Image, path, save, result.AppName);
    }

    public void NotifySaved() => Captured?.Invoke();
}
