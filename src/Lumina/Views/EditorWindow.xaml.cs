using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Editor;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

public partial class EditorWindow : ChromeWindow
{
    private readonly Dictionary<EditorTool, RadioButton> _toolButtons;
    private string? _path;
    private Task? _pendingSave;
    // The file at _path holds what was captured or last saved (false while an auto-save is still being written).
    private bool _onDisk;
    private bool _ready, _syncing;

    // Zoom eases toward a target, keeping the point under the cursor (or the view centre) still.
    private bool _fitMode = true, _fitFill;
    private double _zoom = 1, _zoomTarget = 1;
    private Point _anchorLocal, _anchorView;
    private bool _hasAnchor, _zoomTicking;
    private TimeSpan _zoomLast;

    private EditorWindow(EditorDocument doc, string? path, Task? pendingSave, Task<bool>? copied)
    {
        InitializeComponent();
        _path = path;
        _pendingSave = pendingSave;
        _onDisk = path != null && pendingSave == null;
        if (path != null && pendingSave != null) _ = TrackPendingSaveAsync(pendingSave);

        _toolButtons = new()
        {
            [EditorTool.Select] = ToolSelect, [EditorTool.Arrow] = ToolArrow, [EditorTool.Line] = ToolLine,
            [EditorTool.Rectangle] = ToolRectangle, [EditorTool.Ellipse] = ToolEllipse, [EditorTool.Pen] = ToolPen,
            [EditorTool.Highlighter] = ToolHighlighter, [EditorTool.Text] = ToolText, [EditorTool.Step] = ToolStep,
            [EditorTool.Pixelate] = ToolPixelate, [EditorTool.Spotlight] = ToolSpotlight, [EditorTool.Crop] = ToolCrop,
        };

        Canvas.Load(doc);
        Canvas.Changed += () =>
        {
            UpdateStatus();
            if (_fitMode) Dispatcher.BeginInvoke(() => Fit());
        };
        Canvas.HistoryChanged += () =>
        {
            UpdateHistoryButtons();
            UpdateTitle();
        };
        Canvas.SelectionChanged += UpdateOptionsBar;
        Canvas.ColorPicked += OnColorPicked;
        Canvas.PixelateStrength = StrengthSlider.Value;
        Canvas.SpotlightDim = DimSlider.Value;
        Picker.ColorChanged += OnPickerColor;
        Picker.EyedropperRequested += () =>
        {
            PickerPopup.IsOpen = false;
            Canvas.PickingColor = true;
            Toast.Show("Click the image to pick a color · Esc to cancel", seconds: 8);
        };
        PickerPopup.Opened += (_, _) => Fx.PlayEntrance(Picker, 0, offsetY: 8, fromScale: 0.96);
        _ready = true;
        ToolPen.IsChecked = true;
        UpdateSize(SizeSlider.Value);

        Loaded += async (_, _) =>
        {
            Fit(animate: false);
            // Auto-copy happened as the capture came in; say so here, where the user is looking.
            if (copied != null) Toast.Show(await copied ? "Copied to clipboard" : "Couldn't copy, the clipboard is busy");
        };
        UpdateTitle();
        UpdateStatus();
        UpdateHistoryButtons();
    }

    /// <param name="copied">The auto-copy of this capture, if one was made, so the editor can report it.</param>
    public static void Open(BitmapSource image, string? path, Task? pendingSave = null, Task<bool>? copied = null) =>
        new EditorWindow(new EditorDocument(image), path, pendingSave, copied).Show();

    /// <summary>The file this editor saves to, if it has one yet.</summary>
    public string? FilePath => _path;

    private async Task TrackPendingSaveAsync(Task save)
    {
        try
        {
            await save;
            if (_pendingSave == save) _onDisk = true;
        }
        catch
        {
            // Logged by whoever started it; Save writes it again.
        }
        UpdateSaveUi();
    }

