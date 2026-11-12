using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Lumina.Capture;
using Lumina.Native;
using Lumina.Recording;
using Lumina.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace Lumina.Views;

/// <summary>Notification-area icon with a Fluent-styled WPF menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private Action? _pendingClick;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lumina.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Visible = true,
        };
        UpdateTooltip();
        _icon.BalloonTipClicked += (_, _) => _pendingClick?.Invoke();
        _icon.BalloonTipClosed += (_, _) => _pendingClick = null;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) App.ShowHome();
            else if (e.Button == Forms.MouseButtons.Middle && !App.Instance.Recorder.IsRecording) App.Instance.Controller.Start(CaptureMode.Region);
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    /// <summary>The first shortcut set for a capture mode, for menus and hints.</summary>
    public static string? ShortcutFor(CaptureMode mode) =>
        App.Settings.Hotkeys?.FirstOrDefault(h => h.Mode == mode)?.ToString();

    public void UpdateTooltip()
    {
        string? key = App.Settings.Hotkeys?.FirstOrDefault()?.ToString();
        string text = key == null ? "Lumina" : $"Lumina - {key} to capture";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowMenu()
    {
        var controller = App.Instance.Controller;
        var recorder = App.Instance.Recorder;
        var menu = new ContextMenu();

        if (recorder.IsRecording)
        {
            menu.Items.Add(Item("Stop recording", ShortcutFor(CaptureMode.Record), "IconStop", recorder.Stop));
            menu.Items.Add(Item(recorder.IsPaused ? "Resume recording" : "Pause recording", null,
                recorder.IsPaused ? "IconPlay" : "IconPause", recorder.TogglePause));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Item("Capture region", ShortcutFor(CaptureMode.Region), "IconRegion", () => controller.Start(CaptureMode.Region, settleMs: 150)));
        menu.Items.Add(Item("Capture window", ShortcutFor(CaptureMode.Window), "IconWindow", () => controller.Start(CaptureMode.Window, settleMs: 150)));
        menu.Items.Add(Item("Capture full screen", ShortcutFor(CaptureMode.Screen), "IconScreen", () => controller.Start(CaptureMode.Screen, settleMs: 150)));
        menu.Items.Add(Item("Freeform capture", ShortcutFor(CaptureMode.Freeform), "IconFreeform", () => controller.Start(CaptureMode.Freeform, settleMs: 150)));

        var delayed = Item("Capture after delay", null, "IconTimer", null);
        foreach (int seconds in new[] { 3, 5, 10 })
            delayed.Items.Add(Item($"{seconds} seconds", null, null, () => controller.Start(CaptureMode.Region, seconds, 150)));
        menu.Items.Add(delayed);
        if (!recorder.IsRecording)
            menu.Items.Add(Item(Ffmpeg.IsAvailable ? "Record screen" : "Record screen (set up)", ShortcutFor(CaptureMode.Record), "IconRecord", recorder.Start));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open Lumina", null, "IconHome", App.ShowHome));
        menu.Items.Add(Item("Gallery", null, "IconGallery", () => GalleryWindow.ShowGallery()));
        menu.Items.Add(Item("Open captures folder", null, "IconFolder", OutputService.OpenOutputFolder));
        menu.Items.Add(Item("Settings", null, "IconSettings", App.ShowSettings));
        menu.Items.Add(new Separator());
        if (Updater.State == UpdateState.Ready)
            menu.Items.Add(Item($"Restart to update to {Updater.ReadyVersion?.ToString(3)}", null, "IconDownload", Updater.Restart));
        menu.Items.Add(Item("Quit Lumina", null, "IconPower", App.Quit));

        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) =>
        {
            // The menu only closes on outside clicks if its popup owns the foreground.
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                NativeMethods.SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    /// <summary>A Windows notification from the tray icon; clicking it runs <paramref name="onClick"/>.</summary>
    public void Notify(string title, string text, Action? onClick = null)
    {
        _pendingClick = onClick;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    private static MenuItem Item(string header, string? gesture, string? icon, Action? action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (icon != null)
        {
            item.Icon = new Shapes.Path
            {
                Data = (Geometry)Application.Current.Resources[icon],
                Style = (Style)Application.Current.Resources["Icon"],
            };
        }
        if (action != null) item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
