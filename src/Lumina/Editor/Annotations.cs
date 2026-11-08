using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Lumina.Editor;

public enum ShapeKind { Line, Arrow, Rectangle, Ellipse }

/// <summary>
/// A non-destructive markup element, stored in image pixel coordinates so it can be re-edited later.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ShapeAnnotation), "shape")]
[JsonDerivedType(typeof(StrokeAnnotation), "stroke")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(PixelateAnnotation), "pixelate")]
[JsonDerivedType(typeof(SpotlightAnnotation), "spotlight")]
public abstract class Annotation
{
    public string ColorHex { get; set; } = "#FFFF3B30";
    public double Thickness { get; set; } = 4;

    [JsonIgnore]
    public Color Color
    {
        get => (Color)ColorConverter.ConvertFromString(ColorHex);
        set => ColorHex = value.ToString(CultureInfo.InvariantCulture);
    }

    [JsonIgnore] public abstract Rect Bounds { get; }

    public abstract void Move(Vector delta);

    public abstract void Render(DrawingContext dc, RenderContext ctx);

    public virtual bool HitTest(Point p, double tolerance)
    {
        var b = Bounds;
        b.Inflate(tolerance + Thickness / 2, tolerance + Thickness / 2);
        return b.Contains(p);
    }

    public virtual Annotation Clone() => (Annotation)MemberwiseClone();

    protected Brush MakeBrush(byte? alpha = null)
    {
        var c = Color;
        if (alpha is byte a) c.A = a;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len = ab.LengthSquared;
        if (len < 1e-6) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len, 0, 1);
        return (p - (a + t * ab)).Length;
    }
}

public sealed class ShapeAnnotation : Annotation
{
    public ShapeKind Kind { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    [JsonIgnore] public Point Start => new(X1, Y1);
    [JsonIgnore] public Point End => new(X2, Y2);
    [JsonIgnore] public override Rect Bounds => new(Start, End);

    public override void Move(Vector d)
    {
        X1 += d.X; Y1 += d.Y; X2 += d.X; Y2 += d.Y;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var brush = MakeBrush();
        var pen = new Pen(brush, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        switch (Kind)
        {
            case ShapeKind.Line:
                dc.DrawLine(pen, Start, End);
                break;
            case ShapeKind.Rectangle:
                dc.DrawRoundedRectangle(null, pen, Bounds, Thickness * 0.6, Thickness * 0.6);
                break;
            case ShapeKind.Ellipse:
                var b = Bounds;
                dc.DrawEllipse(null, pen, new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
                break;
            case ShapeKind.Arrow:
                DrawArrow(dc, brush, pen);
                break;
        }
    }

    private void DrawArrow(DrawingContext dc, Brush brush, Pen pen)
    {
        var v = End - Start;
        double len = v.Length;
        if (len < 1) return;
        v /= len;
        var n = new Vector(-v.Y, v.X);
        double head = Math.Min(Math.Max(12, Thickness * 3.6), len * 0.7);
        double half = head * 0.58;
        var basePoint = End - v * head;

        dc.DrawLine(pen, Start, basePoint + v * Math.Min(head * 0.3, Thickness));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(End, true, true);
            g.LineTo(basePoint + n * half, true, true);
            g.LineTo(End - v * head * 0.82, true, true);
            g.LineTo(basePoint - n * half, true, true);
        }
        geometry.Freeze();
        var headPen = new Pen(brush, Math.Max(1, Thickness * 0.35)) { LineJoin = PenLineJoin.Round };
        headPen.Freeze();
        dc.DrawGeometry(brush, headPen, geometry);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        double reach = tolerance + Thickness / 2;
        if (Kind is ShapeKind.Line or ShapeKind.Arrow)
            return DistanceToSegment(p, Start, End) <= reach;

        // Outline only, so shapes drawn around things don't block selecting what's inside.
        var outer = Bounds;
        outer.Inflate(reach, reach);
        var inner = Bounds;
        inner.Inflate(-reach, -reach);
        return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
    }
}

public sealed class StrokeAnnotation : Annotation
{
    public List<Point> Points { get; set; } = new();
    public bool Highlighter { get; set; }

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            double minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
            return new Rect(minX, minY, Points.Max(p => p.X) - minX, Points.Max(p => p.Y) - minY);
        }
    }

    [JsonIgnore] private double Width => Highlighter ? Thickness * 4 : Thickness;

    public override void Move(Vector d)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += d;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count == 0) return;
        var brush = MakeBrush(Highlighter ? (byte)110 : null);
        if (Points.Count == 1)
        {
            dc.DrawEllipse(brush, null, Points[0], Width / 2, Width / 2);
            return;
        }

        var pen = new Pen(brush, Width)
        {
            StartLineCap = Highlighter ? PenLineCap.Square : PenLineCap.Round,
            EndLineCap = Highlighter ? PenLineCap.Square : PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        // Low-pass the raw input, then draw quadratic curves through the segment midpoints.
        var pts = Smooth(Points);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var mid = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                g.QuadraticBezierTo(pts[i], mid, true, true);
            }
            g.LineTo(pts[^1], true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static readonly double[] Kernel = { 1, 4, 6, 4, 1 };

    /// <summary>
    /// Binomial smoothing (two passes ≈ a Gaussian) that irons out mouse jitter and pixel stepping.
    /// The window shrinks near the ends so the stroke still starts and stops exactly where the user did.
    /// </summary>
    private static List<Point> Smooth(List<Point> input)
    {
        var pts = input;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pts.Count < 3) return pts;
            var output = new List<Point>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                int radius = Math.Min(2, Math.Min(i, pts.Count - 1 - i));
                double x = 0, y = 0, total = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    double w = Kernel[k + 2];
                    x += pts[i + k].X * w;
                    y += pts[i + k].Y * w;
                    total += w;
                }
                output.Add(new Point(x / total, y / total));
            }
            pts = output;
        }
        return pts;
    }

    public override bool HitTest(Point p, double tolerance)
    {
        double reach = tolerance + Width / 2;
        if (Points.Count == 1) return (p - Points[0]).Length <= reach;
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= reach) return true;
        return false;
    }

    public override Annotation Clone()
    {
        var copy = (StrokeAnnotation)MemberwiseClone();
        copy.Points = new List<Point>(Points);
        return copy;
    }
}

