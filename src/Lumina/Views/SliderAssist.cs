using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumina.Views;

/// <summary>
/// Lets a slider be grabbed anywhere on its track: the thumb jumps to the press and keeps following the
/// mouse until release (WPF's IsMoveToPointEnabled only jumps, it doesn't continue into a drag).
/// </summary>
public static class SliderAssist
{
    public static readonly DependencyProperty DragAnywhereProperty = DependencyProperty.RegisterAttached(
        "DragAnywhere", typeof(bool), typeof(SliderAssist), new PropertyMetadata(false, OnDragAnywhereChanged));

    public static bool GetDragAnywhere(DependencyObject o) => (bool)o.GetValue(DragAnywhereProperty);
    public static void SetDragAnywhere(DependencyObject o, bool v) => o.SetValue(DragAnywhereProperty, v);

    private static readonly DependencyProperty DraggingProperty = DependencyProperty.RegisterAttached(
        "Dragging", typeof(bool), typeof(SliderAssist), new PropertyMetadata(false));

    private static void OnDragAnywhereChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider || e.NewValue is not true) return;
        slider.PreviewMouseLeftButtonDown += OnDown;
        slider.PreviewMouseMove += OnMove;
        slider.PreviewMouseLeftButtonUp += OnUp;
        slider.LostMouseCapture += (_, _) => slider.SetValue(DraggingProperty, false);
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        // Presses on the thumb itself use its own drag (and press feedback).
        if (IsInsideThumb(e.OriginalSource as DependencyObject, slider)) return;
        if (!SetFromMouse(slider, e)) return;
        slider.SetValue(DraggingProperty, true);
        slider.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if ((bool)slider.GetValue(DraggingProperty) && e.LeftButton == MouseButtonState.Pressed) SetFromMouse(slider, e);
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!(bool)slider.GetValue(DraggingProperty)) return;
        slider.SetValue(DraggingProperty, false);
        slider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static bool SetFromMouse(Slider slider, MouseEventArgs e)
    {
        if (slider.Template?.FindName("PART_Track", slider) is not Track track || track.ActualWidth <= 0) return false;
        double value = track.ValueFromPoint(e.GetPosition(track));
        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
            value = slider.Minimum + System.Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        slider.Value = value; // coerced to the range by the slider
        return true;
    }

    private static bool IsInsideThumb(DependencyObject? source, Slider slider)
    {
        for (var d = source; d != null && d != slider; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Thumb) return true;
        return false;
    }
}
