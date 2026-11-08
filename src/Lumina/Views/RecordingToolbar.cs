using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lumina.Capture;
using Lumina.Native;
using Lumina.Recording;
using Shapes = System.Windows.Shapes;

namespace Lumina.Views;

/// <summary>
/// A borderless helper window shown while recording. It never takes focus (so the app being recorded keeps
/// it) and is excluded from screen capture, so it doesn't end up in the video even when it's inside the area.
/// </summary>
public abstract class RecordingSurfaceWindow : Window
{
    private readonly bool _clickThrough;

    protected RecordingSurfaceWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000; // off-screen until placed in physical pixels
        UseLayoutRounding = true;
        Loaded += (_, _) => Place();
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            long ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            if (_clickThrough) ex |= NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
            if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
                Services.Log.Info("Couldn't exclude recording controls from capture; they may show in the video.");
            Place();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
    }

    protected IntPtr Handle => new WindowInteropHelper(this).Handle;

    private bool _dismissing, _closed;

    /// <summary>Fades out, then closes.</summary>
    public virtual void Dismiss()
    {
        if (_dismissing || _closed) return;
        _dismissing = true;
        if (!IsLoaded || Content is not UIElement content)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (!_closed) Close();
        };
        content.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>Positions the window in physical pixels.</summary>
    protected abstract void Place();

    protected void SetBounds(Int32Rect r) =>
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
}

/// <summary>
/// The glowing outline around the area being recorded, drawn just outside it. Dragging the outline or the tab on
/// top moves the area mid-recording (its size is fixed, since the video's is). The inside is fully transparent,
/// which a layered window treats as click-through, so the app being recorded stays usable.
/// </summary>
public sealed class RecordingFrame : RecordingSurfaceWindow
{
    private const double Ring = 10;     // grab band around the area
    private const double TabSpace = 28; // room above the area for the move tab

    private readonly ScreenRecorder _recorder;
    private readonly Border _band, _line, _tab;
    private readonly Grid _root = new();
    private double _top = TabSpace;
    private bool _tabInside;

    private bool _dragging;
    private NativeMethods.POINT _dragStart;
    private Int32Rect _dragFrom;

    public RecordingFrame(ScreenRecorder recorder) : base(clickThrough: false)
    {
        _recorder = recorder;
        var accent = (Color)Application.Current.Resources["AccentColor"];

        // Nearly invisible, but not zero alpha, so the band takes the mouse while the inside doesn't.
        _band = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move the recording area",
        };
        _band.MouseLeftButtonDown += (_, e) => BeginDrag(e);

        _line = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Color = accent, Opacity = 0.8 },
        };

        // The move tab: an accent pill with grip dots sitting on the top edge.
        var dots = new Shapes.Path
        {
            Data = Geometry.Parse("M0,0 h0.01 M5,0 h0.01 M10,0 h0.01 M15,0 h0.01 M0,4 h0.01 M5,4 h0.01 M10,4 h0.01 M15,4 h0.01"),
            Stroke = Brushes.White,
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9,
        };
        _tab = new Border
        {
            Width = 54,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = (Brush)Application.Current.Resources["AccentGradient"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move the recording area",
            Child = dots,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _tab.MouseLeftButtonDown += (_, e) => BeginDrag(e);
        Fx.SetPressScale(_tab, 0.92);

        _root.Children.Add(_band);
        _root.Children.Add(_line);
        _root.Children.Add(_tab);
        Content = _root;
        ApplyLayout();

        MouseMove += (_, _) => Drag();
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => EndDrag();
        Loaded += (_, _) =>
        {
            Fx.PlayEntrance(_line, 0, offsetY: 0, fromScale: 1.02);
            Fx.PlayEntrance(_tab, 120, offsetY: 8, fromScale: 0.8);
        };
    }

    /// <summary>Positions the band, line and tab for the current top margin.</summary>
    private void ApplyLayout()
    {
        _band.BorderThickness = new Thickness(Ring, _top, Ring, Ring);
        _line.Margin = new Thickness(Ring - 4, _top - 4, Ring - 4, Ring - 4);
        // Normally the tab sits on the top edge outside; at the top of the screen it hangs just inside instead.
        _tab.Margin = new Thickness(0, _tabInside ? _top + 2 : _top - 4 - 18 + 1, 0, 0);
    }

    protected override void Place()
    {
        if (Handle == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var src = _recorder.Source;
        var monitors = MonitorInfo.GetAll();
        int topEdge = monitors.Where(m => m.Bounds.X < src.X + src.Width && src.X < m.Bounds.X + m.Bounds.Width)
            .Select(m => m.Bounds.Y).DefaultIfEmpty(int.MinValue).Min();

        bool inside = src.Y - TabSpace * dpi.DpiScaleY < topEdge;
        if (inside != _tabInside)
        {
            _tabInside = inside;
            _top = inside ? Ring : TabSpace;
            ApplyLayout();
        }
        int ring = (int)Math.Round(Ring * dpi.DpiScaleX);
        int top = (int)Math.Round(_top * dpi.DpiScaleY);
        SetBounds(new Int32Rect(src.X - ring, src.Y - top, src.Width + ring * 2, src.Height + top + ring));
    }

    // ---------- Moving ----------

    private void BeginDrag(MouseButtonEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out _dragStart)) return;
        _dragging = true;
        _dragFrom = _recorder.Source;
        _root.CaptureMouse();
        e.Handled = true;
    }

    private void Drag()
    {
        if (!_dragging || !NativeMethods.GetCursorPos(out var now)) return;
        var r = _dragFrom;
        // Keep the whole area on the screens (clamped to the virtual desktop).
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN), vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN), vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        int x = Math.Clamp(r.X + now.X - _dragStart.X, vx, Math.Max(vx, vx + vw - r.Width));
        int y = Math.Clamp(r.Y + now.Y - _dragStart.Y, vy, Math.Max(vy, vy + vh - r.Height));
        _recorder.Source = new Int32Rect(x, y, r.Width, r.Height);
        Place();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_root.IsMouseCaptured) _root.ReleaseMouseCapture();
    }

    /// <summary>Dims the outline while paused.</summary>
    public void SetPaused(bool paused) => _line.BeginAnimation(OpacityProperty,
        new DoubleAnimation(paused ? 0.4 : 1, TimeSpan.FromMilliseconds(250)));
}

