using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumina.Editor;

public enum EditorTool { Select, Arrow, Line, Rectangle, Ellipse, Pen, Highlighter, Text, Step, Pixelate, Spotlight, Crop }

/// <summary>
/// Interactive surface for the editor. One unit = one image pixel; the window zooms it with a
/// LayoutTransform. When cropped, the view is offset so the crop's top-left is at (0,0), except while
/// the crop tool is active, when the whole image is shown so the crop can be adjusted.
/// </summary>
public sealed class EditorCanvas : Canvas
{
    private static readonly ControlTemplate BareTextBox = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
        "<Border BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
        "Background='{TemplateBinding Background}'><ScrollViewer x:Name='PART_ContentHost' Focusable='False' " +
        "HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' Padding='0' Margin='0'/></Border>" +
        "</ControlTemplate>");

    /// <summary>How far the pen moves toward the cursor per mouse event (1 = no stabilizing).</summary>
    private const double StrokeFollow = 0.55;

    private static readonly Brush CropDim = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly Stack<DocumentState> _undo = new();
    private readonly Stack<DocumentState> _redo = new();
    private EditorTool _tool = EditorTool.Pen;
    private double _zoom = 1;
    private Annotation? _active;
    private DocumentState? _pendingMoveUndo;
    private Point _start, _last, _smoothed;
    private bool _dragging, _moved;
    private Int32Rect? _cropPreview;
    private TextBox? _textBox;
    private TextAnnotation? _textOriginal;

    public EditorDocument Doc { get; private set; } = null!;
    public Annotation? Selected { get; private set; }
    public Color CurrentColor { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public double CurrentThickness { get; set; } = 4;

    // Defaults for new effect annotations (the options bar edits these, and the selection if it matches).
    public RedactStyle PixelateStyle { get; set; } = RedactStyle.Mosaic;
    public double PixelateStrength { get; set; }
    public bool SpotlightEllipse { get; set; }
    public double SpotlightDim { get; set; } = 0.6;
    public bool SpotlightBlur { get; set; }

    /// <summary>Width ÷ height the crop is held to, or null for freeform.</summary>
    public double? CropAspect { get; set; }

    /// <summary>While set, the next click samples a color from the image instead of drawing.</summary>
    public bool PickingColor
    {
        get => _picking;
        set
        {
            _picking = value;
            Cursor = value ? Cursors.Cross : ToolCursor();
        }
    }
    private bool _picking;
    public event Action<Color>? ColorPicked;
    public event Action? SelectionChanged;

    // Repeated edits from a slider/keyboard collapse into one undo step.
    private string? _coalesceKey;
    private DateTime _coalesceAt;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Undo depth of the last saved state; -1 once that state can no longer be reached.</summary>
    private int _savedDepth;
    public bool IsDirty => _undo.Count != _savedDepth;
    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        HistoryChanged?.Invoke();
    }

    /// <summary>Raised whenever the document content changes.</summary>
    public event Action? Changed;
    public event Action? HistoryChanged;

    public EditorCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = false;
        SnapsToDevicePixels = false;
        Cursor = Cursors.Cross;
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            RenderOptions.SetBitmapScalingMode(this, value >= 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            InvalidateVisual();
        }
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitText();
            bool layoutChanges = _tool == EditorTool.Crop || value == EditorTool.Crop;
            _tool = value;
            _cropPreview = null;
            Select(null);
            _picking = false;
            Cursor = ToolCursor();
            if (layoutChanges) InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private Cursor ToolCursor() => _tool switch
    {
        EditorTool.Select => Cursors.Arrow,
        EditorTool.Text => Cursors.IBeam,
        _ => Cursors.Cross,
    };

    public void Load(EditorDocument doc)
    {
        Doc = doc;
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        Selected = null;
        InvalidateMeasure();
        InvalidateVisual();
        HistoryChanged?.Invoke();
    }

    private bool ShowFullImage => Tool == EditorTool.Crop;
    private Vector Offset => ShowFullImage ? new Vector() : new Vector(Doc.EffectiveCrop.X, Doc.EffectiveCrop.Y);

    public Size DisplaySize => ShowFullImage
        ? new Size(Doc.PixelWidth, Doc.PixelHeight)
        : new Size(Doc.EffectiveCrop.Width, Doc.EffectiveCrop.Height);

    private Point ToImage(Point local) => local + Offset;
    private Point ToLocal(Point image) => image - Offset;

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        return Doc == null ? new Size() : DisplaySize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        base.ArrangeOverride(arrangeSize);
        return Doc == null ? arrangeSize : DisplaySize;
    }

    // ---------- History ----------

    private void PushUndo(DocumentState? state = null)
    {
        // Branching off below the saved point (undo, then a new edit) makes the saved state unreachable.
        if (_undo.Count < _savedDepth) _savedDepth = -1;
        _coalesceKey = null;
        _undo.Push(state ?? Doc.Snapshot());
        _redo.Clear();
        HistoryChanged?.Invoke();
    }

    private void DiscardLastUndo()
    {
        if (_undo.Count > 0) _undo.Pop();
        HistoryChanged?.Invoke();
    }

    public void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        _redo.Push(Doc.Snapshot());
        Doc.Restore(_undo.Pop());
        AfterHistoryJump();
    }

    public void Redo()
    {
        CommitText();
        if (_redo.Count == 0) return;
        _undo.Push(Doc.Snapshot());
        Doc.Restore(_redo.Pop());
        AfterHistoryJump();
    }

    private void AfterHistoryJump()
    {
        Selected = null;
        InvalidateMeasure();
        InvalidateVisual();
        HistoryChanged?.Invoke();
        Changed?.Invoke();
    }

    private void OnChanged()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    // ---------- Selection & styling ----------

    private void Select(Annotation? annotation)
    {
        bool changed = Selected != annotation;
        Selected = annotation;
        InvalidateVisual();
        if (changed) SelectionChanged?.Invoke();
    }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        PushUndo();
        Doc.Annotations.Remove(Selected);
        Selected = null;
        OnChanged();
    }

    public void ClearSelection() => Select(null);

    /// <summary>Applies the toolbar color/size to the selected annotation, if any.</summary>
    public void ApplyStyleToSelection(Color? color, double? thickness)
    {
        if (Selected == null) return;
        PushUndoCoalesced(thickness != null ? "size" : "color");
        if (color is Color c) Selected.Color = c;
        if (thickness is double t)
        {
            Selected.Thickness = t;
            if (Selected is TextAnnotation text) text.FontSize = TextAnnotation.FontSizeFor(t);
        }
        OnChanged();
    }

    /// <summary>Edits the selection if it's a <typeparamref name="T"/>; slider drags become a single undo step.</summary>
    public void EditSelected<T>(string key, Action<T> change) where T : Annotation
    {
        if (Selected is not T target) return;
        PushUndoCoalesced(key);
        change(target);
        OnChanged();
    }

    /// <summary>Spotlight darkness and blur are one look for the whole image, so they apply to every spotlight.</summary>
    public void EditSpotlights(string key, Action<SpotlightAnnotation> change)
    {
        var spots = Doc.Annotations.OfType<SpotlightAnnotation>().ToList();
        if (spots.Count == 0) return;
        PushUndoCoalesced(key);
        spots.ForEach(change);
        OnChanged();
    }

    private void PushUndoCoalesced(string key)
    {
        var now = DateTime.UtcNow;
        string fullKey = key + "#" + (Selected?.GetHashCode() ?? 0);
        bool same = _coalesceKey == fullKey && (now - _coalesceAt).TotalMilliseconds < 700;
        if (!same) PushUndo();
        _coalesceKey = fullKey;
        _coalesceAt = now;
    }

    private Annotation? HitTestAnnotations(Point p)
    {
        double tolerance = 6 / Math.Max(0.1, Zoom);
        for (int i = Doc.Annotations.Count - 1; i >= 0; i--)
            if (Doc.Annotations[i].HitTest(p, tolerance)) return Doc.Annotations[i];
        return null;
    }

    private int NextStepNumber() =>
        Doc.Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    // ---------- Mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_textBox != null && _textBox.IsAncestorOf((DependencyObject)e.OriginalSource)) return;
        CommitText();
        Focus();

        var p = ToImage(e.GetPosition(this));
        _start = _last = _smoothed = p;
        _moved = false;

        if (_picking)
        {
            PickingColor = false;
            if (SampleColor(p) is Color picked) ColorPicked?.Invoke(picked);
            e.Handled = true;
            return;
        }

        switch (Tool)
        {
            case EditorTool.Select:
                var hit = HitTestAnnotations(p);
                if (hit is TextAnnotation existingText && e.ClickCount == 2)
                {
                    BeginText(existingText.Position, existingText);
                    break;
                }
                Select(hit);
                if (hit != null)
                {
                    _pendingMoveUndo = Doc.Snapshot();
                    _dragging = true;
                    CaptureMouse();
                    Cursor = Cursors.SizeAll;
                }
                break;

            case EditorTool.Text:
                var underCursor = HitTestAnnotations(p) as TextAnnotation;
                BeginText(underCursor?.Position ?? p, underCursor);
                break;

            case EditorTool.Step:
                PushUndo();
                Doc.Annotations.Add(new StepAnnotation { X = p.X, Y = p.Y, Number = NextStepNumber(), Color = CurrentColor, Thickness = CurrentThickness });
                OnChanged();
                break;

            case EditorTool.Crop when e.ClickCount == 2:
                ResetCrop();
                break;

            case EditorTool.Crop:
                _dragging = true;
                _cropPreview = null;
                CaptureMouse();
                break;

            default:
                PushUndo();
                _active = CreateAnnotation(p);
                Doc.Annotations.Add(_active);
                _dragging = true;
                CaptureMouse();
                InvalidateVisual();
                break;
        }
        e.Handled = true;
    }

    private Annotation CreateAnnotation(Point p) => Tool switch
    {
        EditorTool.Arrow => Shape(ShapeKind.Arrow),
        EditorTool.Line => Shape(ShapeKind.Line),
        EditorTool.Rectangle => Shape(ShapeKind.Rectangle),
        EditorTool.Ellipse => Shape(ShapeKind.Ellipse),
        EditorTool.Pen => new StrokeAnnotation { Points = { p }, Color = CurrentColor, Thickness = CurrentThickness },
        EditorTool.Highlighter => new StrokeAnnotation { Points = { p }, Highlighter = true, Color = HighlighterColor(), Thickness = CurrentThickness },
        EditorTool.Pixelate => new PixelateAnnotation { X = p.X, Y = p.Y, Color = CurrentColor, Style = PixelateStyle, Strength = PixelateStrength },
        EditorTool.Spotlight => new SpotlightAnnotation
        {
            X = p.X, Y = p.Y, Color = CurrentColor, Ellipse = SpotlightEllipse, Dim = SpotlightDim, BlurOutside = SpotlightBlur,
        },
        _ => throw new InvalidOperationException($"No annotation for {Tool}"),
    };

    private ShapeAnnotation Shape(ShapeKind kind) => new()
    {
        Kind = kind, X1 = _start.X, Y1 = _start.Y, X2 = _start.X, Y2 = _start.Y,
        Color = CurrentColor, Thickness = CurrentThickness,
    };

    /// <summary>Highlighters look wrong in black/white, so those fall back to classic yellow.</summary>
    private Color HighlighterColor()
    {
        var c = CurrentColor;
        bool grey = Math.Abs(c.R - c.G) < 12 && Math.Abs(c.G - c.B) < 12;
        return grey ? Color.FromRgb(0xFF, 0xD6, 0x0A) : c;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToImage(e.GetPosition(this));

        if (!_dragging)
        {
            if (Tool == EditorTool.Select)
                Cursor = HitTestAnnotations(p) != null ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (Tool)
        {
            case EditorTool.Select when Selected != null:
                if (!_moved && _pendingMoveUndo != null) PushUndo(_pendingMoveUndo);
                _moved = true;
                Selected.Move(p - _last);
                _last = p;
                InvalidateVisual();
                break;

            case EditorTool.Crop:
                double? aspect = shift ? 1 : CropAspect;
                _cropPreview = aspect is double a ? AspectRect(_start, p, a) : ClampToImage(new Rect(_start, p));
                InvalidateVisual();
                break;

            default:
                UpdateActive(p, shift);
                InvalidateVisual();
                break;
        }
    }

    private void UpdateActive(Point p, bool shift)
    {
        switch (_active)
        {
            case ShapeAnnotation s:
                var end = !shift ? p : s.Kind is ShapeKind.Line or ShapeKind.Arrow ? SnapAngle(_start, p) : Square(_start, p);
                s.X2 = end.X;
                s.Y2 = end.Y;
                break;
            case StrokeAnnotation stroke when shift:
                stroke.Points = new List<Point> { _start, p };
                break;
            case StrokeAnnotation stroke:
                // A light "lazy brush": ease toward the cursor so hand jitter doesn't reach the line.
                _smoothed += (p - _smoothed) * StrokeFollow;
                if ((_smoothed - stroke.Points[^1]).Length >= 2 / Math.Max(0.25, Zoom)) stroke.Points.Add(_smoothed);
                break;
            case RegionAnnotation region:
                region.SetFromPoints(_start, shift ? Square(_start, p) : p);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        switch (Tool)
        {
            case EditorTool.Select:
                _pendingMoveUndo = null;
                if (_moved) OnChanged();
                Cursor = Cursors.Arrow;
                break;

            case EditorTool.Crop:
                if (_cropPreview is { Width: >= 4, Height: >= 4 } r)
                {
                    PushUndo();
                    Doc.Crop = r.X == 0 && r.Y == 0 && r.Width == Doc.PixelWidth && r.Height == Doc.PixelHeight ? null : r;
                    OnChanged();
                }
                _cropPreview = null;
                InvalidateVisual();
                break;

            default:
                // The stabilizer lags slightly behind; end the stroke where the cursor actually stopped.
                if (_active is StrokeAnnotation { Points.Count: > 1 } stroke && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    var end = ToImage(e.GetPosition(this));
                    if ((end - stroke.Points[^1]).Length >= 0.5) stroke.Points.Add(end);
                }
                if (_active != null && IsDegenerate(_active))
                {
                    Doc.Annotations.Remove(_active);
                    DiscardLastUndo();
                    InvalidateVisual();
                }
                else
                {
                    OnChanged();
                }
                _active = null;
                break;
        }
    }

    private static bool IsDegenerate(Annotation a) => a switch
    {
        ShapeAnnotation s => (s.End - s.Start).Length < 3,
        RegionAnnotation r => r.W < 3 || r.H < 3,
        _ => false,
    };

    /// <summary>The largest rectangle of the given aspect anchored at <paramref name="a"/>, toward <paramref name="b"/>, inside the image.</summary>
    private Int32Rect AspectRect(Point a, Point b, double aspect)
    {
        double sx = b.X >= a.X ? 1 : -1, sy = b.Y >= a.Y ? 1 : -1;
        double w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        // Room from the anchor to the image edge in the drag direction.
        double maxW = sx > 0 ? Doc.PixelWidth - a.X : a.X, maxH = sy > 0 ? Doc.PixelHeight - a.Y : a.Y;
        if (w / Math.Max(1e-6, h) > aspect) h = w / aspect; else w = h * aspect;
        if (w > maxW) { w = maxW; h = w / aspect; }
        if (h > maxH) { h = maxH; w = h * aspect; }
        return ClampToImage(new Rect(a, new Point(a.X + sx * w, a.Y + sy * h)));
    }

    /// <summary>Reshapes the current crop to an aspect ratio: the largest such area centred on it.</summary>
    public void ApplyCropAspect(double? aspect)
    {
        CropAspect = aspect;
        if (aspect is not double a) return;
        var current = Doc.EffectiveCrop;
        double cx = current.X + current.Width / 2.0, cy = current.Y + current.Height / 2.0;
        double w = current.Width, h = current.Height;
        if (w / h > a) w = h * a; else h = w / a;
        var r = ClampToImage(new Rect(cx - w / 2, cy - h / 2, w, h));
        if (r.Width < 4 || r.Height < 4 || r.Equals(current)) return;
        PushUndo();
        Doc.Crop = r.X == 0 && r.Y == 0 && r.Width == Doc.PixelWidth && r.Height == Doc.PixelHeight ? null : r;
        OnChanged();
    }

    private Color? SampleColor(Point p)
    {
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= Doc.PixelWidth || y >= Doc.PixelHeight) return null;
        // Sample what's on screen, markup included, not just the original.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-x, -y));
            Doc.DrawContent(dc);
        }
        var rtb = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[4];
        rtb.CopyPixels(px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    public void ResetCrop()
    {
        if (Doc.Crop == null) return;
        PushUndo();
        Doc.Crop = null;
        InvalidateMeasure();
        OnChanged();
    }

    private Int32Rect ClampToImage(Rect r)
    {
        int x1 = (int)Math.Max(0, Math.Round(r.Left)), y1 = (int)Math.Max(0, Math.Round(r.Top));
        int x2 = (int)Math.Min(Doc.PixelWidth, Math.Round(r.Right)), y2 = (int)Math.Min(Doc.PixelHeight, Math.Round(r.Bottom));
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    private static Point Square(Point a, Point b)
    {
        double size = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Point(a.X + Math.Sign(b.X - a.X) * size, a.Y + Math.Sign(b.Y - a.Y) * size);
    }

    private static Point SnapAngle(Point a, Point b)
    {
        var v = b - a;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return a + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    // ---------- Text ----------

    private void BeginText(Point imagePosition, TextAnnotation? existing)
    {
        CommitText();
        if (existing != null)
        {
            PushUndo();
            Doc.Annotations.Remove(existing);
            _textOriginal = existing;
        }

        var color = existing?.Color ?? CurrentColor;
        var brush = new SolidColorBrush(color);
        var box = new TextBox
        {
            Template = BareTextBox,
            Text = existing?.Text ?? "",
            FontFamily = TextAnnotation.Family,
            FontWeight = FontWeights.SemiBold,
            FontSize = existing?.FontSize ?? TextAnnotation.FontSizeFor(CurrentThickness),
            Foreground = brush,
            CaretBrush = brush,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush((Color)Application.Current.Resources["AccentColor"]),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = 24,
            AcceptsReturn = true,
            Tag = color,
        };
        // TextBoxView adds ~2px of internal padding; offset so committed text lands where it was typed.
        var local = ToLocal(imagePosition);
        SetLeft(box, local.X - 3);
        SetTop(box, local.Y - 1);

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
                e.Handled = true;
                CommitText();
                Focus();
            }
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(CommitText);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        };

        _textBox = box;
        Children.Add(box);
        InvalidateVisual();
    }

    public void CommitText()
    {
        if (_textBox == null) return;
        var box = _textBox;
        _textBox = null;
        Children.Remove(box);

        var position = ToImage(new Point(GetLeft(box) + 3, GetTop(box) + 1));
        if (!string.IsNullOrWhiteSpace(box.Text))
        {
            if (_textOriginal == null) PushUndo();
            Doc.Annotations.Add(new TextAnnotation
            {
                X = position.X,
                Y = position.Y,
                Text = box.Text.TrimEnd(),
                FontSize = box.FontSize,
                Color = (Color)box.Tag,
                Thickness = CurrentThickness,
            });
        }
        _textOriginal = null;
        OnChanged();
    }

    // ---------- Rendering ----------

    protected override void OnRender(DrawingContext dc)
    {
        if (Doc == null) return;
        var size = DisplaySize;
        var area = new Rect(size);
        dc.DrawRectangle(Brushes.Transparent, null, area);
        dc.PushClip(new RectangleGeometry(area));
        dc.PushTransform(new TranslateTransform(-Offset.X, -Offset.Y));

        Doc.DrawContent(dc);

        double px = 1 / Math.Max(0.05, Zoom);
        if (Selected != null)
        {
            var b = Selected.Bounds;
            b.Inflate(4 * px + Selected.Thickness / 2, 4 * px + Selected.Thickness / 2);
            var accent = new SolidColorBrush((Color)Application.Current.Resources["AccentColor"]);
            dc.DrawRectangle(null, new Pen(Brushes.White, 3 * px), b);
            dc.DrawRectangle(null, new Pen(accent, 1.5 * px) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }, b);
        }

        if (Tool == EditorTool.Crop) DrawCropOverlay(dc, px);

        dc.Pop();
        dc.Pop();
    }

    private void DrawCropOverlay(DrawingContext dc, double px)
    {
        var crop = _cropPreview ?? Doc.EffectiveCrop;
        var full = new Rect(0, 0, Doc.PixelWidth, Doc.PixelHeight);
        var rect = new Rect(crop.X, crop.Y, crop.Width, crop.Height);
        dc.DrawGeometry(CropDim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(rect)));

        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), px);
        for (int i = 1; i < 3; i++)
        {
            double x = rect.X + rect.Width * i / 3, y = rect.Y + rect.Height * i / 3;
            dc.DrawLine(thirds, new Point(x, rect.Top), new Point(x, rect.Bottom));
            dc.DrawLine(thirds, new Point(rect.Left, y), new Point(rect.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 2 * px), rect);

        var ft = new FormattedText($"{crop.Width} × {crop.Height}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            LabelFace, 12 * px, Brushes.White, 1.0);
        var labelRect = new Rect(rect.X + 6 * px, rect.Y + 6 * px, ft.Width + 12 * px, ft.Height + 6 * px);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), null, labelRect, 4 * px, 4 * px);
        dc.DrawText(ft, new Point(labelRect.X + 6 * px, labelRect.Y + 3 * px));
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
