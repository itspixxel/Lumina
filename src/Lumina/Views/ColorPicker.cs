using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Lumina.Services;
using ShapePath = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Lumina.Views;

/// <summary>HSV picker: saturation/brightness field, hue strip, hex entry, eyedropper and recent colors.</summary>
public sealed class ColorPicker : Border
{
    private const double FieldW = 232, FieldH = 150, StripH = 14;
    private const int MaxRecent = 8;

    private double _h, _s = 1, _v = 1;
    private bool _syncingHex;

    private readonly Border _fieldHue;
    private readonly Canvas _fieldLayer, _hueLayer;
    private readonly Ellipse _fieldThumb, _hueThumb;
    private readonly Border _preview;
    private readonly TextBox _hex;
    private readonly WrapPanel _recent;

    /// <summary>Raised live while dragging (<c>final</c> = false) and once when a pick is settled.</summary>
    public event Action<Color, bool>? ColorChanged;
    public event Action? EyedropperRequested;

    public ColorPicker()
    {
        Background = (Brush)FindResource("RaisedBrush");
        BorderBrush = (Brush)FindResource("StrokeStrongBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        Margin = new Thickness(14, 10, 14, 18); // room for the shadow inside the popup
        Effect = (Effect)FindResource("FloatShadow");

        // Saturation (left to right) and brightness (top to bottom) over the current hue.
        _fieldHue = new Border { CornerRadius = new CornerRadius(9) };
        var whiteFade = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var blackFade = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        _fieldThumb = Thumb(16);
        _fieldLayer = new Canvas { Width = FieldW, Height = FieldH, Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = false };
        _fieldLayer.Children.Add(_fieldThumb);
        var field = new Grid { Width = FieldW, Height = FieldH };
        field.Children.Add(_fieldHue);
        field.Children.Add(whiteFade);
        field.Children.Add(blackFade);
        field.Children.Add(_fieldLayer);
        Drag(_fieldLayer, p =>
        {
            _s = Math.Clamp(p.X / FieldW, 0, 1);
            _v = 1 - Math.Clamp(p.Y / FieldH, 0, 1);
        });

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        var strip = new Border
        {
            CornerRadius = new CornerRadius(StripH / 2),
            Background = rainbow,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        _hueThumb = Thumb(StripH + 4);
        _hueLayer = new Canvas { Width = FieldW, Height = StripH, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        _hueLayer.Children.Add(_hueThumb);
        var hue = new Grid { Width = FieldW, Height = StripH, Margin = new Thickness(0, 12, 0, 0) };
        hue.Children.Add(strip);
        hue.Children.Add(_hueLayer);
        Drag(_hueLayer, p => _h = Math.Clamp(p.X / FieldW, 0, 1) * 359.9);

        _preview = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        _hex = new TextBox
        {
            Width = 104,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            CharacterCasing = CharacterCasing.Upper,
            MaxLength = 7,
        };
        _hex.TextChanged += (_, _) =>
        {
            if (_syncingHex) return;
            if (TryParseHex(_hex.Text, out var c))
            {
                SetColorCore(c);
                ColorChanged?.Invoke(c, false);
            }
        };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TryParseHex(_hex.Text, out var c))
            {
                Commit(c);
                e.Handled = true;
            }
        };
        var dropper = new Button
        {
            Style = (Style)FindResource("IconButton"),
            ToolTip = "Pick a color from the image",
            Margin = new Thickness(6, 0, 0, 0),
            Content = new ShapePath { Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource("IconEyedropper") },
        };
        dropper.Click += (_, _) => EyedropperRequested?.Invoke();
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        row.Children.Add(_preview);
        row.Children.Add(_hex);
        DockPanel.SetDock(dropper, Dock.Right);
        row.Children.Add(dropper);

        _recent = new WrapPanel { Margin = new Thickness(-3, 10, 0, 0), Width = FieldW + 6 };

        var stack = new StackPanel();
        stack.Children.Add(field);
        stack.Children.Add(hue);
        stack.Children.Add(row);
        stack.Children.Add(_recent);
        Child = stack;

        Loaded += (_, _) => BuildRecent();
        SetColorCore(Color.FromRgb(0x7C, 0x5C, 0xFF));
    }

    public Color Color => FromHsv(_h, _s, _v);

    public void SetColor(Color c) => SetColorCore(c);

    /// <summary>Remembers a settled custom color at the front of the recent list.</summary>
    public static void Remember(Color c)
    {
        string hex = Hex(c);
        var list = App.Settings.RecentColors;
        list.RemoveAll(x => string.Equals(x, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);
        SettingsService.Save(App.Settings);
    }

    private void Commit(Color c)
    {
        Remember(c);
        BuildRecent();
        ColorChanged?.Invoke(c, true);
    }

    private void BuildRecent()
    {
        _recent.Children.Clear();
        foreach (var hex in App.Settings.RecentColors.Take(MaxRecent))
        {
            if (!TryParseHex(hex, out var c)) continue;
            var dot = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                ToolTip = hex,
                Cursor = Cursors.Hand,
                Focusable = false,
                Template = DotTemplate,
                Background = new SolidColorBrush(c),
            };
            Fx.SetPressScale(dot, 0.82);
            dot.Click += (_, _) =>
            {
                SetColorCore(c);
                Commit(c);
            };
            _recent.Children.Add(dot);
        }
        _recent.Visibility = _recent.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly ControlTemplate DotTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Grid Background='Transparent'><Ellipse x:Name='Ring' Stroke='White' StrokeThickness='2' Opacity='0' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'/>" +
        "<Ellipse Margin='5' Fill='{TemplateBinding Background}' Stroke='#40FFFFFF' StrokeThickness='1'/></Grid>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Ring' Property='Opacity' Value='0.35'/></Trigger></ControlTemplate.Triggers>" +
        "</ControlTemplate>");

    private void Drag(FrameworkElement area, Action<Point> apply)
    {
        void Update(MouseEventArgs e)
        {
            apply(e.GetPosition(area));
            Refresh();
            ColorChanged?.Invoke(Color, false);
        }
        area.MouseLeftButtonDown += (_, e) =>
        {
            area.CaptureMouse();
            Update(e);
            e.Handled = true;
        };
        area.MouseMove += (_, e) =>
        {
            if (area.IsMouseCaptured) Update(e);
        };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!area.IsMouseCaptured) return;
            area.ReleaseMouseCapture();
            Commit(Color);
        };
    }

    private void SetColorCore(Color c)
    {
        ToHsv(c, out double h, out _s, out _v);
        // Greys have no hue; keep the strip where it was instead of jumping to red.
        if (_s > 0.001) _h = h;
        Refresh();
    }

    private void Refresh()
    {
        var c = Color;
        _fieldHue.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_fieldThumb, _s * FieldW - _fieldThumb.Width / 2);
        Canvas.SetTop(_fieldThumb, (1 - _v) * FieldH - _fieldThumb.Height / 2);
        _fieldThumb.Fill = new SolidColorBrush(c);
        Canvas.SetLeft(_hueThumb, _h / 360 * FieldW - _hueThumb.Width / 2);
        Canvas.SetTop(_hueThumb, (StripH - _hueThumb.Height) / 2);
        _hueThumb.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        _preview.Background = new SolidColorBrush(c);
        if (!_hex.IsKeyboardFocused)
        {
            _syncingHex = true;
            _hex.Text = Hex(c);
            _syncingHex = false;
        }
    }

    private static Ellipse Thumb(double size) => new()
    {
        Width = size,
        Height = size,
        Stroke = Brushes.White,
        StrokeThickness = 2.5,
        IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5, Color = Colors.Black },
    };

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParseHex(string text, out Color color)
    {
        color = default;
        var t = text.Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * ((g - b) / d % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }
}
