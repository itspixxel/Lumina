using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Native;
using Lumina.Services;

namespace Lumina.Capture;

public enum CaptureMode { Region, Window, Screen, Freeform }

public sealed record CaptureResult(BitmapSource Image, string? AppName);

/// <summary>
/// One run of the capture overlay. The screen is frozen up front, then one overlay window per monitor
/// lets the user pick a region, window, monitor or freeform shape. All coordinates are physical
/// virtual-desktop pixels, so selections can cross monitors with different scaling.
/// </summary>
internal sealed class CaptureSession
{
    private readonly List<OverlayWindow> _overlays = new();
    private readonly TaskCompletionSource<CaptureResult?> _tcs = new();
    private readonly string? _foregroundApp;
    private Point _dragStart, _dragCurrent, _spaceAnchor;
    private bool _moved, _spaceHeld, _done;
    private WindowInfo? _selectionWindow;

    public ScreenSnapshot Snapshot { get; }
    public IReadOnlyList<WindowInfo> Windows { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public AppSettings Settings { get; }
    public CaptureMode Mode { get; private set; }
    public Point? Cursor { get; private set; }
    public bool IsSelecting { get; private set; }
    public bool HasSelection => IsSelecting && _moved;
    public List<Point> Lasso { get; } = new();
    public WindowInfo? HoverWindow { get; private set; }
    public MonitorInfo? HoverMonitor { get; private set; }
    public Int32Rect SelectionRect => RectUtil.FromPoints(_dragStart, _dragCurrent);
    /// <summary>The area being captured, set once the user commits; drives the shutter animation.</summary>
    public Int32Rect? FinalRect { get; private set; }
    public bool IsFinishing => FinalRect != null;

    public event Action? ModeChanged;

    private CaptureSession(ScreenSnapshot snapshot, List<WindowInfo> windows, List<MonitorInfo> monitors,
        AppSettings settings, CaptureMode mode, string? foregroundApp)
    {
        Snapshot = snapshot;
        Windows = windows;
        Monitors = monitors;
        Settings = settings;
        Mode = mode;
        _foregroundApp = foregroundApp;
    }

    public static Task<CaptureResult?> RunAsync(CaptureMode mode, AppSettings settings)
    {
        string? foreground = WindowFinder.GetForegroundProcessName();
        var snapshot = ScreenCapture.CaptureVirtualScreen(settings.CaptureCursor);
        var session = new CaptureSession(snapshot, WindowFinder.GetWindows(), MonitorInfo.GetAll(), settings, mode, foreground);
        session.Open();
        return session._tcs.Task;
    }

    private void Open()
    {
        var cursor = NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : new Point(0, 0);
        var startMonitor = MonitorAt(cursor) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors[0];

        foreach (var monitor in Monitors)
        {
            var overlay = new OverlayWindow(this, monitor, showToolbar: monitor == startMonitor);
            _overlays.Add(overlay);
            overlay.Show();
        }

        UpdateCursor(cursor);
        var active = _overlays.First(o => o.Monitor == startMonitor);
        active.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(active).Handle);
        active.Focus();
    }

    public MonitorInfo? MonitorAt(Point p) => Monitors.FirstOrDefault(m => RectUtil.Contains(m.Bounds, p));

    public void SetMode(CaptureMode mode)
    {
        if (_done) return; // capture animation is playing
        if (Mode == mode) return;
        Mode = mode;
        IsSelecting = false;
        Lasso.Clear();
        ModeChanged?.Invoke();
        Invalidate();
    }

    public void UpdateCursor(Point p)
    {
        if (_done) return; // capture animation is playing
        var vs = Snapshot.Bounds;
        p = new Point(Math.Clamp(p.X, vs.X, vs.X + vs.Width - 1), Math.Clamp(p.Y, vs.Y, vs.Y + vs.Height - 1));
        Cursor = p;
        HoverWindow = Windows.FirstOrDefault(w => RectUtil.Contains(w.Bounds, p));
        HoverMonitor = MonitorAt(p);

        if (IsSelecting)
        {
            if (Mode == CaptureMode.Freeform)
            {
                if (Lasso.Count == 0 || (Lasso[^1] - p).Length >= 2) Lasso.Add(p);
                _moved = true;
            }
            else
            {
                if (_spaceHeld)
                {
                    _dragStart += p - _spaceAnchor;
                    _spaceAnchor = p;
                }
                _dragCurrent = p;
                if (!_moved && (Math.Abs(p.X - _dragStart.X) > 3 || Math.Abs(p.Y - _dragStart.Y) > 3)) _moved = true;
            }
        }
        Invalidate();
    }

    public void BeginSelection(Point p)
    {
        if (_done) return; // capture animation is playing
        UpdateCursor(p);
        p = Cursor!.Value;
        if (Mode == CaptureMode.Region)
        {
            IsSelecting = true;
            _moved = false;
            _dragStart = _dragCurrent = p;
            _selectionWindow = HoverWindow;
        }
        else if (Mode == CaptureMode.Freeform)
        {
            IsSelecting = true;
            _moved = false;
            Lasso.Clear();
            Lasso.Add(p);
        }
        Invalidate();
    }

