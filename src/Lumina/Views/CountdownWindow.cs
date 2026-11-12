using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Lumina.Capture;
using Lumina.Native;
using Shapes = System.Windows.Shapes;

namespace Lumina.Views;

/// <summary>
/// The countdown before a delayed capture or a recording. It covers the screen click-through and is excluded
/// from capture. With an area it dims everything else and draws the area's outline in; a glass dial sits in the
/// middle with a ring that runs down and numbers that pop in on each second.
/// </summary>
internal sealed class CountdownWindow : RecordingSurfaceWindow
{
    private const double Dial = 172;

    private readonly MonitorInfo _monitor;
    private readonly Int32Rect? _area;
    private readonly Canvas _canvas = new();
    private readonly Shapes.Path _scrim;
    private readonly Shapes.Rectangle _outline;
    private readonly Grid _card;
    private readonly ScaleTransform _cardScale = new(0.6, 0.6);
    private readonly Grid _numbers = new();
    private readonly CountdownRing _ring;
    private readonly TextBlock _caption;

    private CountdownWindow(Int32Rect? area, string caption) : base(clickThrough: true)
    {
        _area = area;
        IsHitTestVisible = false;
        var monitors = MonitorInfo.GetAll();
        Point anchor = area is { } a
            ? new Point(a.X + a.Width / 2.0, a.Y + a.Height / 2.0)
            : NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : new Point(0, 0);
        _monitor = monitors.FirstOrDefault(m => RectUtil.Contains(m.Bounds, anchor)) ?? monitors.First(m => m.IsPrimary);

        var accent = (Color)Application.Current.Resources["AccentColor"];
        _scrim = new Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x07, 0x06, 0x10)), Opacity = 0 };
        _outline = new Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            RadiusX = 4,
            RadiusY = 4,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = accent, Opacity = 0.9 },
            Visibility = area == null ? Visibility.Collapsed : Visibility.Visible,
        };
        if (area == null) _scrim.Visibility = Visibility.Collapsed;

        // The dial: glass disc, depleting ring, caption and number.
        _ring = new CountdownRing { Margin = new Thickness(9) };
        _caption = new TextBlock
        {
            Text = caption,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Text2Brush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 92),
        };
        _numbers.HorizontalAlignment = HorizontalAlignment.Center;
        _numbers.VerticalAlignment = VerticalAlignment.Center;
        _numbers.Margin = new Thickness(0, 10, 0, 0);
        _card = new Grid { Width = Dial, Height = Dial, RenderTransform = _cardScale, RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0 };
        _card.Children.Add(new Shapes.Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1B, 0x29)),
            Stroke = (Brush)Application.Current.Resources["StrokeStrongBrush"],
            StrokeThickness = 1,
            Effect = (Effect)Application.Current.Resources["FloatShadow"],
        });
        _card.Children.Add(_ring);
        _card.Children.Add(_caption);
        _card.Children.Add(_numbers);

        _canvas.Children.Add(_scrim);
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_card);
        Content = _canvas;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
    }

    /// <summary>Counts down <paramref name="seconds"/>, then plays out; returns once it's gone.</summary>
    public static async Task RunAsync(int seconds, Int32Rect? area = null, string caption = "CAPTURING IN")
    {
        var window = new CountdownWindow(area, caption);
        window.Show();
        await window.PlayAsync(seconds);
        window.Close();
        await Task.Delay(40); // let the compositor drop it before the screen is grabbed
    }

    protected override void Place()
    {
        SetBounds(_monitor.Bounds);
        Layout();
    }

    /// <summary>Positions everything in this window's DIPs, from the physical-pixel area.</summary>
    private void Layout()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var b = _monitor.Bounds;
        double w = b.Width / dpi.DpiScaleX, h = b.Height / dpi.DpiScaleY;
        _canvas.Width = w;
        _canvas.Height = h;

        Rect local = _area is { } a
            ? new Rect((a.X - b.X) / dpi.DpiScaleX, (a.Y - b.Y) / dpi.DpiScaleY, a.Width / dpi.DpiScaleX, a.Height / dpi.DpiScaleY)
            : new Rect(0, 0, w, h);

        _scrim.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(local) },
        };
        var ring = Rect.Inflate(local, 4, 4); // where the recording frame's line will be
        Canvas.SetLeft(_outline, ring.X);
        Canvas.SetTop(_outline, ring.Y);
        _outline.Width = ring.Width;
        _outline.Height = ring.Height;

        double cx = Math.Clamp(local.X + local.Width / 2, Dial / 2 + 12, w - Dial / 2 - 12);
        double cy = Math.Clamp(local.Y + local.Height / 2, Dial / 2 + 12, h - Dial / 2 - 12);
        Canvas.SetLeft(_card, cx - Dial / 2);
        Canvas.SetTop(_card, cy - Dial / 2);
    }

    private async Task PlayAsync(int seconds)
    {
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }
        Layout();

        // In: dim the rest, draw the outline round the area, pop the dial up.
        _scrim.BeginAnimation(OpacityProperty, Anim(0, 1, 320, new CubicEase { EasingMode = EasingMode.EaseOut }));
        DrawOutline();
        _card.BeginAnimation(OpacityProperty, Anim(0, 1, 220, null));
        var pop = Anim(0.6, 1, 520, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        _ring.BeginAnimation(CountdownRing.ProgressProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds)));

        // Each second lands on the clock rather than after the last await, so it doesn't drift.
        var clock = Stopwatch.StartNew();
        for (int i = seconds; i > 0; i--)
        {
            ShowNumber(i, first: i == seconds);
            if (i != seconds) Ripple();
            long due = (long)(seconds - i + 1) * 1000;
            int wait = (int)(due - clock.ElapsedMilliseconds);
            if (wait > 0) await Task.Delay(wait);
        }

        // Out: the dial shrinks away and the dim lifts as the recording frame takes over the outline.
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 0.5, 200, ease));
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 0.5, 200, ease));
        _card.BeginAnimation(OpacityProperty, Anim(null, 0, 180, ease));
        _scrim.BeginAnimation(OpacityProperty, Anim(null, 0, 200, ease));
        await Task.Delay(210);
    }

    private void DrawOutline()
    {
        if (_area == null) return;
        // Dash lengths are in stroke widths: one dash as long as the whole perimeter, slid into view.
        double length = 2 * (_outline.Width + _outline.Height) / _outline.StrokeThickness + 4;
        _outline.StrokeDashArray = new DoubleCollection { length, length };
        _outline.BeginAnimation(Shapes.Shape.StrokeDashOffsetProperty, Anim(length, 0, 700, new QuinticEase { EasingMode = EasingMode.EaseInOut }));
    }

    /// <summary>The old number shrinks up and away while the new one springs in from larger.</summary>
    private void ShowNumber(int n, bool first)
    {
        foreach (var old in _numbers.Children.OfType<TextBlock>().ToList())
        {
            var t = (TransformGroup)old.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            ((ScaleTransform)t.Children[0]).BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 0.55, 220, ease));
            ((ScaleTransform)t.Children[0]).BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 0.55, 220, ease));
            ((TranslateTransform)t.Children[1]).BeginAnimation(TranslateTransform.YProperty, Anim(null, -16, 220, ease));
            var fade = Anim(null, 0, 200, ease);
            fade.Completed += (_, _) => _numbers.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }

        var scale = new ScaleTransform(1.6, 1.6);
        var move = new TranslateTransform(0, first ? 0 : 10);
        var text = new TextBlock
        {
            Text = n.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"],
            FontSize = 76,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, move } },
            Opacity = 0,
        };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        _numbers.Children.Add(text);

        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        int delay = first ? 120 : 70;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1.6, 1, 460, spring, delay));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1.6, 1, 460, spring, delay));
        move.BeginAnimation(TranslateTransform.YProperty, Anim(move.Y, 0, 420, new CubicEase { EasingMode = EasingMode.EaseOut }, delay));
        text.BeginAnimation(OpacityProperty, Anim(0, 1, 200, null, delay));

        if (!first)
        {
            // The dial gives a small heartbeat on each second.
            var beat = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.045, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            beat.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
            _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
        }
    }

    /// <summary>An accent ring that spreads out from the dial and fades.</summary>
    private void Ripple()
    {
        var scale = new ScaleTransform(1, 1);
        var ring = new Shapes.Ellipse
        {
            Stroke = (Brush)Application.Current.Resources["AccentBrush"],
            StrokeThickness = 2,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            IsHitTestVisible = false,
        };
        _card.Children.Insert(0, ring);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 1.55, 850, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 1.55, 850, ease));
        var fade = Anim(0.7, 0, 850, ease);
        fade.Completed += (_, _) => _card.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    private static DoubleAnimation Anim(double? from, double to, int ms, IEasingFunction? ease, int delay = 0) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromMilliseconds(ms),
        BeginTime = TimeSpan.FromMilliseconds(delay),
        EasingFunction = ease,
        FillBehavior = FillBehavior.HoldEnd,
    };
}

/// <summary>A faint track with an accent arc that runs down clockwise from the top as <see cref="Progress"/> falls to 0.</summary>
internal sealed class CountdownRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(CountdownRing), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    private readonly Pen _track, _arc;

    public CountdownRing()
    {
        IsHitTestVisible = false;
        _track = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 4);
        _track.Freeze();
        _arc = new Pen((Brush)Application.Current.Resources["AccentGradient"], 5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        _arc.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double r = Math.Min(ActualWidth, ActualHeight) / 2 - 3;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, _track, c, r, r);

        double p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.001) return;
        if (p >= 0.999)
        {
            dc.DrawEllipse(null, _arc, c, r, r);
            return;
        }
        double angle = p * 360;
        double rad = (angle - 90) * Math.PI / 180;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, _arc, g);
    }
}
