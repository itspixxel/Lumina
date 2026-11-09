using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace Lumina.Views;

/// <summary>
/// A pill that slides in to report something (copied, saved, exporting) and fades out on its own.
/// It stays while the pointer is over it, so its link can be clicked. Long text trims to the space available.
/// </summary>
public sealed class Toast : Border
{
    private readonly TextBlock _text;
    private readonly TextBlock _linkHost;
    private readonly Hyperlink _link;
    private readonly Border _track, _fill;
    private readonly DispatcherTimer _timer = new();
    private Action? _linkAction;

    public Toast()
    {
        SetResourceReference(StyleProperty, "Pill");
        Padding = new Thickness(14, 7, 14, 7);
        HorizontalAlignment = HorizontalAlignment.Center;
        Visibility = Visibility.Collapsed;

        _fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _fill.SetResourceReference(BackgroundProperty, "AccentGradient");
        _track = new Border
        {
            Width = 120, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = _fill, Visibility = Visibility.Collapsed,
        };
        _track.SetResourceReference(BackgroundProperty, "StrokeBrush");
        DockPanel.SetDock(_track, Dock.Left);

        _link = new Hyperlink();
        _link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextBrush");
        _link.Click += (_, _) =>
        {
            _linkAction?.Invoke();
            Hide();
        };
        _linkHost = new TextBlock(_link) { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_linkHost, Dock.Right);

        _text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var row = new DockPanel();
        row.Children.Add(_track);
        row.Children.Add(_linkHost);
        row.Children.Add(_text);
        Child = row;

        Cursor = Cursors.Arrow;
        MouseLeftButtonDown += (_, e) => e.Handled = true; // clicks here aren't for whatever sits underneath
        _timer.Tick += (_, _) => Hide();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            if (_track.Visibility != Visibility.Visible && Visibility == Visibility.Visible) _timer.Start();
        };
    }

    /// <summary>Shows a message, with an optional link, for a few seconds.</summary>
    public void Show(string text, string? linkText = null, Action? linkAction = null, double seconds = 3.5)
    {
        _track.Visibility = Visibility.Collapsed;
        _linkAction = linkAction;
        _link.Inlines.Clear();
        if (linkText != null) _link.Inlines.Add(linkText);
        _linkHost.Visibility = linkText != null ? Visibility.Visible : Visibility.Collapsed;
        Present(text);
        _timer.Interval = TimeSpan.FromSeconds(linkText != null ? Math.Max(seconds, 5) : seconds);
        if (!IsMouseOver) _timer.Start();
    }

    /// <summary>Shows a progress bar with a label until <see cref="Show"/> or <see cref="Hide"/> replaces it.</summary>
    public void ShowProgress(string text)
    {
        _fill.Width = 0;
        _track.Visibility = Visibility.Visible;
        _linkHost.Visibility = Visibility.Collapsed;
        Present(text);
    }

    /// <summary>Moves the progress bar, from 0 to 1.</summary>
    public void SetProgress(double fraction) => _fill.Width = _track.Width * Math.Clamp(fraction, 0, 1);

    public void Hide()
    {
        _timer.Stop();
        Fx.Reveal(this, false, offsetY: 8);
    }

    private void Present(string text)
    {
        _timer.Stop();
        bool showing = Visibility == Visibility.Visible && Tag is not "hiding";
        _text.Text = text;
        _text.ToolTip = text;
        if (showing) Fx.PlayEntrance(_text, 0, offsetY: 4); // a new message in a toast that's already up
        else Fx.Reveal(this, true, offsetY: 8);
    }
}
