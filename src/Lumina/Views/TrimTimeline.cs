using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumina.Views;

/// <summary>
/// Filmstrip timeline with trim handles and a playhead. Drag the handles to trim, click or drag anywhere
/// else to scrub. Times are in seconds.
/// </summary>
public sealed class TrimTimeline : FrameworkElement
{
    private const double Handle = 14, Radius = 10, MinLength = 0.2;

    public static readonly DependencyProperty DurationProperty = Register(nameof(Duration), 0.0);
    public static readonly DependencyProperty StartProperty = Register(nameof(Start), 0.0);
    public static readonly DependencyProperty EndProperty = Register(nameof(End), 0.0);
    public static readonly DependencyProperty PositionProperty = Register(nameof(Position), 0.0);

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(TrimTimeline), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => (double)GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    private IReadOnlyList<BitmapSource> _thumbs = Array.Empty<BitmapSource>();
    private enum Drag { None, Start, End, Scrub }
    private Drag _drag;

    /// <summary>The user scrubbed, or moved a handle (the video should show that frame).</summary>
    public event Action<double>? SeekRequested;
    /// <summary>A trim handle moved.</summary>
    public event Action? TrimChanged;
    /// <summary>A drag began (true) or ended (false), so playback can pause while scrubbing.</summary>
    public event Action<bool>? Dragging;

    private readonly Brush _track, _dim, _accent, _grip, _playhead;
    private readonly Pen _framePen, _playheadPen;

    public TrimTimeline()
    {
        Height = 68;
        Cursor = Cursors.Hand;
        Focusable = false;
        _track = (Brush)Application.Current.Resources["PanelBrush"];
        _dim = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, 0x0E, 0x0D, 0x15)));
        _accent = (Brush)Application.Current.Resources["AccentBrush"];
        _grip = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
        _playhead = Brushes.White;
        _framePen = Frozen(new Pen(_accent, 3));
        _playheadPen = Frozen(new Pen(_playhead, 2));
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public void SetThumbnails(IReadOnlyList<BitmapSource> thumbs)
    {
        _thumbs = thumbs;
        InvalidateVisual();
    }

    private Rect Strip => new(Handle, 6, Math.Max(1, ActualWidth - Handle * 2), Math.Max(1, ActualHeight - 12));

    private double X(double t) => Strip.X + (Duration > 0 ? t / Duration : 0) * Strip.Width;
    private double T(double x) => Duration <= 0 ? 0 : Math.Clamp((x - Strip.X) / Strip.Width, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var strip = Strip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable

        // Filmstrip
        dc.PushClip(new RectangleGeometry(strip, 6, 6));
        dc.DrawRectangle(_track, null, strip);
        if (_thumbs.Count > 0)
        {
            double aspect = _thumbs[0].PixelWidth / (double)Math.Max(1, _thumbs[0].PixelHeight);
            double cellW = Math.Max(8, strip.Height * aspect);
            int cells = (int)Math.Ceiling(strip.Width / cellW);
            for (int i = 0; i < cells; i++)
            {
                double x = strip.X + i * cellW;
                var thumb = _thumbs[Math.Min(_thumbs.Count - 1, (int)((x - strip.X + cellW / 2) / strip.Width * _thumbs.Count))];
                dc.DrawImage(thumb, new Rect(x, strip.Y, cellW, strip.Height));
            }
        }
        if (Duration > 0)
        {
            double sx = X(Start), ex = X(End);
            if (sx > strip.X) dc.DrawRectangle(_dim, null, new Rect(strip.X, strip.Y, sx - strip.X, strip.Height));
            if (ex < strip.Right) dc.DrawRectangle(_dim, null, new Rect(ex, strip.Y, strip.Right - ex, strip.Height));
        }
        dc.Pop();
        if (Duration <= 0) return;

        // Trim frame with grab handles on either side
        double s = X(Start), e = X(End);
        var frame = new Rect(s - Handle, 1.5, e - s + Handle * 2, ActualHeight - 3);
        var outline = new StreamGeometry();
        using (var ctx = outline.Open())
        {
            // Outer rounded rect minus the inner window = a frame whose sides are the handles.
            ctx.BeginFigure(new Point(frame.X + Radius, frame.Y), true, true);
            ctx.LineTo(new Point(frame.Right - Radius, frame.Y), false, false);
            ctx.ArcTo(new Point(frame.Right, frame.Y + Radius), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(frame.Right, frame.Bottom - Radius), false, false);
            ctx.ArcTo(new Point(frame.Right - Radius, frame.Bottom), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(frame.X + Radius, frame.Bottom), false, false);
            ctx.ArcTo(new Point(frame.X, frame.Bottom - Radius), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(frame.X, frame.Y + Radius), false, false);
            ctx.ArcTo(new Point(frame.X + Radius, frame.Y), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, false, false);
            ctx.BeginFigure(new Point(s, frame.Y + 3), true, true);
            ctx.LineTo(new Point(e, frame.Y + 3), false, false);
            ctx.LineTo(new Point(e, frame.Bottom - 3), false, false);
            ctx.LineTo(new Point(s, frame.Bottom - 3), false, false);
        }
        outline.FillRule = FillRule.EvenOdd;
        outline.Freeze();
        dc.DrawGeometry(_accent, null, outline);
        double mid = ActualHeight / 2;
        dc.DrawRoundedRectangle(_grip, null, new Rect(s - Handle / 2 - 1.5, mid - 9, 3, 18), 1.5, 1.5);
        dc.DrawRoundedRectangle(_grip, null, new Rect(e + Handle / 2 - 1.5, mid - 9, 3, 18), 1.5, 1.5);

        // Playhead
        double px = Math.Clamp(X(Position), strip.X, strip.Right);
        dc.DrawLine(_playheadPen, new Point(px, 0), new Point(px, ActualHeight));
        dc.DrawEllipse(_playhead, null, new Point(px, 2), 4.5, 4.5);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Cursor = HitHandle(p.X) != Drag.None ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        Apply(p.X);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        var x = e.GetPosition(this).X;
        _drag = HitHandle(x);
        if (_drag == Drag.None) _drag = Drag.Scrub;
        CaptureMouse();
        Dragging?.Invoke(true);
        Apply(x);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        ReleaseMouseCapture();
        Dragging?.Invoke(false);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        Dragging?.Invoke(false);
    }

    private Drag HitHandle(double x)
    {
        if (Duration <= 0) return Drag.None;
        double s = X(Start) - Handle / 2, e = X(End) + Handle / 2;
        double ds = Math.Abs(x - s), de = Math.Abs(x - e);
        if (Math.Min(ds, de) > Handle) return Drag.None;
        return ds < de || (ds == de && x < s) ? Drag.Start : Drag.End;
    }

    private void Apply(double x)
    {
        switch (_drag)
        {
            case Drag.Start:
                Start = Math.Clamp(T(x + Handle / 2), 0, Math.Max(0, End - MinLength));
                Position = Start;
                TrimChanged?.Invoke();
                SeekRequested?.Invoke(Start);
                break;
            case Drag.End:
                End = Math.Clamp(T(x - Handle / 2), Math.Min(Duration, Start + MinLength), Duration);
                Position = End;
                TrimChanged?.Invoke();
                SeekRequested?.Invoke(End);
                break;
            case Drag.Scrub:
                Position = T(x);
                SeekRequested?.Invoke(Position);
                break;
        }
    }

    private static T2 Frozen<T2>(T2 f) where T2 : Freezable
    {
        f.Freeze();
        return f;
    }
}