public sealed class TextAnnotation : Annotation
{
    public static readonly FontFamily Family = new("Segoe UI");
    public static readonly Typeface Face = new(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 24;

    [JsonIgnore] public Point Position => new(X, Y);

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            var ft = Format(Brushes.Black);
            return new Rect(X, Y, Math.Max(ft.WidthIncludingTrailingWhitespace, 4), ft.Height);
        }
    }

    public static double FontSizeFor(double thickness) => 12 + thickness * 3;

    public override void Move(Vector d)
    {
        X += d.X; Y += d.Y;
    }

    public FormattedText Format(Brush brush) =>
        new(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, FontSize, brush, 1.0);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (string.IsNullOrEmpty(Text)) return;
        // A soft contrasting halo keeps text readable on busy screenshots.
        var c = Color;
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        var halo = new SolidColorBrush(luminance > 0.6 ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(170, 255, 255, 255));
        halo.Freeze();
        var ft = Format(MakeBrush());
        var geometry = ft.BuildGeometry(Position);
        var haloPen = new Pen(halo, Math.Max(2, FontSize / 7)) { LineJoin = PenLineJoin.Round };
        haloPen.Freeze();
        dc.DrawGeometry(null, haloPen, geometry);
        dc.DrawGeometry(MakeBrush(), null, geometry);
    }
}

public sealed class StepAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public int Number { get; set; } = 1;

    [JsonIgnore] public double Radius => 9 + Thickness * 1.6;
    [JsonIgnore] public override Rect Bounds => new(X - Radius, Y - Radius, Radius * 2, Radius * 2);

    public override void Move(Vector d)
    {
        X += d.X; Y += d.Y;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var c = Color;
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        var fg = luminance > 0.65 ? Brushes.Black : Brushes.White;
        var ring = new Pen(fg, Math.Max(1.5, Radius * 0.12));
        ring.Freeze();
        var center = new Point(X, Y);
        dc.DrawEllipse(MakeBrush(), ring, center, Radius, Radius);
        var ft = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(TextAnnotation.Family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * (Number > 9 ? 0.95 : 1.15), fg, 1.0);
        dc.DrawText(ft, new Point(X - ft.Width / 2, Y - ft.Height / 2));
    }
}

/// <summary>Base for annotations defined by a rectangle.</summary>
public abstract class RegionAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    [JsonIgnore] public override Rect Bounds => new(X, Y, W, H);

    public void SetFromPoints(Point a, Point b)
    {
        X = Math.Min(a.X, b.X); Y = Math.Min(a.Y, b.Y);
        W = Math.Abs(a.X - b.X); H = Math.Abs(a.Y - b.Y);
    }

    public override void Move(Vector d)
    {
        X += d.X; Y += d.Y;
    }
}

public enum RedactStyle { Mosaic, Blur, Solid }

/// <summary>Redacts an area by pixelating, blurring or covering the original image underneath it.</summary>
public sealed class PixelateAnnotation : RegionAnnotation
{
    public RedactStyle Style { get; set; } = RedactStyle.Mosaic;

    /// <summary>1 (fine) to 10 (coarse); 0 = automatic, from the region size.</summary>
    public double Strength { get; set; }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = ctx.ClampToImage(Bounds);
        if (r.Width < 1 || r.Height < 1) return;
        var target = new Rect(r.X, r.Y, r.Width, r.Height);
        if (Style == RedactStyle.Solid)
        {
            dc.DrawRoundedRectangle(MakeBrush(), null, target, 2, 2);
            return;
        }
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, Style == RedactStyle.Mosaic ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        using (var g = group.Open())
            g.DrawImage(Style == RedactStyle.Mosaic ? ctx.GetPixelated(r, Strength) : ctx.GetBlurred(r, Strength), target);
        dc.DrawDrawing(group);
    }
}

/// <summary>Keeps an area bright while dimming (and optionally blurring) the rest; drawn by the document.</summary>
public sealed class SpotlightAnnotation : RegionAnnotation
{
    public bool Ellipse { get; set; }

    /// <summary>How dark the surroundings get, 0–1. Shared by all spotlights in a document.</summary>
    public double Dim { get; set; } = 0.6;

    /// <summary>Also blur everything outside the spotlights.</summary>
    public bool BlurOutside { get; set; }

    [JsonIgnore]
    public Geometry Shape => Ellipse
        ? new EllipseGeometry(Bounds)
        : new RectangleGeometry(Bounds, Math.Min(10, Math.Min(W, H) / 6), Math.Min(10, Math.Min(W, H) / 6));

    public override void Render(DrawingContext dc, RenderContext ctx) { }

    public override bool HitTest(Point p, double tolerance)
    {
        var b = Bounds;
        b.Inflate(tolerance, tolerance);
        return b.Contains(p);
    }
}