    public void EndSelection(Point p)
    {
        if (_done) return; // capture animation is playing
        UpdateCursor(p);
        switch (Mode)
        {
            case CaptureMode.Region:
                if (!IsSelecting) return;
                bool moved = _moved;
                IsSelecting = false;
                if (moved)
                {
                    var r = SelectionRect;
                    if (r.Width >= 2 && r.Height >= 2) Finish(r, _selectionWindow?.ProcessName ?? _foregroundApp);
                }
                else if (HoverWindow != null)
                {
                    // A plain click in region mode grabs the window under the cursor.
                    Finish(HoverWindow.Bounds, HoverWindow.ProcessName);
                }
                Invalidate();
                break;
            case CaptureMode.Window:
                if (HoverWindow != null) Finish(HoverWindow.Bounds, HoverWindow.ProcessName);
                break;
            case CaptureMode.Screen:
                if (HoverMonitor != null) Finish(HoverMonitor.Bounds, null);
                break;
            case CaptureMode.Freeform:
                if (!IsSelecting) return;
                IsSelecting = false;
                if (Lasso.Count >= 3) FinishLasso();
                else { Lasso.Clear(); Invalidate(); }
                break;
        }
    }

    public void RightClick()
    {
        if (_done) return; // capture animation is playing
        if (IsSelecting)
        {
            IsSelecting = false;
            Lasso.Clear();
            Invalidate();
        }
        else
        {
            Cancel();
        }
    }

    public void HandleKeyDown(KeyEventArgs e)
    {
        if (_done) return; // capture animation is playing
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.Escape:
                RightClick();
                break;
            case Key.D1 or Key.NumPad1 or Key.R: SetMode(CaptureMode.Region); break;
            case Key.D2 or Key.NumPad2 or Key.W: SetMode(CaptureMode.Window); break;
            case Key.D3 or Key.NumPad3 or Key.S: SetMode(CaptureMode.Screen); break;
            case Key.D4 or Key.NumPad4 or Key.L: SetMode(CaptureMode.Freeform); break;
            case Key.F:
            case Key.Enter:
                if (HasSelection && Mode == CaptureMode.Region) EndSelection(Cursor!.Value);
                else if (HoverMonitor != null) Finish(HoverMonitor.Bounds, null);
                break;
            case Key.Space:
                if (IsSelecting && Mode == CaptureMode.Region && !_spaceHeld && Cursor is Point c)
                {
                    _spaceHeld = true;
                    _spaceAnchor = c;
                }
                break;
            case Key.Left: Nudge(shift ? -10 : -1, 0); break;
            case Key.Right: Nudge(shift ? 10 : 1, 0); break;
            case Key.Up: Nudge(0, shift ? -10 : -1); break;
            case Key.Down: Nudge(0, shift ? 10 : 1); break;
            default:
                return;
        }
        e.Handled = true;
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceHeld = false;
    }

    /// <summary>Moves the real cursor by whole pixels for precise selections.</summary>
    private static void Nudge(int dx, int dy)
    {
        if (NativeMethods.GetCursorPos(out var pt)) NativeMethods.SetCursorPos(pt.X + dx, pt.Y + dy);
    }

    /// <summary>Cancels when the user switches away to another app (e.g. Alt+Tab).</summary>
    public void CheckFocus()
    {
        if (!_done && !_overlays.Any(o => o.IsActive)) Cancel();
    }

    private void Finish(Int32Rect rect, string? app)
    {
        if (_done) return;
        _done = true;
        rect = RectUtil.Intersect(rect, Snapshot.Bounds);
        var image = Snapshot.Crop(rect);
        Complete(rect, new CaptureResult(image, app));
    }

    /// <summary>Plays the shutter animation on every overlay, then hands the result back.</summary>
    private async void Complete(Int32Rect rect, CaptureResult result)
    {
        FinalRect = rect;
        IsSelecting = false;
        Invalidate();

        // The capture flies into the corner only when the quick preview will be there to catch it.
        bool fly = Settings.ShowQuickPreview && !Settings.OpenEditorAfterCapture;
        var center = new Point(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);
        var flyOn = Cursor is Point c && MonitorAt(c) is { } m && RectUtil.Intersect(rect, m.Bounds).Width > 0 ? m : MonitorAt(center);
        try
        {
            await Task.WhenAll(_overlays.Select(o => o.PlayCaptureAsync(rect, result.Image, fly && o.Monitor == flyOn)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture animation");
        }
        // Hold the landed image on screen until the preview has faded in on top of it, so there's no gap.
        var caught = fly ? Views.QuickPreviewWindow.ExpectArrival() : null;
        _tcs.TrySetResult(result);
        if (caught != null)
        {
            await Task.WhenAny(caught, Task.Delay(700));
            Views.QuickPreviewWindow.CancelArrival();
        }
        CloseOverlays();
    }

    private void FinishLasso()
    {
        if (_done) return;
        _done = true;
        var rect = RectUtil.Intersect(RectUtil.FromPoints(
            new Point(Lasso.Min(p => p.X), Lasso.Min(p => p.Y)),
            new Point(Lasso.Max(p => p.X) + 1, Lasso.Max(p => p.Y) + 1)), Snapshot.Bounds);
        var crop = Snapshot.Crop(rect);

        var offset = new Vector(rect.X, rect.Y);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Lasso[0] - offset, true, true);
            ctx.PolyLineTo(Lasso.Skip(1).Select(p => p - offset).ToList(), true, true);
        }
        geometry.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(geometry);
            dc.DrawImage(crop, new Rect(0, 0, rect.Width, rect.Height));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(rect.Width, rect.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        Complete(rect, new CaptureResult(rtb, _foregroundApp));
    }

    public void Cancel()
    {
        if (_done) return;
        _done = true;
        CloseOverlays();
        _tcs.TrySetResult(null);
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }

    private void Invalidate()
    {
        foreach (var overlay in _overlays) overlay.Refresh();
    }
}
