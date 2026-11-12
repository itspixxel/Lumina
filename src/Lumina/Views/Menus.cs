using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lumina.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Lumina.Views;

/// <summary>Context-menu entries with a Midnight icon.</summary>
public static class Menus
{
    public static MenuItem Entry(string header, string iconKey, Action action, string gesture = "") =>
        Entry(header, (Geometry)Application.Current.FindResource(iconKey), action, gesture);

    public static MenuItem Entry(string header, Geometry icon, Action action, string gesture = "")
    {
        var entry = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Icon = new ShapePath { Style = (Style)Application.Current.FindResource("Icon"), Data = icon },
        };
        entry.Click += (_, _) => action();
        return entry;
    }

    /// <summary>Opens the output folder; when screenshots and recordings live apart, asks which one.</summary>
    public static void ShowOutputFolders(UIElement anchor)
    {
        string shots = App.Settings.OutputDirectory, videos = App.Settings.RecordingsDirectory;
        if (GalleryView.SamePath(shots, videos))
        {
            OutputService.OpenFolder(shots);
            return;
        }
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = anchor is Window ? PlacementMode.MousePoint : PlacementMode.Bottom,
        };
        menu.Items.Add(Entry("Screenshots", "IconRegion", () => OutputService.OpenFolder(shots)));
        menu.Items.Add(Entry("Recordings", "IconRecord", () => OutputService.OpenFolder(videos)));
        menu.IsOpen = true;
    }
}
