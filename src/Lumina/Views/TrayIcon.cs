using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Lumina.Capture;
using Lumina.Native;
using Lumina.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace Lumina.Views;

/// <summary>Notification-area icon with a Fluent-styled WPF menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lumina.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Lumina · Win+Shift+S to capture",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) App.ShowHome();
            else if (e.Button == Forms.MouseButtons.Middle) App.Instance.Controller.Start(CaptureMode.Region);
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    private void ShowMenu()
    {
        var controller = App.Instance.Controller;
        var menu = new ContextMenu();

        menu.Items.Add(Item("Capture region", "Win+Shift+S", "IconRegion", () => controller.Start(CaptureMode.Region, settleMs: 150)));
        menu.Items.Add(Item("Capture window", null, "IconWindow", () => controller.Start(CaptureMode.Window, settleMs: 150)));
        menu.Items.Add(Item("Capture full screen", null, "IconScreen", () => controller.Start(CaptureMode.Screen, settleMs: 150)));
        menu.Items.Add(Item("Freeform capture", null, "IconFreeform", () => controller.Start(CaptureMode.Freeform, settleMs: 150)));

        var delayed = Item("Capture after delay", null, "IconTimer", null);
        foreach (int seconds in new[] { 3, 5, 10 })
            delayed.Items.Add(Item($"{seconds} seconds", null, null, () => controller.Start(CaptureMode.Region, seconds, 150)));
        menu.Items.Add(delayed);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open Lumina", null, "IconHome", App.ShowHome));
        menu.Items.Add(Item("Open captures folder", null, "IconFolder", OutputService.OpenOutputFolder));
        menu.Items.Add(Item("Settings", null, "IconSettings", App.ShowSettings));
        menu.Items.Add(new Separator());
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
