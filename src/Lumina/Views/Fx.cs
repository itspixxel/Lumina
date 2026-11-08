using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumina.Views;

/// <summary>
/// Small motion toolkit used across the UI. Everything is driven by attached properties so templates
/// can animate state changes declaratively (e.g. bind <see cref="ShowProperty"/> to IsMouseOver
/// instead of snapping a brush in a trigger).
/// </summary>
public static class Fx
{
    public static readonly Duration Fast = TimeSpan.FromMilliseconds(130);
    public static readonly Duration Medium = TimeSpan.FromMilliseconds(220);
    public static readonly Duration Slow = TimeSpan.FromMilliseconds(380);

    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseOutStrong = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });

    // ---------- Show: animated opacity driven by a bool ----------

    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnShowChanged));
    public static bool GetShow(DependencyObject o) => (bool)o.GetValue(ShowProperty);
    public static void SetShow(DependencyObject o, bool v) => o.SetValue(ShowProperty, v);

    /// <summary>Opacity used while <see cref="ShowProperty"/> is true (default 1).</summary>
    public static readonly DependencyProperty ShowOpacityProperty = DependencyProperty.RegisterAttached(
        "ShowOpacity", typeof(double), typeof(Fx), new PropertyMetadata(1.0));
    public static double GetShowOpacity(DependencyObject o) => (double)o.GetValue(ShowOpacityProperty);
    public static void SetShowOpacity(DependencyObject o, double v) => o.SetValue(ShowOpacityProperty, v);

    /// <summary>When true, showing also springs the element up from a smaller scale.</summary>
    public static readonly DependencyProperty ShowPopProperty = DependencyProperty.RegisterAttached(
        "ShowPop", typeof(bool), typeof(Fx), new PropertyMetadata(false));
    public static bool GetShowPop(DependencyObject o) => (bool)o.GetValue(ShowPopProperty);
    public static void SetShowPop(DependencyObject o, bool v) => o.SetValue(ShowPopProperty, v);

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        bool show = (bool)e.NewValue;
        double target = show ? GetShowOpacity(d) : 0;
        bool live = el is FrameworkElement { IsLoaded: true };
        if (!live)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = target;
            return;
        }
        Animate(el, UIElement.OpacityProperty, target, show ? Fast : Medium, EaseOut);
        if (show && GetShowPop(d))
        {
            var (scale, _) = Parts(el);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.55, 1, Medium, Spring));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.55, 1, Medium, Spring));
        }
    }

    // ---------- SlideX: animated horizontal offset (switch knobs) ----------

    public static readonly DependencyProperty SlideXProperty = DependencyProperty.RegisterAttached(
        "SlideX", typeof(double), typeof(Fx), new PropertyMetadata(0.0, OnSlideXChanged));
    public static double GetSlideX(DependencyObject o) => (double)o.GetValue(SlideXProperty);
    public static void SetSlideX(DependencyObject o, double v) => o.SetValue(SlideXProperty, v);

    private static void OnSlideXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var (_, translate) = Parts(el);
        if (el is FrameworkElement { IsLoaded: true })
            translate.BeginAnimation(TranslateTransform.XProperty, Anim(null, (double)e.NewValue, Medium, Spring));
        else
            translate.X = (double)e.NewValue;
    }

    // ---------- PressScale: shrink while pressed, spring back on release ----------

    public static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached(
        "PressScale", typeof(double), typeof(Fx), new PropertyMetadata(1.0, OnPressScaleChanged));
    public static double GetPressScale(DependencyObject o) => (double)o.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject o, double v) => o.SetValue(PressScaleProperty, v);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewMouseLeftButtonDown -= OnPressDown;
        el.PreviewMouseLeftButtonUp -= OnPressUp;
        el.MouseLeave -= OnPressUp;
        el.LostMouseCapture -= OnPressUp;
        if ((double)e.NewValue == 1.0) return;
        el.PreviewMouseLeftButtonDown += OnPressDown;
        el.PreviewMouseLeftButtonUp += OnPressUp;
        el.MouseLeave += OnPressUp;
        el.LostMouseCapture += OnPressUp;
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement el || !el.IsEnabled) return;
        var (scale, _) = Parts(el);
        double s = GetPressScale(el);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, s, Fast, EaseOut));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, s, Fast, EaseOut));
    }

    private static void OnPressUp(object sender, EventArgs e)
    {
        if (sender is not UIElement el || !Transforms.TryGetValue(el, out var parts)) return;
        if (parts.Scale.ScaleX == 1 && parts.Scale.HasAnimatedProperties == false) return;
        parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, 1, Slow, Spring));
        parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, 1, Slow, Spring));
    }

    // ---------- HoverLift: rise slightly while hovered ----------

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(double), typeof(Fx), new PropertyMetadata(0.0, OnHoverLiftChanged));
    public static double GetHoverLift(DependencyObject o) => (double)o.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject o, double v) => o.SetValue(HoverLiftProperty, v);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.MouseEnter -= OnLiftEnter;
        el.MouseLeave -= OnLiftLeave;
        if ((double)e.NewValue == 0) return;
        el.MouseEnter += OnLiftEnter;
        el.MouseLeave += OnLiftLeave;
    }

    private static void OnLiftEnter(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement el || !el.IsEnabled) return;
        Parts(el).Translate.BeginAnimation(TranslateTransform.YProperty, Anim(null, -GetHoverLift(el), Medium, EaseOut));
    }

    private static void OnLiftLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement el)
            Parts(el).Translate.BeginAnimation(TranslateTransform.YProperty, Anim(null, 0, Medium, EaseOut));
    }

    // ---------- Entrance: fade + rise in when loaded ----------

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnEntranceChanged));
    public static bool GetEntrance(DependencyObject o) => (bool)o.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject o, bool v) => o.SetValue(EntranceProperty, v);

    public static readonly DependencyProperty EntranceDelayProperty = DependencyProperty.RegisterAttached(
        "EntranceDelay", typeof(int), typeof(Fx), new PropertyMetadata(0));
    public static int GetEntranceDelay(DependencyObject o) => (int)o.GetValue(EntranceDelayProperty);
    public static void SetEntranceDelay(DependencyObject o, int v) => o.SetValue(EntranceDelayProperty, v);

    /// <summary>Starting vertical offset in DIPs; negative values drop in from above.</summary>
    public static readonly DependencyProperty EntranceOffsetProperty = DependencyProperty.RegisterAttached(
        "EntranceOffset", typeof(double), typeof(Fx), new PropertyMetadata(14.0));
    public static double GetEntranceOffset(DependencyObject o) => (double)o.GetValue(EntranceOffsetProperty);
    public static void SetEntranceOffset(DependencyObject o, double v) => o.SetValue(EntranceOffsetProperty, v);

    /// <summary>Starting scale (1 = no zoom).</summary>
    public static readonly DependencyProperty EntranceScaleProperty = DependencyProperty.RegisterAttached(
        "EntranceScale", typeof(double), typeof(Fx), new PropertyMetadata(1.0));
    public static double GetEntranceScale(DependencyObject o) => (double)o.GetValue(EntranceScaleProperty);
    public static void SetEntranceScale(DependencyObject o, double v) => o.SetValue(EntranceScaleProperty, v);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.Loaded -= OnEntranceLoaded;
        if (!(bool)e.NewValue) return;
        el.Opacity = 0; // hidden until the first frame of the animation, so nothing flashes
        el.Loaded += OnEntranceLoaded;
    }

    private static void OnEntranceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el)
            PlayEntrance(el, GetEntranceDelay(el), GetEntranceOffset(el), GetEntranceScale(el));
    }

    /// <summary>Fades an element in while it rises (or drops) into place.</summary>
    public static void PlayEntrance(UIElement el, int delayMs = 0, double offsetY = 14, double fromScale = 1)
    {
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var (scale, translate) = Parts(el);
        el.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, Slow, EaseOut, begin));
        if (offsetY != 0) translate.BeginAnimation(TranslateTransform.YProperty, Anim(offsetY, 0, Slow, EaseOutStrong, begin));
        if (fromScale != 1)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(fromScale, 1, Slow, EaseOutStrong, begin));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(fromScale, 1, Slow, EaseOutStrong, begin));
        }
    }

    // ---------- Reveal: animated show/hide that also collapses ----------

    /// <summary>Shows or hides an element with a short fade and slide, collapsing it once hidden.</summary>
    public static void Reveal(FrameworkElement el, bool show, double offsetY = 6)
    {
        var (scale, translate) = Parts(el);
        bool visible = el.Visibility == Visibility.Visible && !(el.Tag is "hiding");
        if (show == visible) return;

        if (show)
        {
            el.Tag = null;
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, Medium, EaseOut));
            translate.BeginAnimation(TranslateTransform.YProperty, Anim(offsetY, 0, Medium, EaseOutStrong));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.96, 1, Medium, EaseOutStrong));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.96, 1, Medium, EaseOutStrong));
        }
        else
        {
            el.Tag = "hiding";
            var fade = Anim(null, 0, Fast, EaseOut, freeze: false); // needs a Completed handler
            fade.Completed += (_, _) =>
            {
                if (el.Tag is not "hiding") return;
                el.Tag = null;
                el.Visibility = Visibility.Collapsed;
            };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, Anim(null, offsetY / 2, Fast, EaseOut));
        }
    }

    // ---------- Plumbing ----------

    private sealed record TransformParts(ScaleTransform Scale, TranslateTransform Translate);

    private static readonly ConditionalWeakTable<UIElement, TransformParts> Transforms = new();

    /// <summary>Gives the element a scale + translate render transform we own (keeping any existing one).</summary>
    private static (ScaleTransform Scale, TranslateTransform Translate) Parts(UIElement el)
    {
        if (Transforms.TryGetValue(el, out var parts)) return (parts.Scale, parts.Translate);

        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        if (el.RenderTransform is { } existing && existing != Transform.Identity && !existing.IsFrozen)
            group.Children.Add(existing);
        group.Children.Add(scale);
        group.Children.Add(translate);
        el.RenderTransform = group;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        Transforms.Add(el, new TransformParts(scale, translate));
        return (scale, translate);
    }

    private static void Animate(UIElement el, DependencyProperty property, double to, Duration duration, IEasingFunction ease) =>
        el.BeginAnimation(property, Anim(null, to, duration, ease));

    private static AnimationTimeline Anim(double? from, double to, Duration duration, IEasingFunction ease, TimeSpan? begin = null, bool freeze = true)
    {
        // A delayed animation with a start value holds that value from the very first frame. A plain BeginTime
        // would leave the element at its resting value until the delay ran out, so it shows, vanishes and
        // then animates in: the entrance looks like it plays twice.
        if (from is double start && begin is TimeSpan wait && wait > TimeSpan.Zero && duration.HasTimeSpan)
        {
            var held = new DoubleAnimationUsingKeyFrames { Duration = wait + duration.TimeSpan };
            held.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            held.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(wait)));
            held.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(wait + duration.TimeSpan), ease));
            if (freeze) held.Freeze();
            return held;
        }
        var a = new DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
        if (from is double f) a.From = f;
        if (begin is TimeSpan b) a.BeginTime = b;
        if (freeze) a.Freeze();
        return a;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
