using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lumina.Capture;
using Lumina.Recording;
using CaptureMode = Lumina.Capture.CaptureMode;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

public partial class HomeWindow : ChromeWindow
{
    public HomeWindow()
    {
        InitializeComponent();
        Ffmpeg.StateChanged += UpdateRecordCard;
        App.Instance.Recorder.StateChanged += RecorderChanged;
        Closed += (_, _) =>
        {
            Ffmpeg.StateChanged -= UpdateRecordCard;
            App.Instance.Recorder.StateChanged -= RecorderChanged;
        };
        Activated += (_, _) =>
        {
            UpdateHint();
            UpdateRecordCard();
        };
        Loaded += (_, _) => UpdateRecordCard();
        Gallery.ViewAllRequested += () => GalleryWindow.ShowGallery();
    }

    /// <summary>Each mode card's tooltip names its key here (1 to 5) and its shortcut from anywhere, if set.</summary>
    private void UpdateModeTooltips()
    {
        static string Tip(string what, int key, CaptureMode mode) =>
            TrayIcon.ShortcutFor(mode) is { } global ? $"{what} ({key}), {global} from anywhere" : $"{what} ({key})";
        RegionCard.ToolTip = Tip("Capture a region", 1, CaptureMode.Region);
        WindowCard.ToolTip = Tip("Capture a window", 2, CaptureMode.Window);
        ScreenCard.ToolTip = Tip("Capture a whole screen", 3, CaptureMode.Screen);
        FreeformCard.ToolTip = Tip("Capture a freeform shape", 4, CaptureMode.Freeform);
    }

    private void UpdateHint()
    {
        UpdateModeTooltips();
        var hotkeys = App.Settings.Hotkeys ?? new();
        var main = hotkeys.FirstOrDefault(h => h.Mode == Capture.CaptureMode.Region) ?? hotkeys.FirstOrDefault();
        ShortcutPanel.Visibility = main != null ? Visibility.Visible : Visibility.Collapsed;
        ShortcutOffText.Visibility = main != null ? Visibility.Collapsed : Visibility.Visible;
        if (main != null) KeycapRow.Fill(ShortcutKeys, main.Parts());
    }

    /// <summary>The Record card warns until FFmpeg is set up, and shows download progress.</summary>
    private void UpdateRecordCard()
    {
        var shortcut = TrayIcon.ShortcutFor(CaptureMode.Record);
        string? badge = null;
        bool warn = false;
        if (App.Instance.Recorder.IsRecording) badge = "Recording";
        else if (Ffmpeg.IsInstalling) badge = Ffmpeg.InstallProgress is double p ? $"Installing {p:P0}" : "Installing";
        else if (!Ffmpeg.IsAvailable)
        {
            badge = "Set up";
            warn = true;
        }

        RecordBadge.Visibility = badge != null ? Visibility.Visible : Visibility.Collapsed;
        RecordBadgeText.Text = badge ?? "";
        RecordBadgeIcon.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        var tint = warn ? Color.FromRgb(0xFF, 0xC2, 0x4D) : ((SolidColorBrush)FindResource("AccentTextBrush")).Color;
        RecordBadgeText.Foreground = new SolidColorBrush(tint);
        RecordBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, tint.R, tint.G, tint.B));
        RecordCaption.Text = shortcut != null ? shortcut : "Video with sound";
        RecordCard.ToolTip = warn
            ? "Recording needs FFmpeg, a free download. Click to set it up."
            : shortcut != null ? $"Record your screen (5), {shortcut} from anywhere" : "Record your screen (5)";
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (App.Instance.Recorder.IsRecording) App.Instance.Recorder.Stop();
        else App.Instance.Recorder.Start();
    }

    private void RecorderChanged() => UpdateRecordCard();

    private int SelectedDelay =>
        DelayBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int s) ? s : 0;

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            App.Instance.Controller.Start(Enum.Parse<Capture.CaptureMode>(tag), SelectedDelay);
    }

    /// <summary>The gallery shows two folders; when they differ, ask which one.</summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Menus.ShowOutputFolders((UIElement)sender);

    private void Settings_Click(object sender, RoutedEventArgs e) => App.ShowSettings();

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
            InitialDirectory = Directory.Exists(App.Settings.OutputDirectory) ? App.Settings.OutputDirectory : null,
        };
        if (dialog.ShowDialog(this) == true) EditorWindow.OpenFile(dialog.FileName);
    }

    private static CaptureMode? ModeForKey(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => CaptureMode.Region,
        Key.D2 or Key.NumPad2 => CaptureMode.Window,
        Key.D3 or Key.NumPad3 => CaptureMode.Screen,
        Key.D4 or Key.NumPad4 => CaptureMode.Freeform,
        Key.D5 or Key.NumPad5 => CaptureMode.Record,
        _ => null,
    };

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        var mods = Keyboard.Modifiers;
        if (Gallery.HandleKey(e))
        {
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.None && ModeForKey(e.Key) is { } mode)
        {
            e.Handled = true;
            if (mode == CaptureMode.Record) App.Instance.Recorder.Toggle();
            else App.Instance.Controller.Start(mode, SelectedDelay);
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenImage_Click(this, e);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            App.ShowSettings();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.E)
        {
            Menus.ShowOutputFolders(this);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.F)
        {
            GalleryWindow.ShowGallery(focusSearch: true);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.None && e.Key == Key.F5)
        {
            Gallery.Refresh();
            e.Handled = true;
        }
    }
}