    public static void OpenFile(string path)
    {
        try
        {
            var doc = ProjectStore.TryLoad(path) ?? new EditorDocument(OutputService.LoadImage(path));
            new EditorWindow(doc, path, null, null).Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening image");
            LuminaDialog.Alert(null, $"Couldn't open {Path.GetFileName(path)}", ex.Message);
        }
    }

    // ---------- Toolbar ----------

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        Canvas.Tool = Enum.Parse<EditorTool>(tag);
        if (_fitMode) Dispatcher.BeginInvoke(() => Fit());
        UpdateOptionsBar();
    }

    private void SetTool(EditorTool tool) => _toolButtons[tool].IsChecked = true;

    // ---------- Color ----------

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Background: SolidColorBrush brush }) return;
        Canvas.CurrentColor = brush.Color;
        Canvas.ApplyStyleToSelection(brush.Color, null);
    }

    private void CustomSwatch_Click(object sender, RoutedEventArgs e)
    {
        Picker.SetColor(Canvas.CurrentColor);
        PickerPopup.IsOpen = true;
    }

    private void OnPickerColor(Color color, bool final) => UseCustomColor(color);

    private void OnColorPicked(Color color)
    {
        ColorPicker.Remember(color);
        Picker.SetColor(color);
        UseCustomColor(color);
        Flash($"Picked {ColorPicker.Hex(color)}");
    }

    private void UseCustomColor(Color color)
    {
        CustomSwatch.Background = new SolidColorBrush(color);
        CustomSwatch.ToolTip = $"Custom color {ColorPicker.Hex(color)} · click to change";
        if (CustomSwatch.IsChecked != true) CustomSwatch.IsChecked = true; // Color_Checked applies it
        else
        {
            Canvas.CurrentColor = color;
            Canvas.ApplyStyleToSelection(color, null);
        }
    }

    // ---------- Size ----------

    private void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateSize(e.NewValue);
        Canvas.CurrentThickness = e.NewValue;
        Canvas.ApplyStyleToSelection(null, e.NewValue);
    }

    private void UpdateSize(double value)
    {
        SizePreview.Width = SizePreview.Height = 3 + value * 1.25;
        SizeText.Text = value.ToString(value < 9.95 ? "0.#" : "0", System.Globalization.CultureInfo.CurrentCulture);
    }

    // ---------- Tool options ----------

    private void UpdateOptionsBar()
    {
        var selected = Canvas.Selected;
        FrameworkElement? panel = Canvas.Tool switch
        {
            EditorTool.Pixelate => PixelateOptions,
            EditorTool.Spotlight => SpotlightOptions,
            EditorTool.Crop => CropOptions,
            EditorTool.Select when selected is PixelateAnnotation => PixelateOptions,
            EditorTool.Select when selected is SpotlightAnnotation => SpotlightOptions,
            _ => null,
        };

        _syncing = true;
        if (panel == PixelateOptions)
        {
            var style = (selected as PixelateAnnotation)?.Style ?? Canvas.PixelateStyle;
            (style switch { RedactStyle.Blur => RedactBlur, RedactStyle.Solid => RedactSolid, _ => RedactMosaic }).IsChecked = true;
            if (selected is PixelateAnnotation { Strength: > 0 } p) StrengthSlider.Value = p.Strength;
            UpdateRedactUi(style);
        }
        else if (panel == SpotlightOptions)
        {
            bool ellipse = (selected as SpotlightAnnotation)?.Ellipse ?? Canvas.SpotlightEllipse;
            (ellipse ? SpotEllipse : SpotRect).IsChecked = true;
            if (selected is SpotlightAnnotation spot)
            {
                DimSlider.Value = spot.Dim;
                SpotBlur.IsChecked = spot.BlurOutside;
            }
        }
        _syncing = false;

        foreach (var p in new FrameworkElement[] { PixelateOptions, SpotlightOptions, CropOptions })
        {
            bool show = p == panel;
            bool wasHidden = p.Visibility != Visibility.Visible;
            p.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && wasHidden && OptionsBar.Visibility == Visibility.Visible) Fx.PlayEntrance(p, 0, offsetY: -4);
        }
        Fx.Reveal(OptionsBar, panel != null, offsetY: -8);
    }

    private void UpdateRedactUi(RedactStyle style)
    {
        StrengthGroup.Visibility = style == RedactStyle.Solid ? Visibility.Collapsed : Visibility.Visible;
        StrengthLabel.Text = style == RedactStyle.Blur ? "Blur amount" : "Block size";
    }

    private void Redact_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        var style = Enum.Parse<RedactStyle>(tag);
        UpdateRedactUi(style);
        if (_syncing) return;
        Canvas.PixelateStyle = style;
        Canvas.EditSelected<PixelateAnnotation>("redact-style", a => a.Style = style);
    }

    private void Strength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        Canvas.PixelateStrength = e.NewValue;
        Canvas.EditSelected<PixelateAnnotation>("redact-strength", a => a.Strength = e.NewValue);
    }

    private void SpotShape_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        bool ellipse = SpotEllipse.IsChecked == true;
        Canvas.SpotlightEllipse = ellipse;
        Canvas.EditSelected<SpotlightAnnotation>("spot-shape", a => a.Ellipse = ellipse);
    }

    private void Dim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        Canvas.SpotlightDim = e.NewValue;
        Canvas.EditSpotlights("spot-dim", a => a.Dim = e.NewValue);
    }

    private void SpotBlur_Click(object sender, RoutedEventArgs e)
    {
        bool on = SpotBlur.IsChecked == true;
        Canvas.SpotlightBlur = on;
        Canvas.EditSpotlights("spot-blur", a => a.BlurOutside = on);
    }

    private void Aspect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        double? aspect = tag switch
        {
            "Free" => null,
            "Original" => Canvas.Doc.PixelWidth / (double)Canvas.Doc.PixelHeight,
            _ => double.Parse(tag.Split(':')[0]) / double.Parse(tag.Split(':')[1]),
        };
        Canvas.ApplyCropAspect(aspect);
    }

    private void CropReset_Click(object sender, RoutedEventArgs e) => Canvas.ResetCrop();
    private void CropDone_Click(object sender, RoutedEventArgs e) => SetTool(EditorTool.Select);

    private void Undo_Click(object sender, RoutedEventArgs e) => Canvas.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Canvas.Redo();

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = Canvas.CanUndo;
        RedoButton.IsEnabled = Canvas.CanRedo;
    }

    private void UpdateTitle()
    {
        Title = (_path != null ? Path.GetFileName(_path) : "Untitled capture") + (Canvas.IsDirty ? " •" : "") + " — Lumina";
        UpdateSaveUi();
    }

    private void UpdateSaveUi()
    {
        bool saved = _onDisk && !Canvas.IsDirty;
        SaveButton.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        SavedLabel.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        SavedLabel.ToolTip = _path;
    }

    /// <summary>Lifts the color bar above the corner pills when the window is too narrow for all three in one row.</summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double side = Math.Max(SizePill.ActualWidth, ZoomPill.ActualWidth) + 18 + 12;
        bool fits = e.NewSize.Width >= ColorBar.ActualWidth + 2 * side;
        ColorBar.Margin = new Thickness(0, 0, 0, fits ? 18 : 64);
        Toast.Margin = new Thickness(24, 0, 24, fits ? 72 : 118);
    }

    private void UpdateStatus()
    {
        var crop = Canvas.Doc.EffectiveCrop;
        StatusText.Text = $"{crop.Width} × {crop.Height} px";
        UpdateTitle();
    }

    // ---------- Output ----------

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    private async Task CopyAsync()
    {
        Canvas.CommitText();
        bool ok = await ClipboardService.CopyImageAsync(Canvas.Doc.RenderFinal());
        Flash(ok ? "Copied to clipboard" : "Clipboard is busy, try again");
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        Canvas.CommitText();
        try
        {
            if (_pendingSave != null)
            {
                try { await _pendingSave; } catch { /* we overwrite it below anyway */ }
                _pendingSave = null;
            }
            _path ??= OutputService.ReservePath(null);
            var image = Canvas.Doc.RenderFinal();
            await OutputService.SaveAsync(image, _path);
            ProjectStore.Save(_path, Canvas.Doc);
            _onDisk = true;
            Canvas.MarkSaved();
            UpdateTitle();
            Flash($"Saved {Path.GetFileName(_path)}");
            App.Instance.Controller.NotifySaved();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving from editor");
            LuminaDialog.Alert(this, "Couldn't save", ex.Message);
            return false;
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task SaveAsAsync()
    {
        Canvas.CommitText();
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png|JPEG image|*.jpg",
            FileName = _path != null ? Path.GetFileNameWithoutExtension(_path) + "_edited" : "Lumina capture",
            InitialDirectory = _path != null ? Path.GetDirectoryName(_path) : App.Settings.OutputDirectory,
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await OutputService.SaveAsync(Canvas.Doc.RenderFinal(), dialog.FileName);
            _path = dialog.FileName;
            _pendingSave = null;
            _onDisk = true;
            ProjectStore.Save(_path, Canvas.Doc);
            Canvas.MarkSaved();
            UpdateTitle();
            Flash($"Saved {Path.GetFileName(_path)}");
            App.Instance.Controller.NotifySaved();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save as");
            LuminaDialog.Alert(this, "Couldn't save", ex.Message);
        }
    }

    private void Flash(string message) => Toast.Show(message);

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Canvas.CommitText();
        if (!Canvas.IsDirty)
        {
            base.OnClosing(e);
            return;
        }

        string name = _path != null ? $"“{Path.GetFileName(_path)}”" : "This capture";
        var answer = LuminaDialog.Show(this, "Save your changes?", $"{name} has markup that hasn't been saved yet.",
            DialogKind.Question, (Geometry)FindResource("IconSave"),
            new DialogButton("Don't save", "discard", Danger: true),
            new DialogButton("Cancel", "cancel", Cancel: true),
            new DialogButton("Save", "save", Primary: true));
        if (answer == "cancel")
        {
            e.Cancel = true;
        }
        else if (answer == "save")
        {
            e.Cancel = true;
            if (await SaveAsync())
            {
                Close();
            }
        }
        base.OnClosing(e);
    }

    // ---------- Zoom ----------

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <param name="anchor">Point in the viewport to keep still while zooming; the view centre by default.</param>
    private void SetZoom(double zoom, bool fit = false, bool animate = true, Point? anchor = null)
    {
        _fitMode = fit;
        _zoomTarget = Math.Clamp(zoom, 0.05, 16);
        _anchorView = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        _hasAnchor = IsLoaded && Canvas.IsVisible;
        if (_hasAnchor) _anchorLocal = Scroller.TranslatePoint(_anchorView, Canvas);
        UpdateZoomButtons();

        if (!animate || !IsLoaded)
        {
            StopZoomTicking();
            ApplyZoom(_zoomTarget);
            return;
        }
        if (_zoomTicking) return;
        _zoomTicking = true;
        _zoomLast = TimeSpan.Zero;
        CompositionTarget.Rendering += ZoomTick;
    }

    private void ZoomTick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _zoomLast == TimeSpan.Zero ? 1 / 60.0 : (now - _zoomLast).TotalSeconds;
        if (dt <= 0) return;
        _zoomLast = now;
        // Ease in log space so zooming in and out feel equally quick.
        double k = 1 - Math.Exp(-dt * 16);
        double next = Math.Exp(Math.Log(_zoom) + (Math.Log(_zoomTarget) - Math.Log(_zoom)) * k);
        if (Math.Abs(Math.Log(next / _zoomTarget)) < 0.002)
        {
            next = _zoomTarget;
            StopZoomTicking();
        }
        ApplyZoom(next);
    }

    private void StopZoomTicking()
    {
        if (!_zoomTicking) return;
        _zoomTicking = false;
        CompositionTarget.Rendering -= ZoomTick;
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = zoom;
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = zoom;
        Canvas.Zoom = zoom * DpiScale;
        ZoomText.Text = $"{Math.Round(zoom * DpiScale * 100)}%";
        if (!_hasAnchor) return;
        Scroller.UpdateLayout();
        var now = Canvas.TranslatePoint(_anchorLocal, Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + now.X - _anchorView.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + now.Y - _anchorView.Y);
    }

    private void UpdateZoomButtons()
    {
        FitButton.IsChecked = _fitMode;
        ActualButton.IsChecked = !_fitMode && Math.Abs(_zoomTarget * DpiScale - 1) < 0.001;
    }

    /// <param name="fill">Fill the window even past 100%; automatic fits never enlarge small captures.</param>
    private void Fit(bool? fill = null, bool animate = true)
    {
        if (fill is bool f) _fitFill = f;
        var size = Canvas.DisplaySize;
        double availableW = Scroller.ActualWidth - 144, availableH = Scroller.ActualHeight - 180;
        if (availableW <= 0 || availableH <= 0 || size.Width <= 0 || size.Height <= 0) return;
        double zoom = Math.Min(availableW / size.Width, availableH / size.Height);
        if (!_fitFill) zoom = Math.Min(1 / DpiScale, zoom); // one image pixel per screen pixel at most
        SetZoom(zoom, fit: true, animate: animate);
    }

    private void ZoomBy(double factor, Point? anchor = null) => SetZoom(_zoomTarget * factor, anchor: anchor);

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ZoomBy(Math.Pow(1.0015, e.Delta), e.GetPosition(Scroller));
        e.Handled = true;
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode) Fit(animate: false);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void Fit_Click(object sender, RoutedEventArgs e) => Fit(fill: true);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1 / DpiScale);

    // ---------- Keyboard ----------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // typing into a text annotation
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.Z: Canvas.Undo(); break;
                case Key.Y: Canvas.Redo(); break;
                case Key.C: e.Handled = true; await CopyAsync(); return;
                case Key.S: e.Handled = true; await SaveAsync(); return;
                case Key.D0 or Key.NumPad0: Fit(fill: true); break;
                case Key.D1 or Key.NumPad1: SetZoom(1 / DpiScale); break;
                case Key.OemPlus or Key.Add: ZoomBy(1.25); break;
                case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (key == Key.Z) { Canvas.Redo(); e.Handled = true; }
            else if (key == Key.S) { e.Handled = true; await SaveAsAsync(); }
            return;
        }

        if (mods != ModifierKeys.None) return;
        EditorTool? tool = key switch
        {
            Key.V => EditorTool.Select, Key.A => EditorTool.Arrow, Key.L => EditorTool.Line,
            Key.R => EditorTool.Rectangle, Key.E => EditorTool.Ellipse, Key.P => EditorTool.Pen,
            Key.H => EditorTool.Highlighter, Key.T => EditorTool.Text, Key.N => EditorTool.Step,
            Key.B => EditorTool.Pixelate, Key.S => EditorTool.Spotlight, Key.C => EditorTool.Crop,
            _ => null,
        };
        if (tool is EditorTool t)
        {
            SetTool(t);
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.OemOpenBrackets:
                SizeSlider.Value -= 1;
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                SizeSlider.Value += 1;
                e.Handled = true;
                break;
            case Key.Escape when Canvas.PickingColor:
                Canvas.PickingColor = false;
                Toast.Hide();
                e.Handled = true;
                break;
            case Key.Delete or Key.Back:
                Canvas.DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                if (Canvas.Tool == EditorTool.Crop) SetTool(EditorTool.Select);
                else Canvas.ClearSelection();
                e.Handled = true;
                break;
            case Key.Enter when Canvas.Tool == EditorTool.Crop:
                SetTool(EditorTool.Select);
                e.Handled = true;
                break;
        }
    }
}
