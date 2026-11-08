using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumina.Capture;

/// <summary>
/// A soft falloff (glow or shadow) built from many thin nested strokes. Each ring's alpha is solved so the
/// stacked result follows a smooth curve, instead of the visible steps you get from two or three wide pens.
/// </summary>
internal sealed class SoftRings
{
    private readonly Pen[] _pens;
    private readonly double[] _reach;

    public Brush Core { get; }

    /// <param name="peak">Opacity right at the edge.</param>
    /// <param name="radius">Distance at which the falloff reaches zero.</param>
    public SoftRings(Color color, double peak, double radius, int steps)
    {
        _pens = new Pen[steps];
        _reach = new double[steps];
        // Target coverage per band, sampled mid-band on an ease-out curve that lands softly at zero.
        double Target(int k) => k >= steps ? 0 : peak * Math.Pow(1 - (k + 0.5) / steps, 2.4);
        for (int k = steps - 1; k >= 0; k--)
        {
            // Bands are covered by every ring at least that wide; "over" compositing multiplies the transparencies.
            double alpha = 1 - (1 - Target(k)) / (1 - Target(k + 1));
            var c = color;
            c.ScA = (float)Math.Clamp(alpha, 0, 1);
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            _reach[k] = radius * (k + 1) / steps;
            var pen = new Pen(brush, _reach[k]) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            _pens[k] = pen;
        }
        var core = color;
        core.ScA = (float)peak;
        Core = new SolidColorBrush(core);
        Core.Freeze();
    }

    /// <summary>Falloff outside a (rounded) rectangle only; the inside is left untouched.</summary>
    public void DrawAround(DrawingContext dc, Rect r, double cornerRadius = 0)
    {
        for (int k = 0; k < _pens.Length; k++)
        {
            double half = _reach[k] / 2;
            var ring = new Rect(r.X - half, r.Y - half, r.Width + _reach[k], r.Height + _reach[k]);
            double cr = cornerRadius + half;
            dc.DrawRoundedRectangle(null, _pens[k], ring, cr, cr);
        }
    }

    /// <summary>The same falloff as pens stroked centred on a path, so it spreads both ways (lasso outlines).</summary>
    public Pen[] Centered()
    {
        var pens = new Pen[_pens.Length];
        for (int k = 0; k < pens.Length; k++)
        {
            var pen = new Pen(_pens[k].Brush, _reach[k] * 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            pens[k] = pen;
        }
        return pens;
    }
}

/// <summary>CSS-style cubic-bezier timing curve.</summary>
internal readonly struct Bezier(double x1, double y1, double x2, double y2)
{
    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Solve x(s) = t with Newton, falling back to bisection, then return y(s).
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Sample(s, x1, x2) - t;
            double d = Slope(s, x1, x2);
            if (Math.Abs(x) < 1e-5) return Sample(s, y1, y2);
            if (Math.Abs(d) < 1e-6) break;
            s -= x / d;
        }
        double lo = 0, hi = 1;
        s = t;
        for (int i = 0; i < 30; i++)
        {
            double x = Sample(s, x1, x2);
            if (Math.Abs(x - t) < 1e-5) break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return Sample(s, y1, y2);
    }

    private static double Sample(double s, double a, double b) => ((1 - 3 * b + 3 * a) * s + (3 * b - 6 * a)) * s * s + 3 * a * s;
    private static double Slope(double s, double a, double b) => 3 * (1 - 3 * b + 3 * a) * s * s + 2 * (3 * b - 6 * a) * s + 3 * a;
}

/// <summary>
/// The captured image travelling from where it was taken to the quick-preview corner. Drawn by hand from a
/// single clock so position, size, rounding, shadow and flash all stay in lockstep on every frame.
/// </summary>
internal sealed class FlightCard : FrameworkElement
{
    public const double FlightStart = 70, FlightLength = 470, Total = FlightStart + FlightLength;

    // Different curves per axis bend the path into a gentle arc instead of a straight slide.
    private static readonly Bezier MoveX = new(0.32, 0, 0.12, 1);
    private static readonly Bezier MoveY = new(0.55, 0, 0.18, 1);
    private static readonly Bezier Shrink = new(0.28, 0, 0.1, 1);

    private static readonly SoftRings Shadow = new(Color.FromRgb(0, 0, 0), 0.5, 30, 22);
    private static readonly Brush FlashBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Pen Rim = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))), 1));

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(FlightCard), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Milliseconds since the shutter.</summary>
    public double Time { get => (double)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }

    private readonly BitmapSource _image;
    private readonly Rect _from, _to;

    public FlightCard(BitmapSource image, Rect from, Rect to)
    {
        _image = image;
        _from = from;
        _to = to;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double t = Time;
        double p = Math.Clamp((t - FlightStart) / FlightLength, 0, 1);
        double ex = MoveX.Ease(p), ey = MoveY.Ease(p), es = Shrink.Ease(p);

        double w = Lerp(_from.Width, _to.Width, es), h = Lerp(_from.Height, _to.Height, es);
        // Track the centre, so the shrink and the travel don't fight each other.
        double cx = Lerp(_from.X + _from.Width / 2, _to.X + _to.Width / 2, ex);
        double cy = Lerp(_from.Y + _from.Height / 2, _to.Y + _to.Height / 2, ey);
        var r = new Rect(cx - w / 2, cy - h / 2, w, h);

        // Lifted off the page mid-flight; settles flat again as the preview takes over.
        double lift = Smooth(0, 0.22, p) * (1 - 0.65 * Smooth(0.7, 1, p));
        double radius = 7 * Math.Sin(Math.PI * Math.Min(1, p * 1.1)) * Math.Min(1, Math.Min(w, h) / 40);
        radius = Math.Max(0, radius);

        if (lift > 0.002)
        {
            dc.PushOpacity(lift);
            var shadow = r;
            shadow.Offset(0, 4 + 8 * lift);
            dc.DrawRoundedRectangle(Shadow.Core, null, shadow, radius, radius);
            Shadow.DrawAround(dc, shadow, radius);
            dc.Pop();
        }

        dc.PushClip(new RectangleGeometry(r, radius, radius));
        dc.DrawImage(_image, r);
        double flash = Flash(t);
        if (flash > 0.002)
        {
            dc.PushOpacity(flash);
            dc.DrawRectangle(FlashBrush, null, r);
            dc.Pop();
        }
        dc.Pop();

        if (lift > 0.002)
        {
            dc.PushOpacity(lift);
            dc.DrawRoundedRectangle(null, Rim, new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)), radius, radius);
            dc.Pop();
        }
    }

    /// <summary>Quick rise, long soft tail.</summary>
    private static double Flash(double t)
    {
        const double peakAt = 60, endAt = 330, peak = 0.55;
        if (t <= 0 || t >= endAt) return 0;
        if (t < peakAt) return peak * Math.Sin(t / peakAt * Math.PI / 2);
        double u = (t - peakAt) / (endAt - peakAt);
        return peak * Math.Pow(1 - u, 2.2);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