/// <summary>Floating controls while recording: timer, pause, stop, audio mutes and discard.</summary>
public sealed class RecordingToolbar : RecordingSurfaceWindow
{
    private readonly RecordingController _controller;
    private readonly Int32Rect _area;
    private readonly Shapes.Ellipse _dot, _halo;
    private readonly SolidColorBrush _dotBrush;
    private readonly Shapes.Path _spinner;
    private readonly RollingText _time;
    private readonly TextBlock _pausedText;
    private readonly Border _pausedHost;
    private readonly Button _pause, _stop, _discard;
    private readonly Shapes.Path _pauseIcon;
    private readonly StackPanel _row, _buttons;
    private readonly DispatcherTimer _timer;
    private readonly Border _pill;
    private readonly TranslateTransform _pillMove = new();
    private bool _placed, _paused;

    public RecordingToolbar(RecordingController controller, ScreenRecorder recorder) : base(clickThrough: false)
    {
        _controller = controller;
        _area = recorder.Area;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        Foreground = (Brush)Application.Current.Resources["TextBrush"];

        _row = new StackPanel { Orientation = Orientation.Horizontal };

        // The live dot: a red light with a halo breathing out of it; a spinner takes its place while saving.
        _dotBrush = new SolidColorBrush(((SolidColorBrush)Application.Current.Resources["DangerBrush"]).Color);
        _halo = new Shapes.Ellipse { Width = 10, Height = 10, Fill = _dotBrush, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        _dot = new Shapes.Ellipse { Width = 10, Height = 10, Fill = _dotBrush, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        _spinner = new Shapes.Path
        {
            Data = Geometry.Parse("M6,1 A5,5 0 1 1 1,6"),
            Width = 12,
            Height = 12,
            Stroke = (Brush)Application.Current.Resources["AccentBrush"],
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        var light = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 9, 0) };
        light.Children.Add(_halo);
        light.Children.Add(_dot);
        light.Children.Add(_spinner);
        _halo.HorizontalAlignment = _dot.HorizontalAlignment = HorizontalAlignment.Center;
        _halo.VerticalAlignment = _dot.VerticalAlignment = VerticalAlignment.Center;

        _time = new RollingText { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        TextElement.SetFontFamily(_time, (FontFamily)Application.Current.Resources["DisplayFont"]);
        TextElement.SetFontWeight(_time, FontWeights.SemiBold);
        TextElement.SetFontSize(_time, 14);
        _time.SetText("0:00", animate: false);

        // "Paused" slides out from behind the timer.
        _pausedText = new TextBlock
        {
            Text = "Paused",
            Foreground = (Brush)Application.Current.Resources["Text2Brush"],
            FontSize = 12,
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pausedHost = new Border { Child = _pausedText, Width = 0, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };

        // Grip: drag anywhere on the dot or timer to move the bar out of the way.
        var grip = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "Drag to move", MinWidth = 70 };
        grip.Children.Add(light);
        grip.Children.Add(_time);
        grip.Children.Add(_pausedHost);
        grip.MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch (InvalidOperationException) { /* mouse already released */ }
        };
        _row.Children.Add(grip);
        _row.Children.Add(Divider());

        _buttons = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true };
        _pause = IconButton("IconPause", "Pause");
        _pauseIcon = (Shapes.Path)_pause.Content;
        _pause.Click += (_, _) => _controller.TogglePause();
        _buttons.Children.Add(_pause);

        _stop = IconButton("IconStop", "Stop and save");
        _stop.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        ((Shapes.Path)_stop.Content).Fill = (Brush)Application.Current.Resources["DangerBrush"];
        _stop.Click += (_, _) => _controller.Stop();
        _buttons.Children.Add(_stop);

        if (recorder.HasMicrophone || recorder.HasSystemAudio)
        {
            _buttons.Children.Add(Divider());
            if (recorder.HasMicrophone)
                _buttons.Children.Add(MuteButton("IconMic", "microphone", muted => recorder.MicMuted = muted));
            if (recorder.HasSystemAudio)
                _buttons.Children.Add(MuteButton("IconSpeaker", "system audio", muted => recorder.SystemMuted = muted));
        }

        _buttons.Children.Add(Divider());
        _discard = IconButton("IconTrash", "Discard recording");
        _discard.Click += (_, _) => _controller.Discard();
        _buttons.Children.Add(_discard);
        _row.Children.Add(_buttons);

        _pill = new Border
        {
            Style = (Style)Application.Current.Resources["Pill"],
            Padding = new Thickness(4),
            Margin = new Thickness(16), // room for the shadow
            Child = _row,
            Effect = (System.Windows.Media.Effects.Effect)Application.Current.Resources["FloatShadow"],
            RenderTransform = _pillMove,
        };
        Content = _pill;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateTime();
        Loaded += (_, _) => PlayIn();
        Closed += (_, _) => _timer.Stop();
    }

