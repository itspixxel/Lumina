using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>
/// Base for Lumina's main windows: draws its own themed title bar (via WindowChrome) while keeping
/// native resizing, snapping, rounded corners and the DWM shadow.
/// </summary>
public class ChromeWindow : Window
{
    public static readonly DependencyProperty TitleBarContentProperty = DependencyProperty.Register(
        nameof(TitleBarContent), typeof(object), typeof(ChromeWindow));

    /// <summary>Extra controls shown on the right side of the title bar.</summary>
    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public ChromeWindow()
    {
        Style = (Style)Application.Current.FindResource("ChromeWindowStyle");
        SourceInitialized += (_, _) => ApplyDwmStyle();
        Theme.Changed += ApplyDwmStyle;
        Closed += (_, _) => Theme.Changed -= ApplyDwmStyle;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Minimize") is Button min) min.Click += (_, _) => WindowState = WindowState.Minimized;
        if (GetTemplateChild("PART_Maximize") is Button max)
            max.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, _) => Close();
    }

    /// <summary>Dark or light frame, round corners, and a 1 px border in the theme's strong line color.</summary>
    private void ApplyDwmStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var theme = Theme.Current;
        var line = theme["StrokeStrong"];
        // Square corners make square windows; small ones, the small Windows rounding.
        int dark = theme.IsDark ? 1 : 0, round = theme.Corners == 0 ? 1 : theme.Corners <= 6 ? 3 : 2;
        int border = line.R | (line.G << 8) | (line.B << 16); // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
