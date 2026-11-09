using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;

namespace Lumina.Views;

public enum DialogKind { Info, Question, Warning, Error }

/// <param name="Result">Returned from <see cref="LuminaDialog.Show"/> when this button is chosen.</param>
/// <param name="Primary">Accent-styled default button (Enter).</param>
/// <param name="Danger">Destructive choice, shown in red.</param>
/// <param name="Cancel">Chosen by Esc or by closing the dialog.</param>
public sealed record DialogButton(string Text, string Result, bool Primary = false, bool Danger = false, bool Cancel = false);

/// <summary>Themed replacement for MessageBox: a floating card that dims its owner while open.</summary>
public sealed class LuminaDialog : Window
{
    private static readonly Geometry WarningIcon = Geometry.Parse("M12,3.5 L21.5,20 H2.5 Z M12,10 V14 M12,17 V17.2");
    private static readonly Geometry ErrorIcon = Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M9,9 L15,15 M15,9 L9,15");
    private static readonly Geometry InfoIcon = Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M12,11 V16.5 M12,7.5 V7.7");

    private string? _result;
    private readonly string? _cancelResult;

    private LuminaDialog(string title, string message, DialogKind kind, Geometry? icon, DialogButton[] buttons)
    {
        _cancelResult = buttons.FirstOrDefault(b => b.Cancel)?.Result;

        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Foreground = (Brush)FindResource("TextBrush");
        UseLayoutRounding = true;

        var (tint, soft) = kind switch
        {
            DialogKind.Error or DialogKind.Warning => ((Brush)FindResource("DangerBrush"), (Brush)new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x5C, 0x7A))),
            _ => ((Brush)FindResource("AccentTextBrush"), (Brush)FindResource("AccentSoftBrush")),
        };

        var iconTile = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = soft,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new ShapePath
            {
                Style = (Style)FindResource("Icon"),
                Data = icon ?? kind switch { DialogKind.Error => ErrorIcon, DialogKind.Warning => WarningIcon, _ => InfoIcon },
                Stroke = tint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new StackPanel { Margin = new Thickness(16, 1, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)FindResource("DisplayFont"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)FindResource("Text2Brush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
        });

        var header = new DockPanel();
        DockPanel.SetDock(iconTile, Dock.Left);
        header.Children.Add(iconTile);
        header.Children.Add(text);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        foreach (var spec in buttons)
        {
            var button = new Button
            {
                Content = spec.Text,
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = spec.Primary,
                IsCancel = spec.Cancel,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            if (spec.Primary) button.Style = (Style)FindResource("AccentButton");
            else if (spec.Danger) button.Style = (Style)FindResource("DangerButton");
            button.Click += (_, _) => Finish(spec.Result);
            buttonRow.Children.Add(button);
        }

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(buttonRow);

        var card = new Border
        {
            Width = 420,
            Margin = new Thickness(28),
            Padding = new Thickness(22, 22, 22, 18),
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResource("RaisedBrush"),
            BorderBrush = (Brush)FindResource("StrokeStrongBrush"),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            Child = body,
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not Button) DragMove();
        };
        Content = card;

        Loaded += (_, _) =>
        {
            Fx.PlayEntrance(card, 0, offsetY: 10, fromScale: 0.94);
            (buttonRow.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault) ?? buttonRow.Children.OfType<Button>().LastOrDefault())?.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _cancelResult == null)
            {
                Finish(null);
                e.Handled = true;
            }
        };
    }

    private void Finish(string? result)
    {
        _result = result;
        Close();
    }

    /// <summary>Shows the dialog modally and returns the chosen button's result (the Cancel button's if dismissed).</summary>
    public static string? Show(Window? owner, string title, string message, DialogKind kind, Geometry? icon, params DialogButton[] buttons)
    {
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                  ?? Application.Current.Windows.OfType<HomeWindow>().FirstOrDefault(w => w.IsVisible);

        var dialog = new LuminaDialog(title, message, kind, icon, buttons);
        if (owner is { IsVisible: true } && owner != dialog)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        var dim = DimAdorner.Attach(dialog.Owner);
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            dim?.Detach();
        }
        return dialog._result ?? dialog._cancelResult;
    }

    /// <summary>A simple notice with one OK button.</summary>
    /// <summary>The Shift+Delete question: deleting skips the Recycle Bin.</summary>
    public static bool ConfirmPermanentDelete(Window? owner, string what) =>
        Show(owner, $"Permanently delete {what}?", "It skips the Recycle Bin and can't be undone.", DialogKind.Warning, null,
            new DialogButton("Cancel", "cancel", Cancel: true),
            new DialogButton("Delete permanently", "delete", Danger: true)) == "delete";

    public static void Alert(Window? owner, string title, string message, DialogKind kind = DialogKind.Error) =>
        Show(owner, title, message, kind, null, new DialogButton("OK", "ok", Primary: true, Cancel: true));

    /// <summary>Darkens the owner's content while a dialog is up.</summary>
    private sealed class DimAdorner : Adorner
    {
        private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0x05, 0x04, 0x0A)));
        private readonly AdornerLayer _layer;

        private DimAdorner(UIElement target, AdornerLayer layer) : base(target)
        {
            _layer = layer;
            IsHitTestVisible = true;
        }

        public static DimAdorner? Attach(Window? owner)
        {
            if (owner?.Content is not UIElement content || AdornerLayer.GetAdornerLayer(content) is not { } layer) return null;
            var dim = new DimAdorner(content, layer);
            layer.Add(dim);
            Fx.PlayEntrance(dim, 0, offsetY: 0);
            return dim;
        }

        public void Detach() => _layer.Remove(this);

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            dc.DrawRectangle(Shade, null, new Rect(-200, -200, size.Width + 400, size.Height + 400));
        }

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }
}
