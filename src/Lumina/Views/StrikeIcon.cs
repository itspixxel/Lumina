using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shapes = System.Windows.Shapes;

namespace Lumina.Views;

/// <summary>
/// A line icon that can be struck through, Snipping Tool style: a diagonal line draws across it, with a gap cut
/// out of the icon on either side of the line. Used for muted / turned-off audio sources.
/// </summary>
public sealed class StrikeIcon : Grid
{
    // Everything outside a band along the diagonal; clipping the icon to it leaves the gap around the strike.
    private static readonly Geometry Cut = Freeze(Geometry.Parse("F0 M-2,-2 H26 V26 H-2 Z M4,0 L24,20 L20,24 L0,4 Z"));
    private static readonly Geometry Line = Freeze(Geometry.Parse("M4,4 L20,20"));

    private readonly Shapes.Path _icon;
    private readonly ScaleTransform _strikeScale = new(0, 0);
    private bool _struck;

    public StrikeIcon(string iconKey)
    {
        _icon = Make(iconKey);
        var strike = Make(iconKey);
        strike.Data = Line;
        strike.RenderTransformOrigin = new Point(4 / 24.0, 4 / 24.0); // grows from the line's start
        strike.RenderTransform = _strikeScale;
        Children.Add(_icon);
        Children.Add(strike);
    }

    public bool Struck => _struck;

    /// <summary>Draws the strike in (or wipes it away), with a little squash of the icon when animated.</summary>
    public void SetStruck(bool struck, bool animate = true)
    {
        if (struck == _struck && animate) return;
        _struck = struck;
        _icon.Clip = struck ? Cut : null;
        var ms = TimeSpan.FromMilliseconds(animate ? 220 : 0);
        var draw = new DoubleAnimation(struck ? 1 : 0, ms) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _strikeScale.BeginAnimation(ScaleTransform.ScaleXProperty, draw);
        _strikeScale.BeginAnimation(ScaleTransform.ScaleYProperty, draw);
        _icon.BeginAnimation(OpacityProperty, new DoubleAnimation(struck ? 0.6 : 1, ms));
        if (animate) Bump(this);
    }

    /// <summary>A quick squash-and-spring, for acknowledging a click.</summary>
    public static void Bump(UIElement el, double to = 0.82)
    {
        if (el.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform();
            el.RenderTransform = scale;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static Shapes.Path Make(string key) => new()
    {
        Data = (Geometry)Application.Current.Resources[key],
        Style = (Style)Application.Current.Resources["Icon"],
    };

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