    // ---------- Building ----------

    private static Shapes.Rectangle Divider() => new()
    {
        Style = (Style)Application.Current.Resources["VDivider"],
        Margin = new Thickness(4, 0, 4, 0),
    };

    private static Button IconButton(string icon, string tip) => new()
    {
        Style = (Style)Application.Current.Resources["IconButton"],
        ToolTip = tip,
        Focusable = false,
        Content = new Shapes.Path
        {
            Style = (Style)Application.Current.Resources["Icon"],
            Data = (Geometry)Application.Current.Resources[icon],
            RenderTransformOrigin = new Point(0.5, 0.5),
        },
    };

    /// <summary>Mutes a source mid-recording; the icon gets struck through while muted.</summary>
    private static Button MuteButton(string icon, string what, Action<bool> apply)
    {
        var glyph = new StrikeIcon(icon);
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            ToolTip = $"Mute {what}",
            Focusable = false,
            Content = glyph,
        };
        button.Click += (_, _) =>
        {
            bool muted = !glyph.Struck;
            apply(muted);
            glyph.SetStruck(muted);
            button.ToolTip = muted ? $"Unmute {what}" : $"Mute {what}";
        };
        return button;
    }

    // ---------- Animation ----------

    /// <summary>The pill rises in with a spring, then its contents settle in one after another.</summary>
    private void PlayIn()
    {
        Fx.PlayEntrance(_pill, 0, offsetY: 16, fromScale: 0.9);
        int i = 0;
        foreach (UIElement child in _row.Children)
        {
            if (child == _buttons)
                foreach (UIElement button in _buttons.Children) Fx.PlayEntrance(button, 90 + 35 * i++, offsetY: 8);
            else
                Fx.PlayEntrance(child, 90 + 35 * i++, offsetY: 8);
        }
    }

    /// <summary>The live dot's halo breathes out every 1.6 s; the dot itself gives a soft beat with it.</summary>
    private void Breathe(bool on)
    {
        var haloScale = (ScaleTransform)_halo.RenderTransform;
        var dotScale = (ScaleTransform)_dot.RenderTransform;
        if (!on)
        {
            haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _halo.BeginAnimation(OpacityProperty, Anim(null, 0, 200));
            dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var period = TimeSpan.FromMilliseconds(1600);
        var spread = new DoubleAnimation(1, 2.6, TimeSpan.FromMilliseconds(1200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var spreadLoop = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
        spreadLoop.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        spreadLoop.KeyFrames.Add(new EasingDoubleKeyFrame(2.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200)), spread.EasingFunction));
        var fadeLoop = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
        fadeLoop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fadeLoop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, spreadLoop);
        haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, spreadLoop);
        _halo.BeginAnimation(OpacityProperty, fadeLoop);

        var beat = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
        dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
    }

    /// <summary>Swaps an icon with a quick shrink-and-spin out and a springy pop back in.</summary>
    private static void Morph(Shapes.Path icon, string key)
    {
        var geometry = (Geometry)Application.Current.Resources[key];
        if (icon.Data == geometry) return;
        var scale = new ScaleTransform();
        var spin = new RotateTransform();
        icon.RenderTransform = new TransformGroup { Children = { scale, spin } };

        var shrink = Anim(1, 0.3, 110, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        shrink.Completed += (_, _) =>
        {
            icon.Data = geometry;
            var grow = Anim(0.3, 1, 340, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            spin.BeginAnimation(RotateTransform.AngleProperty, Anim(-90, 0, 340, new CubicEase { EasingMode = EasingMode.EaseOut }));
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        spin.BeginAnimation(RotateTransform.AngleProperty, Anim(0, 90, 110, null));
    }

    /// <summary>Animates a host's width open to fit its child, or closed.</summary>
    private static void Slide(Border host, bool open)
    {
        double to = 0;
        if (open)
        {
            host.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            to = host.Child.DesiredSize.Width;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        host.BeginAnimation(WidthProperty, Anim(null, to, open ? 260 : 200, ease));
        host.BeginAnimation(OpacityProperty, Anim(null, open ? 1 : 0, open ? 260 : 160, ease));
    }

    private static DoubleAnimation Anim(double? from, double to, int ms, IEasingFunction? ease = null) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromMilliseconds(ms),
        EasingFunction = ease,
    };

    // ---------- State ----------

    /// <summary>Starts the timer and the live dot once frames are flowing.</summary>
    public void Begin()
    {
        _timer.Start();
        UpdateTime();
        SetPaused(false);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Morph(_pauseIcon, paused ? "IconPlay" : "IconPause");
        _pause.ToolTip = paused ? "Resume" : "Pause";

        var color = ((SolidColorBrush)Application.Current.Resources[paused ? "Text3Brush" : "DangerBrush"]).Color;
        _dotBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(250)));
        Breathe(!paused);
        Slide(_pausedHost, paused);
        // While paused the time slowly blinks, like a stopped clock.
        if (paused)
            _time.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        else
            _time.BeginAnimation(OpacityProperty, Anim(null, 1, 150));
        UpdateTime();
    }

    /// <summary>The controls fold away and the dot becomes a spinner while the file is finished.</summary>
    public void ShowSaving()
    {
        _timer.Stop();
        Breathe(false);
        _time.BeginAnimation(OpacityProperty, Anim(null, 1, 150));
        Slide(_pausedHost, false);
        _dot.BeginAnimation(OpacityProperty, Anim(null, 0, 150));
        _spinner.BeginAnimation(OpacityProperty, Anim(0, 1, 200));
        ((RotateTransform)_spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
        _time.SetText("Saving…", animate: true);

        _buttons.IsEnabled = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _buttons.BeginAnimation(WidthProperty, Anim(_buttons.ActualWidth, 0, 280, ease));
        _buttons.BeginAnimation(OpacityProperty, Anim(null, 0, 180));
        if (_row.Children.Count > 1 && _row.Children[1] is UIElement divider) divider.BeginAnimation(OpacityProperty, Anim(null, 0, 180));
    }

    /// <summary>Sinks and fades away, then closes.</summary>
    public override void Dismiss()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _pillMove.BeginAnimation(TranslateTransform.YProperty, Anim(null, 12, 180, ease));
        base.Dismiss();
    }

    private void UpdateTime()
    {
        var t = _controller.Elapsed;
        _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>
    /// Below the recorded area if there's room, else above it, else inside along the bottom edge
    /// (it's excluded from the capture, so overlapping is fine).
    /// </summary>
    protected override void Place()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;

        var center = new Point(_area.X + _area.Width / 2.0, _area.Y + _area.Height / 2.0);
        var monitors = MonitorInfo.GetAll();
        var monitor = monitors.FirstOrDefault(m => RectUtil.Contains(m.Bounds, center)) ?? monitors.First(m => m.IsPrimary);
        var work = monitor.WorkArea;
        int gap = (int)(4 * dpi.DpiScaleY); // the pill's margin already leaves some space

        int x = Math.Clamp((int)center.X - w / 2, work.X, Math.Max(work.X, work.X + work.Width - w));
        int y = _area.Y + _area.Height + gap;
        if (y + h > work.Y + work.Height)
        {
            y = _area.Y - h - gap;
            if (y < work.Y) y = Math.Min(_area.Y + _area.Height, work.Y + work.Height) - h - (int)(12 * dpi.DpiScaleY);
        }
        if (_placed) return; // keep wherever the user dragged it
        _placed = true;
        SetBounds(new Int32Rect(x, y, w, h));
    }
}
