using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumina.Native;
using Lumina.Recording;
using Lumina.Services;
using Lumina.Views;
using Shapes = System.Windows.Shapes;

namespace Lumina.Capture;

/// <summary>Borderless window covering exactly one monitor, showing the frozen screenshot.</summary>
internal sealed class OverlayWindow : Window
{
    private readonly CaptureSession _session;
    private readonly OverlaySurface _surface;
    private readonly Canvas _fxLayer = new() { IsHitTestVisible = false };
    private readonly FrameworkElement? _toolbar;
    private TextBlock? _hint;
    private readonly Dictionary<CaptureMode, RadioButton> _modeButtons = new();
    // The open device list and the chevron that opened it. A plain popup rather than a ContextMenu: a menu grabs
    // the mouse while open, which swallowed clicks on the chevron instead of letting it close the list.
    private Popup? _audioPopup;
    private Button? _audioArrow;
    private bool _toolbarHere;

    public MonitorInfo Monitor { get; }

    public OverlayWindow(CaptureSession session, MonitorInfo monitor, bool showToolbar)
    {
        _session = session;
        Monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        var image = session.Snapshot.Crop(monitor.Bounds);
        var background = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.NearestNeighbor);

        _surface = new OverlaySurface(session, monitor, image);
        var root = new Grid();
        root.Children.Add(background);
        root.Children.Add(_surface);
        root.Children.Add(_fxLayer);
        if (showToolbar)
        {
            _toolbar = BuildToolbar();
            root.Children.Add(_toolbar);
            session.ModeChanged += SyncModeButtons;
        }
        Content = root;

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => Place();
        PreviewKeyDown += (_, e) =>
        {
            if (_audioPopup?.IsOpen == true)
            {
                // Esc closes the device list, not the whole capture.
                if (e.Key == Key.Escape) CloseAudioList();
                e.Handled = true;
                return;
            }
            _session.HandleKeyDown(e);
        };
        PreviewMouseDown += (_, e) =>
        {
            if (_audioPopup?.IsOpen != true) return;
            var hit = e.OriginalSource as DependencyObject;
            if (IsInside(hit, _audioArrow)) return; // the chevron toggles the list itself
            CloseAudioList();
            // A click elsewhere on the screen only dismisses the list; it doesn't start a selection.
            if (!IsInside(hit, _toolbar)) e.Handled = true;
        };
        PreviewKeyUp += (_, e) => _session.HandleKeyUp(e);
        Deactivated += (_, _) => CloseAudioList();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(_session.CheckFocus, DispatcherPriority.Background);
        Closed += (_, _) => session.ModeChanged -= SyncModeButtons;
    }

    /// <summary>Pins the window to the monitor in physical pixels, regardless of DPI.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = Monitor.Bounds;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_NOOWNERZORDER);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes to the "suggested" rect on DPI changes; snap back to the monitor.
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Send);
    }

    public void Refresh()
    {
        _surface.InvalidateVisual();
        if (_toolbar != null)
        {
            bool here = _session.ToolbarMonitor == Monitor;
            bool hide = !here || _session.IsSelecting || _session.IsFinishing;
            _toolbar.IsHitTestVisible = !hide;
            Fx.SetShow(_toolbar, !hide); // fades out while dragging a selection, back in after
            if (here && !_toolbarHere && !hide && _toolbar.IsLoaded)
                Fx.PlayEntrance(_toolbar, 0, offsetY: -18, fromScale: 0.94); // drops in on the screen the cursor moved to
            _toolbarHere = here;
            if (!here) CloseAudioList();
        }
    }

    // ---------- Capture "shutter" ----------

    /// <summary>
    /// Plays the capture-confirm animation: a flash over the captured area, the dim lifting away, and
    /// (when <paramref name="fly"/>) the capture shrinking into the corner where the quick preview appears.
    /// </summary>
    public Task PlayCaptureAsync(Int32Rect captured, BitmapSource image, bool fly)
    {
        var done = new TaskCompletionSource();
        _surface.PlayCapture(flash: !fly);

        if (!fly)
        {
            Delay(240, done);
            return done.Task;
        }

        var start = _surface.ToLocal(captured);
        var area = _surface.ToLocal(Monitor.WorkArea);
        // Where QuickPreviewWindow's thumbnail lands: bottom-right, inside its card margins.
        double scale = Math.Min(1, Math.Min(300 / start.Width, 190 / start.Height));
        double w = start.Width * scale, h = start.Height * scale;
        var end = new Rect(area.Right - 30 - w, area.Bottom - 74 - h, w, h);

        var card = new FlightCard(image, start, end) { Width = ActualWidth, Height = ActualHeight };
        _fxLayer.Children.Add(card);
        var clock = new DoubleAnimation(0, FlightCard.Total, TimeSpan.FromMilliseconds(FlightCard.Total));
        clock.Completed += (_, _) => done.TrySetResult();
        card.BeginAnimation(FlightCard.TimeProperty, clock);

        Delay(1200, done); // safety net
        return done.Task;
    }

    private void Delay(int ms, TaskCompletionSource done)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            done.TrySetResult();
        };
        timer.Start();
    }

    // ---------- Toolbar ----------

    private FrameworkElement BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (_session.ForRecording)
        {
            // A red "REC" tag so it's obvious this selection starts a recording.
            var tag = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            tag.Children.Add(new Shapes.Ellipse { Width = 9, Height = 9, Fill = Res("DangerBrush", Brushes.Red), VerticalAlignment = VerticalAlignment.Center });
            tag.Children.Add(new TextBlock { Text = "Record", FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush", Brushes.White), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(tag);
            panel.Children.Add(new Shapes.Rectangle { Style = Application.Current.TryFindResource("VDivider") as Style, Margin = new Thickness(4, 0, 6, 0) });
        }
        var toggleStyle = Application.Current.TryFindResource("ToolRadio") as Style;

        void AddMode(CaptureMode mode, string icon, string tip)
        {
            var button = new RadioButton
            {
                GroupName = "capture-mode",
                Content = MakeIcon(icon),
                ToolTip = tip,
                IsChecked = _session.Mode == mode,
                Padding = new Thickness(10, 7, 10, 7),
                Focusable = false,
                Cursor = Cursors.Hand,
            };
            if (toggleStyle != null) button.Style = toggleStyle;
            button.Checked += (_, _) => _session.SetMode(mode);
            _modeButtons[mode] = button;
            panel.Children.Add(button);
        }

        AddMode(CaptureMode.Region, "IconRegion", "Region (1)");
        AddMode(CaptureMode.Window, "IconWindow", "Window (2)");
        AddMode(CaptureMode.Screen, "IconScreen", "Full screen (3)");
        if (!_session.ForRecording) AddMode(CaptureMode.Freeform, "IconFreeform", "Freeform (4)");
        if (_session.ForRecording)
        {
            panel.Children.Add(new Shapes.Rectangle { Style = Application.Current.TryFindResource("VDivider") as Style, Margin = new Thickness(6, 0, 4, 0) });
            panel.Children.Add(AudioControl(AudioFlow.Output));
            panel.Children.Add(AudioControl(AudioFlow.Input));
        }

        _hint = new TextBlock
        {
            Text = HintFor(_session.Mode, _session.ForRecording),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            MinWidth = 210,
            FontSize = 12,
            Foreground = Res("Text2Brush", Brushes.Gray),
        };
        panel.Children.Add(_hint);

        var close = new Button
        {
            Content = MakeIcon("IconClose"),
            ToolTip = "Cancel (Esc)",
            Style = Application.Current.TryFindResource("IconButton") as Style,
            Focusable = false,
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => _session.Cancel();
        panel.Children.Add(close);

        var pill = new Border
        {
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Cursor = Cursors.Arrow,
        };
        if (Application.Current.TryFindResource("Pill") is Style pillStyle) pill.Style = pillStyle;
        TextElement.SetFontFamily(pill, (FontFamily)Application.Current.Resources["UiFont"]);
        _toolbarHere = _session.ToolbarMonitor == Monitor;
        Fx.SetShow(pill, _toolbarHere);
        pill.IsHitTestVisible = _toolbarHere;
        pill.Loaded += (_, _) =>
        {
            if (_toolbarHere) Fx.PlayEntrance(pill, 60, offsetY: -18, fromScale: 0.94);
        };
        return pill;
    }

    // ---------- Recording sound ----------

    /// <summary>
    /// A split control: the icon turns the source on or off (struck through when off, like Snipping Tool's),
    /// the chevron beside it opens the device list.
    /// </summary>
    private FrameworkElement AudioControl(AudioFlow flow)
    {
        bool output = flow == AudioFlow.Output;
        string kind = output ? "System audio" : "Microphone";
        bool IsOn() => output ? App.Settings.RecordSystemAudio : App.Settings.RecordMicrophone;

        var glyph = new StrikeIcon(output ? "IconSpeaker" : "IconMic");
        var toggle = new Button
        {
            Content = glyph,
            Style = Application.Current.TryFindResource("IconButton") as Style,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = "",
        };

        void Sync(bool animate) => glyph.SetStruck(!IsOn(), animate);
        Sync(false);

        toggle.ToolTipOpening += (_, _) =>
        {
            if (!IsOn())
            {
                toggle.ToolTip = $"{kind} is off · click to turn on";
                return;
            }
            string? id = output ? App.Settings.SystemAudioDevice : App.Settings.MicrophoneDevice;
            string device = id == null
                ? AudioDevices.Default(flow)?.Name ?? "default device"
                : AudioDevices.List(flow).Find(d => d.Id == id)?.Name ?? "unplugged device";
            toggle.ToolTip = $"{kind}: {device} · click to turn off";
        };
        toggle.Click += (_, _) =>
        {
            if (output) App.Settings.RecordSystemAudio = !App.Settings.RecordSystemAudio;
            else App.Settings.RecordMicrophone = !App.Settings.RecordMicrophone;
            SettingsService.Save(App.Settings);
            Sync(true);
        };

        // The chevron flips while its list is open.
        var flip = new RotateTransform();
        var chevron = new Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            Stroke = Res("Text2Brush", Brushes.Gray),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 8,
            Height = 4,
            Stretch = Stretch.None,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = flip,
        };
        var arrow = new Button
        {
            Content = chevron,
            Style = Application.Current.TryFindResource("IconButton") as Style,
            Padding = new Thickness(5, 7, 5, 7),
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = output ? "Choose the output to record" : "Choose the microphone",
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        void Flip(bool open) => flip.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open ? 180 : 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // The device list: a card in a popup, refilled each time it opens.
        var list = new StackPanel();
        var card = new Border
        {
            Child = list,
            Margin = new Thickness(10, 6, 14, 16), // room for the shadow
            Padding = new Thickness(5),
            MinWidth = 196,
            CornerRadius = new CornerRadius(12),
            Background = Res("RaisedBrush", Brushes.Black),
            BorderBrush = Res("StrokeStrongBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Effect = Application.Current.TryFindResource("FloatShadow") as Effect,
        };
        // The entrance animation goes on the card, not the popup's child: a popup places itself using its child's transform.
        var host = new Grid { Children = { card } };
        TextElement.SetFontFamily(host, (FontFamily)Application.Current.Resources["UiFont"]);
        TextElement.SetFontSize(host, 13);
        var popup = new Popup
        {
            Child = host,
            PlacementTarget = toggle,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -10, // cancel the shadow margin so the card lines up with the control
            VerticalOffset = -2,
            AllowsTransparency = true,
            StaysOpen = true, // closed by us: chevron, Esc, or a click elsewhere
            PopupAnimation = PopupAnimation.None,
            Focusable = false,
        };
        popup.Opened += (_, _) =>
        {
            Flip(true);
            Fx.PlayEntrance(card, 0, offsetY: -6, fromScale: 0.96);
        };
        popup.Closed += (_, _) =>
        {
            Flip(false);
            if (_audioPopup == popup)
            {
                _audioPopup = null;
                _audioArrow = null;
            }
        };
        arrow.Click += (_, _) =>
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                return;
            }
            CloseAudioList(); // the other source's list
            FillAudioList(list, flow, () =>
            {
                popup.IsOpen = false;
                Sync(true);
            });
            _audioPopup = popup;
            _audioArrow = arrow;
            popup.IsOpen = true;
        };

        var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0) };
        group.Children.Add(toggle);
        group.Children.Add(arrow);
        return group;
    }

    private void CloseAudioList()
    {
        if (_audioPopup != null) _audioPopup.IsOpen = false;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        while (d != null)
        {
            if (d == ancestor) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static void FillAudioList(StackPanel list, AudioFlow flow, Action changed)
    {
        var s = App.Settings;
        bool output = flow == AudioFlow.Output;
        bool on = output ? s.RecordSystemAudio : s.RecordMicrophone;
        string? selected = output ? s.SystemAudioDevice : s.MicrophoneDevice;
        list.Children.Clear();
        var rowStyle = Application.Current.TryFindResource("MenuRowButton") as Style;

        void AddDevice(string name, string? id)
        {
            var row = new DockPanel();
            // The chosen device is ticked; dimmed while the source is off.
            var check = Check(id != selected ? 0 : on ? 1 : 0.4);
            check.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(new TextBlock { Text = name, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var item = new Button { Content = row, Style = rowStyle, ToolTip = name };
            item.Click += (_, _) =>
            {
                // Picking a device also turns the source on.
                if (output)
                {
                    s.SystemAudioDevice = id;
                    s.RecordSystemAudio = true;
                }
                else
                {
                    s.MicrophoneDevice = id;
                    s.RecordMicrophone = true;
                }
                SettingsService.Save(s);
                changed();
            };
            list.Children.Add(item);
        }

        AddDevice(AudioDevices.DefaultLabel(flow), null);
        var devices = AudioDevices.List(flow);
        foreach (var device in devices) AddDevice(device.Name, device.Id);
        if (selected != null && !devices.Exists(d => d.Id == selected)) AddDevice("Unplugged device", selected);
    }

    private static Shapes.Path Check(double opacity = 1) => new()
    {
        Data = Geometry.Parse("M3,9.5 L7,13.5 L15,5"),
        Stroke = Res("AccentTextBrush", Brushes.White),
        StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Width = 18,
        Height = 18,
        Opacity = opacity,
    };

    private static string HintFor(CaptureMode mode, bool record) => (mode, record) switch
    {
        (CaptureMode.Region, true) => "Drag an area to record · click a window",
        (CaptureMode.Window, true) => "Click a window to record it",
        (CaptureMode.Screen, true) => "Click a screen to record · F for this one",
        (CaptureMode.Region, _) => "Drag to select · click a window",
        (CaptureMode.Window, _) => "Click a window to capture it",
        (CaptureMode.Screen, _) => "Click a screen · F for this one",
        (CaptureMode.Freeform, _) => "Draw around anything",
        _ => "",
    };

    private void SyncModeButtons()
    {
        if (_modeButtons.TryGetValue(_session.Mode, out var button)) button.IsChecked = true;
        if (_hint != null)
        {
            _hint.Text = HintFor(_session.Mode, _session.ForRecording);
            Fx.PlayEntrance(_hint, 0, offsetY: 5);
        }
    }

    private static Shapes.Path MakeIcon(string key) => new()
    {
        Data = (Geometry)Application.Current.Resources[key],
        Style = (Style)Application.Current.Resources["Icon"],
    };

    private static Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;
}

/// <summary>Draws the dimming, selection, hover highlight and magnifier on top of the frozen screen.</summary>
internal sealed class OverlaySurface : FrameworkElement
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x9A, 0x07, 0x06, 0x10)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1B, 0x29)));
    private static readonly Brush FlashBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>0 → 1 as the overlay settles in on open.</summary>
    public static readonly DependencyProperty IntroProperty = DependencyProperty.Register(
        nameof(Intro), typeof(double), typeof(OverlaySurface), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>0 → 1 as everything lifts away after a capture.</summary>
    public static readonly DependencyProperty OutroProperty = DependencyProperty.Register(
        nameof(Outro), typeof(double), typeof(OverlaySurface), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Strength of the white shutter flash over the captured area.</summary>
    public static readonly DependencyProperty FlashProperty = DependencyProperty.Register(
        nameof(Flash), typeof(double), typeof(OverlaySurface), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Intro { get => (double)GetValue(IntroProperty); set => SetValue(IntroProperty, value); }
    public double Outro { get => (double)GetValue(OutroProperty); set => SetValue(OutroProperty, value); }
    public double Flash { get => (double)GetValue(FlashProperty); set => SetValue(FlashProperty, value); }

    private readonly CaptureSession _session;
    private readonly MonitorInfo _monitor;
    private readonly BitmapSource _image;
    private readonly Brush _accent, _hoverFill;
    private readonly Pen _selectionPen, _hoverPen, _bracketPen, _guidePen, _loupeBorder, _gridPen, _centerPenDark, _centerPenLight;
    private readonly SoftRings _glow, _loupeGlow;
    private readonly Pen[] _lassoGlow;

    // The highlight box glides toward its target instead of jumping between windows.
    private Rect? _shown, _target;
    private bool _ticking;
    private TimeSpan _lastTick;
    // 0 → 1 as a highlight first appears (fades in while settling onto its target).
    private double _appear = 1;
    private const double AppearSeconds = 0.32;

    public OverlaySurface(CaptureSession session, MonitorInfo monitor, BitmapSource image)
    {
        _session = session;
        _monitor = monitor;
        _image = image;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        var accentColor = (Color)Application.Current.Resources["AccentColor"];
        _accent = Freeze(new SolidColorBrush(accentColor));
        _hoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1C, accentColor.R, accentColor.G, accentColor.B)));
        _selectionPen = Freeze(new Pen(_accent, 2));
        _hoverPen = Freeze(new Pen(_accent, 2));
        _bracketPen = Freeze(new Pen(Brushes.White, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        _guidePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) });
        _glow = new SoftRings(accentColor, 0.42, 22, 22);
        _lassoGlow = new SoftRings(accentColor, 0.4, 16, 12).Centered();
        _loupeGlow = new SoftRings(Colors.Black, 0.45, 18, 14);
        _loupeBorder = Freeze(new Pen(Brushes.White, 2));
        _gridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1));
        _centerPenDark = Freeze(new Pen(Brushes.Black, 1));
        _centerPenLight = Freeze(new Pen(Brushes.White, 1));

        Loaded += (_, _) => BeginAnimation(IntroProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Unloaded += (_, _) => StopTicking();
    }

    private double ScaleX => ActualWidth > 0 ? _monitor.Bounds.Width / ActualWidth : 1;
    private double ScaleY => ActualHeight > 0 ? _monitor.Bounds.Height / ActualHeight : 1;

    private Point ToPhysical(Point local) => new(_monitor.Bounds.X + local.X * ScaleX, _monitor.Bounds.Y + local.Y * ScaleY);
    private Point ToLocal(Point physical) => new((physical.X - _monitor.Bounds.X) / ScaleX, (physical.Y - _monitor.Bounds.Y) / ScaleY);
    public Rect ToLocal(Int32Rect r) => new(ToLocal(new Point(r.X, r.Y)), ToLocal(new Point(r.X + r.Width, r.Y + r.Height)));

    public void PlayCapture(bool flash)
    {
        StopTicking();
        if (flash) BeginAnimation(FlashProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), new CubicEase { EasingMode = EasingMode.EaseOut }),
            },
        });
        BeginAnimation(OutroProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = TimeSpan.FromMilliseconds(60),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ---------- Input ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        _session.BeginSelection(ToPhysical(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _session.UpdateCursor(ToPhysical(e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _session.EndSelection(ToPhysical(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _session.RightClick();
        e.Handled = true;
    }

    // ---------- Glide ----------

    private void SetTarget(Rect? target, bool snap)
    {
        _target = target;
        if (target != null && _shown == null && _session.FinalRect == null)
        {
            _appear = 0; // nothing shown yet: fade in instead of popping on
            StartTicking();
        }
        if (target == null || snap || _shown == null)
        {
            _shown = target;
            if (target == null) _appear = 1;
            return;
        }
        if (_shown != target) StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastTick == TimeSpan.Zero ? 1 / 60.0 : (now - _lastTick).TotalSeconds;
        if (dt <= 0) return;
        _lastTick = now;
        if (_appear < 1) _appear = Math.Min(1, _appear + dt / AppearSeconds);
        if (_target is not Rect t || _shown is not Rect c)
        {
            StopTicking();
            return;
        }

        double k = 1 - Math.Exp(-dt * 22); // frame-rate independent ease toward the target
        var next = new Rect(c.X + (t.X - c.X) * k, c.Y + (t.Y - c.Y) * k,
            Math.Max(0, c.Width + (t.Width - c.Width) * k), Math.Max(0, c.Height + (t.Height - c.Height) * k));
        if (Math.Abs(next.X - t.X) + Math.Abs(next.Y - t.Y) + Math.Abs(next.Width - t.Width) + Math.Abs(next.Height - t.Height) < 1)
        {
            next = t;
            if (_appear >= 1) StopTicking();
        }
        _shown = next;
        InvalidateVisual();
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= Tick;
    }

    // ---------- Rendering ----------

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, full); // makes the whole surface hit-testable

        Rect? target = null;
        string? label = null;
        bool solid = false;
        Geometry? lasso = null;

        if (_session.FinalRect is Int32Rect final)
        {
            if (_session.Mode == CaptureMode.Freeform && _session.Lasso.Count > 2) lasso = BuildLasso();
            else target = ToLocal(final);
            solid = true;
        }
        else if (_session.Mode == CaptureMode.Freeform)
        {
            if (_session.Lasso.Count > 1) lasso = BuildLasso();
        }
        else if (_session.HasSelection)
        {
            var r = _session.SelectionRect;
            target = ToLocal(r);
            label = $"{r.Width} × {r.Height}";
            solid = true;
        }
        else if (_session.Mode is CaptureMode.Region or CaptureMode.Window && _session.HoverWindow is { } w)
        {
            target = ToLocal(w.Bounds);
            label = string.IsNullOrWhiteSpace(w.Title) ? $"{w.Bounds.Width} × {w.Bounds.Height}" : Truncate(w.Title, 60);
        }
        else if (_session.Mode == CaptureMode.Screen && _session.HoverMonitor == _monitor)
        {
            target = full;
            label = $"{_monitor.Bounds.Width} × {_monitor.Bounds.Height}";
        }

        SetTarget(target, snap: solid);
        var box = _shown;
        // Ease-out on the appearance: starts slightly larger and transparent, settles onto the target.
        double appear = 1 - Math.Pow(1 - _appear, 3);
        if (box is Rect settling && appear < 1)
        {
            double grow = 14 * (1 - appear);
            settling.Inflate(grow, grow);
            box = settling;
        }

        double intro = Intro, outro = Outro;
        double presence = intro * (1 - outro);

        // Dim everything except the box/lasso.
        Geometry dim = new RectangleGeometry(full);
        if (box is Rect hole) dim = new CombinedGeometry(GeometryCombineMode.Exclude, dim, new RectangleGeometry(hole));
        else if (lasso != null) dim = new CombinedGeometry(GeometryCombineMode.Exclude, dim, lasso);
        dc.PushOpacity(presence);
        dc.DrawGeometry(DimBrush, null, dim);
        if (box is Rect clearing && appear < 1)
        {
            // The hole brightens gradually rather than snapping open.
            dc.PushOpacity(1 - appear);
            dc.DrawRectangle(DimBrush, null, clearing);
            dc.Pop();
        }
        dc.Pop();

        dc.PushOpacity(presence);
        if (box is Rect b)
        {
            dc.PushOpacity(appear);
            if (!solid) dc.DrawRectangle(_hoverFill, null, b);
            _glow.DrawAround(dc, b);
            var inner = new Rect(b.X + 1, b.Y + 1, Math.Max(0, b.Width - 2), Math.Max(0, b.Height - 2));
            dc.DrawRectangle(null, solid ? _selectionPen : _hoverPen, inner);
            if (solid && _session.FinalRect == null) DrawBrackets(dc, b);
            if (label != null && b.IntersectsWith(full)) DrawLabel(dc, b, label, solid);
            dc.Pop();
        }
        if (lasso != null)
        {
            foreach (var glow in _lassoGlow) dc.DrawGeometry(null, glow, lasso);
            dc.DrawGeometry(null, _selectionPen, lasso);
        }

        if (_session.FinalRect == null && _session.Cursor is Point c && _session.HoverMonitor == _monitor)
        {
            if (_session.Mode is CaptureMode.Region or CaptureMode.Freeform && !_session.IsSelecting) DrawGuides(dc, c);
            if (_session.Settings.ShowMagnifier) DrawLoupe(dc, c);
        }
        dc.Pop();

        // Shutter flash over whatever was captured.
        if (Flash > 0.001)
        {
            dc.PushOpacity(Flash);
            if (lasso != null) dc.DrawGeometry(FlashBrush, null, lasso);
            else if (box is Rect fb) dc.DrawRectangle(FlashBrush, null, fb);
            dc.Pop();
        }
    }

    private static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    /// <summary>White L-shaped corner handles on an active selection.</summary>
    private void DrawBrackets(DrawingContext dc, Rect b)
    {
        double len = Math.Min(18, Math.Min(b.Width, b.Height) / 3);
        if (len < 4) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            void Corner(Point corner, double dx, double dy)
            {
                ctx.BeginFigure(new Point(corner.X + dx * len, corner.Y), false, false);
                ctx.LineTo(corner, true, true);
                ctx.LineTo(new Point(corner.X, corner.Y + dy * len), true, true);
            }
            Corner(b.TopLeft, 1, 1);
            Corner(b.TopRight, -1, 1);
            Corner(b.BottomLeft, 1, -1);
            Corner(b.BottomRight, -1, -1);
        }
        g.Freeze();
        dc.DrawGeometry(null, _bracketPen, g);
    }

    /// <summary>Full-screen alignment guides through the cursor before a drag starts.</summary>
    private void DrawGuides(DrawingContext dc, Point physical)
    {
        var c = ToLocal(physical);
        double x = Math.Floor(c.X) + 0.5, y = Math.Floor(c.Y) + 0.5;
        dc.DrawLine(_guidePen, new Point(x, 0), new Point(x, ActualHeight));
        dc.DrawLine(_guidePen, new Point(0, y), new Point(ActualWidth, y));
    }

    private Geometry BuildLasso()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var pts = _session.Lasso;
            ctx.BeginFigure(ToLocal(pts[0]), true, !_session.IsSelecting);
            ctx.PolyLineTo(pts.Skip(1).Select(ToLocal).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Selections get their size at the bottom-right corner; hover targets get a title tag above.</summary>
    private void DrawLabel(DrawingContext dc, Rect anchor, string text, bool selection)
    {
        var ft = Text(text, LabelFace, 12, Brushes.White);
        double dot = selection ? 14 : 0;
        double w = ft.Width + 18 + dot, h = ft.Height + 10;
        double x, y;
        if (selection)
        {
            x = Math.Clamp(anchor.Right - w, 6, Math.Max(6, ActualWidth - w - 6));
            y = anchor.Bottom + 8;
            if (y + h > ActualHeight - 6) y = Math.Max(6, anchor.Bottom - h - 8);
        }
        else
        {
            x = Math.Clamp(anchor.X, 6, Math.Max(6, ActualWidth - w - 6));
            y = anchor.Y - h - 8;
            if (y < 6) y = Math.Max(6, anchor.Y + 8);
        }
        var rect = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(LabelBrush, null, rect, h / 2, h / 2);
        if (selection) dc.DrawEllipse(_accent, null, new Point(x + 13, y + h / 2), 4, 4);
        dc.DrawText(ft, new Point(x + 9 + dot, y + 5));
    }

    private void DrawLoupe(DrawingContext dc, Point physical)
    {
        const double size = 128, zoom = 8, gap = 26, infoHeight = 44;
        var c = ToLocal(physical);
        double x = c.X + gap, y = c.Y + gap;
        if (x + size > ActualWidth - 4) x = c.X - gap - size;
        if (y + size + infoHeight > ActualHeight - 4) y = c.Y - gap - size - infoHeight;
        var box = new Rect(x, y, size, size);

        // Draw the whole monitor image scaled up and clipped, centred on the pixel under the cursor.
        double px = Math.Floor(physical.X) - _monitor.Bounds.X, py = Math.Floor(physical.Y) - _monitor.Bounds.Y;
        dc.PushClip(new RectangleGeometry(box, 12, 12));
        dc.DrawRectangle(Brushes.Black, null, box);
        dc.DrawImage(_image, new Rect(
            box.X + size / 2 - (px + 0.5) * zoom, box.Y + size / 2 - (py + 0.5) * zoom,
            _image.PixelWidth * zoom, _image.PixelHeight * zoom));
        for (double gx = box.X + size / 2 - zoom / 2 - zoom * 8; gx < box.Right; gx += zoom)
            dc.DrawLine(_gridPen, new Point(gx, box.Top), new Point(gx, box.Bottom));
        for (double gy = box.Y + size / 2 - zoom / 2 - zoom * 8; gy < box.Bottom; gy += zoom)
            dc.DrawLine(_gridPen, new Point(box.Left, gy), new Point(box.Right, gy));
        var center = new Rect(box.X + size / 2 - zoom / 2, box.Y + size / 2 - zoom / 2, zoom, zoom);
        dc.DrawRectangle(null, _centerPenDark, new Rect(center.X - 1, center.Y - 1, zoom + 2, zoom + 2));
        dc.DrawRectangle(null, _centerPenLight, center);
        dc.Pop();
        _loupeGlow.DrawAround(dc, box, 12);
        dc.DrawRoundedRectangle(null, _loupeBorder, box, 12, 12);

        var color = _session.Snapshot.GetPixel((int)Math.Floor(physical.X), (int)Math.Floor(physical.Y));
        var info = new Rect(box.X, box.Bottom + 6, size, infoHeight - 6);
        dc.DrawRoundedRectangle(LabelBrush, null, info, 8, 8);
        dc.DrawRoundedRectangle(new SolidColorBrush(color), _centerPenLight, new Rect(info.X + 8, info.Y + 9, 20, 20), 5, 5);
        dc.DrawText(Text($"#{color.R:X2}{color.G:X2}{color.B:X2}", MonoFace, 12, Brushes.White), new Point(info.X + 36, info.Y + 4));
        dc.DrawText(Text($"{(int)physical.X}, {(int)physical.Y}", MonoFace, 11, Brushes.LightGray), new Point(info.X + 36, info.Y + 20));
    }

    private FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
