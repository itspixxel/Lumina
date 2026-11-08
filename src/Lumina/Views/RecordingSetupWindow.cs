using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Lumina.Capture;
using Lumina.Recording;
using CaptureMode = Lumina.Capture.CaptureMode;
using Lumina.Services;
using Microsoft.Win32;
using ShapePath = System.Windows.Shapes.Path;

namespace Lumina.Views;

/// <summary>
/// Welcome card on first launch, and the place to get FFmpeg for recording. The download runs in the
/// background, so the card can be closed and capture keeps working meanwhile.
/// </summary>
public sealed class RecordingSetupWindow : Window
{
    private static RecordingSetupWindow? _current;

    private readonly TextBlock _status;
    private readonly Border _track, _fill;
    private readonly Button _primary, _secondary, _custom;
    private readonly TextBlock _body;

    private RecordingSetupWindow(bool firstRun)
    {
        Title = firstRun ? "Welcome to Lumina" : "Set up screen recording";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = true;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/lumina.ico"));
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Foreground = (Brush)FindResource("TextBrush");
        UseLayoutRounding = true;

        var body = new StackPanel();

        // Header
        var header = new DockPanel();
        var tile = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(13),
            Background = (Brush)FindResource("AccentGradient"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new ShapePath
            {
                Style = (Style)FindResource("Icon"),
                Data = (Geometry)FindResource(firstRun ? "IconRegion" : "IconRecord"),
                Stroke = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        header.Children.Add(tile);
        var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = Title,
            FontFamily = (FontFamily)FindResource("DisplayFont"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        titles.Children.Add(new TextBlock
        {
            Text = firstRun ? "Screenshots, markup and screen recording." : "One quick download and you're ready to record.",
            Foreground = (Brush)FindResource("Text2Brush"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        header.Children.Add(titles);
        body.Children.Add(header);

        if (firstRun) body.Children.Add(ShortcutLines());

        // Recording section
        var section = new StackPanel();
        var sectionTitle = new DockPanel();
        sectionTitle.Children.Add(new ShapePath
        {
            Style = (Style)FindResource("Icon"),
            Data = (Geometry)FindResource("IconRecord"),
            Stroke = (Brush)FindResource("AccentTextBrush"),
            Margin = new Thickness(0, 0, 8, 0),
        });
        sectionTitle.Children.Add(new TextBlock { Text = "Screen recording uses FFmpeg", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        section.Children.Add(sectionTitle);
        _body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Text2Brush"),
            LineHeight = 19,
            Margin = new Thickness(0, 6, 0, 0),
        };
        section.Children.Add(_body);

        _fill = new Border { CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("AccentGradient"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)FindResource("RaisedBrush"),
            Margin = new Thickness(0, 14, 0, 0),
            Child = _fill,
            Visibility = Visibility.Collapsed,
        };
        section.Children.Add(_track);
        _status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        section.Children.Add(_status);

        body.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"),
            Background = (Brush)FindResource("WindowBrush"),
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(16, 14, 16, 14),
            Child = section,
        });

        // Buttons
        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        _custom = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Margin = new Thickness(-10, 0, 0, 0),
            Content = new TextBlock { Text = "Use my own ffmpeg.exe…", Foreground = (Brush)FindResource("AccentTextBrush") },
        };
        _custom.Click += (_, _) => PickCustom();
        DockPanel.SetDock(_custom, Dock.Left);
        buttons.Children.Add(_custom);

        _primary = new Button { Style = (Style)FindResource("AccentButton"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        _primary.Click += (_, _) => Primary();
        _secondary = new Button { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        _secondary.Click += (_, _) => Secondary();
        DockPanel.SetDock(_primary, Dock.Right);
        DockPanel.SetDock(_secondary, Dock.Right);
        buttons.Children.Add(_primary);
        buttons.Children.Add(_secondary);
        body.Children.Add(buttons);

        var card = new Border
        {
            Width = 480,
            Margin = new Thickness(28),
            Padding = new Thickness(24, 22, 24, 20),
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => Fx.PlayEntrance(card, 0, offsetY: 10, fromScale: 0.94);
        Ffmpeg.StateChanged += Sync;
        Closed += (_, _) =>
        {
            Ffmpeg.StateChanged -= Sync;
            _current = null;
            if (!App.Settings.SetupShown)
            {
                App.Settings.SetupShown = true;
                SettingsService.Save(App.Settings);
            }
        };
        Sync();
    }

    /// <summary>"Press Win+Shift+S to capture, Win+Shift+R to record" using the user's actual shortcuts.</summary>
    private UIElement ShortcutLines()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        void Line(CaptureMode mode, string what)
        {
            var binding = App.Settings.Hotkeys?.FirstOrDefault(h => h.Mode == mode);
            if (binding == null) return;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var keys = new StackPanel { Orientation = Orientation.Horizontal };
            KeycapRow.Fill(keys, binding.Parts());
            row.Children.Add(keys);
            row.Children.Add(new TextBlock { Text = what, Foreground = (Brush)FindResource("Text2Brush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            panel.Children.Add(row);
        }
        Line(CaptureMode.Region, "to capture, from anywhere");
        Line(CaptureMode.Record, "to record your screen");
        panel.Children.Add(new TextBlock
        {
            Text = "Lumina lives in the tray. Change shortcuts any time in Settings.",
            FontSize = 12,
            Foreground = (Brush)FindResource("Text3Brush"),
            Margin = new Thickness(0, 10, 0, 0),
        });
        return panel;
    }

    public static void ShowSetup(bool firstRun)
    {
        if (_current != null)
        {
            _current.Activate();
            return;
        }
        var window = _current = new RecordingSetupWindow(firstRun);
        var owner = Application.Current.Windows.OfType<HomeWindow>().FirstOrDefault(w => w.IsVisible);
        if (owner != null)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        window.Show();
        window.Activate();
    }

    private void Sync()
    {
        bool installing = Ffmpeg.IsInstalling;
        bool ready = !installing && Ffmpeg.IsAvailable;

        _body.Text = ready
            ? "Everything's set. Pick Record on the home screen or press your record shortcut."
            : $"FFmpeg is the free, open-source video engine behind most screen recorders. Lumina downloads it once ({Ffmpeg.DownloadSizeText}) into its own folder. Nothing else on your PC changes, and screenshots work without it.";

        _track.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        if (installing)
        {
            double p = Ffmpeg.InstallProgress ?? 0;
            _fill.Width = Math.Max(6, (_track.ActualWidth > 0 ? _track.ActualWidth : 430) * p);
        }

        string stage = Ffmpeg.InstallStage;
        bool failed = !installing && !ready && stage.StartsWith("Download failed");
        _status.Visibility = installing || ready || failed ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = ready ? "✓  Screen recording is ready" : stage;
        _status.Foreground = (Brush)FindResource(failed ? "DangerBrush" : ready ? "AccentTextBrush" : "Text2Brush");
        _status.FontWeight = ready ? FontWeights.SemiBold : FontWeights.Normal;

        _custom.Visibility = ready || installing ? Visibility.Collapsed : Visibility.Visible;
        if (ready)
        {
            _primary.Content = "Done";
            _secondary.Visibility = Visibility.Collapsed;
        }
        else if (installing)
        {
            _primary.Content = "Hide";
            _secondary.Content = "Cancel";
            _secondary.Visibility = Visibility.Visible;
        }
        else
        {
            _primary.Content = failed ? "Try again" : $"Download";
            _secondary.Content = "Not now";
            _secondary.Visibility = Visibility.Visible;
        }
    }

    private void Primary()
    {
        if (Ffmpeg.IsInstalling || Ffmpeg.IsAvailable) Close();
        else _ = Ffmpeg.InstallAsync();
    }

    private void Secondary()
    {
        if (Ffmpeg.IsInstalling) Ffmpeg.CancelInstall();
        else Close();
    }

    private async void PickCustom()
    {
        var dialog = new OpenFileDialog { Title = "Choose ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        if (!await Ffmpeg.UseCustomAsync(dialog.FileName))
            LuminaDialog.Alert(this, "That doesn't look like FFmpeg", "Lumina couldn't run it as ffmpeg.exe. Pick the ffmpeg.exe from an FFmpeg build's bin folder.");
    }
}
