using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumina.Views;

/// <summary>
/// Text where each character rolls when it changes, like an odometer: the old character slides up and out
/// while the new one slides in from below. Digits use tabular figures so the width doesn't wobble.
/// </summary>
public sealed class RollingText : StackPanel
{
    private readonly List<Grid> _slots = new();
    private string _text = "";

    public RollingText()
    {
        Orientation = Orientation.Horizontal;
        ClipToBounds = true;
    }

    public string Text
    {
        get => _text;
        set => SetText(value, animate: true);
    }

    public void SetText(string text, bool animate)
    {
        if (text == _text) return;
        string old = _text;
        _text = text;

        // Slots line up from the right, so 9:59 → 10:00 rolls the existing digits and adds one on the left.
        while (_slots.Count < text.Length)
        {
            var slot = new Grid { ClipToBounds = true };
            _slots.Insert(0, slot);
            Children.Insert(0, slot);
        }
        while (_slots.Count > text.Length)
        {
            Children.Remove(_slots[0]);
            _slots.RemoveAt(0);
        }

        int offset = text.Length - old.Length;
        for (int i = 0; i < text.Length; i++)
        {
            int j = i - offset;
            char was = j >= 0 && j < old.Length ? old[j] : '\0';
            if (was == text[i]) continue;
            Roll(_slots[i], text[i].ToString(), animate && was != '\0');
        }
    }

    private void Roll(Grid slot, string ch, bool animate)
    {
        var incoming = new TextBlock { Text = ch };
        Typography.SetNumeralAlignment(incoming, FontNumeralAlignment.Tabular);
        if (!animate)
        {
            slot.Children.Clear();
            slot.Children.Add(incoming);
            return;
        }

        double h = Math.Max(10, TextElement.GetFontSize(this) * 0.9);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);

        // Anything still leaving from an earlier roll is dropped; the current one slides out.
        while (slot.Children.Count > 1) slot.Children.RemoveAt(0);
        if (slot.Children.Count == 1 && slot.Children[0] is TextBlock outgoing)
        {
            var up = new TranslateTransform();
            outgoing.RenderTransform = up;
            up.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -h, duration) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, duration);
            fade.Completed += (_, _) => slot.Children.Remove(outgoing);
            outgoing.BeginAnimation(OpacityProperty, fade);
        }

        var rise = new TranslateTransform(0, h);
        incoming.RenderTransform = rise;
        incoming.Opacity = 0;
        slot.Children.Add(incoming);
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(h, 0, duration) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }
}
