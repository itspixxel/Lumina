using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
}
