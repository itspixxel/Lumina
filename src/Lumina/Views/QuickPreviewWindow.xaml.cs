using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumina.Capture;
using Lumina.Native;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>The floating thumbnail that appears in the corner after each capture.</summary>
public partial class QuickPreviewWindow : Window
{
    private static QuickPreviewWindow? _current;

    private readonly BitmapSource _image;
    private readonly string? _appName;
    private readonly DispatcherTimer _timer;
    private string? _path;
    private Task? _saveTask;
    private Point _downPoint;
    private bool _mouseDown, _dragged, _closing;

    private QuickPreviewWindow(BitmapSource image, string? path, Task? saveTask, string? appName)
    {
        InitializeComponent();
        _image = image;
        _path = path;
        _saveTask = saveTask;
        _appName = appName;
        Thumb.Source = image;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(App.Settings.PreviewSeconds, 2, 60)) };
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };
        Loaded += OnLoaded;
        UpdateLabels();
    }

    public static void ShowCapture(BitmapSource image, string? path, Task? saveTask, string? appName)
    {
        CloseCurrent();
        _current = new QuickPreviewWindow(image, path, saveTask, appName);
        _current.Show();
    }

    public static void CloseCurrent()
    {
        var current = _current;
        _current = null;
        current?.Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOnCursorMonitor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnCursorMonitor);

        var arrival = _arrival;
        _arrival = null;
        if (arrival != null)
        {
            // The capture has just flown into this exact spot; fade the card in around it without moving.
            Slide.Y = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            fade.Completed += (_, _) => Dispatcher.BeginInvoke(() => arrival.TrySetResult(), DispatcherPriority.Background);
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }
        _timer.Start();
    }

    private static TaskCompletionSource? _arrival;

    /// <summary>Marks the next preview as catching a flying capture; completes once it fully covers it.</summary>
    public static Task ExpectArrival()
    {
        _arrival = new TaskCompletionSource();
        return _arrival.Task;
    }

    public static void CancelArrival()
    {
        _arrival?.TrySetResult();
        _arrival = null;
    }

    /// <summary>Bottom-right of the work area on the monitor the user just captured on (in physical pixels).</summary>
    private void PlaceOnCursorMonitor()
    {
        var monitors = MonitorInfo.GetAll();
        var cursor = NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : new Point();
        var monitor = monitors.FirstOrDefault(m => RectUtil.Contains(m.Bounds, cursor))
                      ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor == null) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(hwnd, out var r);
        var area = monitor.WorkArea;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            area.X + area.Width - (r.Right - r.Left), area.Y + area.Height - (r.Bottom - r.Top), 0, 0,
            SwpNoSize | NativeMethods.SWP_NOACTIVATE);
    }

    private const uint SwpNoSize = 0x0001;

    private void UpdateLabels()
    {
        TitleText.Text = _path != null ? "Saved" : "Captured";
        string size = $"{_image.PixelWidth} x {_image.PixelHeight}";
        InfoText.Text = App.Settings.CopyToClipboard ? $"{size} - copied" : size;
        SaveButton.ToolTip = _path != null ? "Show in folder" : "Save";
        SaveIcon.Data = (Geometry)FindResource(_path != null ? "IconFolder" : "IconSave");
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_current == this) _current = null;
        base.OnClosed(e);
    }

    private async Task<string?> EnsureSavedAsync()
    {
        if (_path == null)
        {
            _path = OutputService.ReservePath(_appName);
            LibraryIndex.SetApp(_path, _appName);
            _saveTask = OutputService.SaveAsync(_image, _path);
            UpdateLabels();
        }
        try
        {
            if (_saveTask != null) await _saveTask;
            App.Instance.Controller.NotifySaved();
            return _path;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving from preview");
            LuminaDialog.Alert(null, "Couldn't save the capture", ex.Message);
            return null;
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        await ClipboardService.CopyImageAsync(_image);
        InfoText.Text = $"{_image.PixelWidth} x {_image.PixelHeight} - copied";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        bool alreadySaved = _path != null;
        var path = await EnsureSavedAsync();
        if (path != null && alreadySaved) OutputService.ShowInFolder(path);
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => OpenEditor();

    private void OpenEditor()
    {
        EditorWindow.Open(_image, _path, _saveTask);
        Close();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null)
        {
            bool permanent = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (permanent && !LuminaDialog.ConfirmPermanentDelete(null, Path.GetFileName(_path))) return;
            try { if (_saveTask != null) await _saveTask; } catch { /* nothing to delete */ }
            if (permanent) OutputService.DeletePermanently(_path);
            else OutputService.Recycle(_path);
            App.Instance.Controller.NotifySaved();
        }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Thumb_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = true;
        _dragged = false;
        _downPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private async void Thumb_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown || _dragged || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _downPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragged = true;
        _timer.Stop();
        // Drag-out needs a real file; save to the temp folder if the user turned off auto-save.
        string? path = _path;
        if (path == null)
        {
            path = Path.Combine(AppPaths.TempDir, $"Lumina_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await OutputService.SaveAsync(_image, path);
        }
        else if (_saveTask != null)
        {
            await _saveTask;
        }
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop(Thumb, data, DragDropEffects.Copy);
        _mouseDown = false;
    }

    private void Thumb_MouseUp(object sender, MouseButtonEventArgs e)
    {
        bool click = _mouseDown && !_dragged;
        _mouseDown = false;
        if (click) OpenEditor();
    }
}
