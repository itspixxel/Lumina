using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using Lumina.Capture;
using Lumina.Editor;
using Lumina.Recording;
using CaptureMode = Lumina.Capture.CaptureMode;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

public sealed class RecentItem(string path, string name, string info, BitmapSource? thumb, bool isVideo) : INotifyPropertyChanged
{
    private bool _isSelected, _isRenaming;

    public string Path { get; } = path;
    public string Name { get; } = name;
    public string Info { get; } = info;
    public BitmapSource? Thumb { get; } = thumb;
    public bool IsVideo { get; } = isVideo;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>The tile's name is an edit box.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class HomeWindow : ChromeWindow
{
    private static readonly Geometry IconSelectCircle = Frozen(Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M8,12.5 L11,15.5 L16.5,9.5"));

    private int _refreshVersion;
    private List<RecentItem> _items = new();
    private RecentItem? _anchor;

    public HomeWindow()
    {
        InitializeComponent();
        App.Instance.Controller.Captured += RefreshRecent;
        Ffmpeg.StateChanged += UpdateRecordCard;
        App.Instance.Recorder.StateChanged += RecorderChanged;
        Closed += (_, _) =>
        {
            App.Instance.Controller.Captured -= RefreshRecent;
            Ffmpeg.StateChanged -= UpdateRecordCard;
            App.Instance.Recorder.StateChanged -= RecorderChanged;
        };
        Activated += (_, _) =>
        {
            UpdateHint();
            UpdateRecordCard();
        };
        Loaded += (_, _) =>
        {
            RefreshRecent();
            UpdateRecordCard();
        };
    }

    /// <summary>Each mode card's tooltip names its key here (1–5) and its shortcut from anywhere, if set.</summary>
    private void UpdateModeTooltips()
    {
        static string Tip(string what, int key, CaptureMode mode) =>
            TrayIcon.ShortcutFor(mode) is { } global ? $"{what} ({key}) · {global} from anywhere" : $"{what} ({key})";
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
            : shortcut != null ? $"Record your screen (5) · {shortcut} from anywhere" : "Record your screen (5)";
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (App.Instance.Recorder.IsRecording) App.Instance.Recorder.Stop();
        else App.Instance.Recorder.Start();
    }

    private void RecorderChanged()
    {
        UpdateRecordCard();
        if (!App.Instance.Recorder.IsRecording) RefreshRecent(); // a recording was just saved (or discarded)
    }

    public async void RefreshRecent()
    {
        int version = ++_refreshVersion;
        string images = App.Settings.OutputDirectory, videos = App.Settings.RecordingsDirectory;
        var items = await Task.Run(() => LoadRecentAsync(images, videos, 60));
        if (version != _refreshVersion) return;

        // Keep the selection across refreshes (new captures, deletes elsewhere).
        var selected = _items.Where(i => i.IsSelected).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) item.IsSelected = selected.Contains(item.Path);
        _anchor = _anchor == null ? null : items.FirstOrDefault(i => string.Equals(i.Path, _anchor.Path, StringComparison.OrdinalIgnoreCase));

        var known = _items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _items = items;
        RecentList.ItemsSource = items;
        AnimateNewTiles(items, known);
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUi();
    }

    /// <summary>Tiles that weren't on screen before cascade in; existing ones stay put.</summary>
    private void AnimateNewTiles(List<RecentItem> items, HashSet<string> known)
    {
        bool firstLoad = known.Count == 0;
        Dispatcher.BeginInvoke(() =>
        {
            int order = 0;
            foreach (var item in items)
            {
                if (known.Contains(item.Path)) continue;
                if (RecentList.ItemContainerGenerator.ContainerFromItem(item) is not UIElement tile) continue;
                int delay = firstLoad ? 180 + Math.Min(order, 12) * 35 : order * 35;
                Fx.PlayEntrance(tile, delay, offsetY: 16, fromScale: firstLoad ? 1 : 0.92);
                order++;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The newest screenshots and recordings together, newest first.</summary>
    private static async Task<List<RecentItem>> LoadRecentAsync(string imageDir, string videoDir, int count)
    {
        static IEnumerable<FileInfo> Files(string dir, Func<string, bool> match) =>
            Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles().Where(f => match(f.FullName)) : Enumerable.Empty<FileInfo>();

        var files = Files(imageDir, OutputService.IsImageFile)
            .Concat(Files(videoDir, OutputService.IsVideoFile))
            .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase) // both folders can be the same
            .OrderByDescending(f => f.LastWriteTime)
            .Take(count)
            .ToList();

        using var ffmpegSlots = new SemaphoreSlim(4);
        return (await Task.WhenAll(files.Select(async file =>
        {
            bool video = OutputService.IsVideoFile(file.FullName);
            var thumb = video ? await VideoThumbAsync(file, ffmpegSlots) : LoadThumb(file.FullName);
            return new RecentItem(file.FullName, file.Name, FriendlyAge(file.LastWriteTime), thumb, video);
        }))).ToList();
    }

    private static BitmapSource? LoadThumb(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 360;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Still being written, or not a readable image; skip the thumbnail.
            return null;
        }
    }

    /// <summary>A recording's first frame, grabbed by FFmpeg once and kept in temp until the file changes.</summary>
    private static async Task<BitmapSource?> VideoThumbAsync(FileInfo file, SemaphoreSlim slots)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..16];
        string thumb = Path.Combine(AppPaths.TempDir, "thumbs", key + ".jpg");
        if (!File.Exists(thumb) && Ffmpeg.IsAvailable)
        {
            await slots.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
                await Ffmpeg.RunJobAsync($"-i {Ffmpeg.Quote(file.FullName)} -frames:v 1 -vf scale=360:-2 -q:v 4 {Ffmpeg.Quote(thumb)}", 0, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Thumbnail for {file.Name}");
            }
            finally
            {
                slots.Release();
            }
        }
        return File.Exists(thumb) ? LoadThumb(thumb) : null;
    }

    private static string FriendlyAge(DateTime time)
    {
        var age = DateTime.Now - time;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 7) return time.ToString("dddd, HH:mm");
        return time.ToString("d MMM yyyy");
    }

    private int SelectedDelay =>
        DelayBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int s) ? s : 0;

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            App.Instance.Controller.Start(Enum.Parse<Capture.CaptureMode>(tag), SelectedDelay);
    }

    /// <summary>The gallery shows two folders; when they differ, ask which one.</summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string shots = App.Settings.OutputDirectory, videos = App.Settings.RecordingsDirectory;
        if (SamePath(shots, videos))
        {
            OutputService.OpenFolder(shots);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("Screenshots", "IconRegion", () => OutputService.OpenFolder(shots)));
        menu.Items.Add(MenuEntry("Recordings", "IconRecord", () => OutputService.OpenFolder(videos)));
        menu.IsOpen = true;
    }

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

    // ---------- Selection ----------

    private List<RecentItem> SelectedItems => _items.Where(i => i.IsSelected).ToList();
    private bool AnySelected => _items.Any(i => i.IsSelected);

    private void UpdateSelectionUi()
    {
        int count = _items.Count(i => i.IsSelected);
        Fx.Reveal(SelectionBar, count > 0);
        Fx.Reveal(RecentHint, count == 0, offsetY: -4);
        SelectionText.Text = $"{count} selected";
        RecentList.Tag = count > 0; // shows every tile's select circle while in selection mode
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

    private void Toggle(RecentItem item)
    {
        item.IsSelected = !item.IsSelected;
        _anchor = item;
        UpdateSelectionUi();
    }

    /// <summary>Shift+click: selects everything between the last clicked tile and this one.</summary>
    private void SelectRange(RecentItem item)
    {
        int from = _anchor == null ? -1 : _items.IndexOf(_anchor);
        int to = _items.IndexOf(item);
        if (from < 0 || to < 0)
        {
            Toggle(item);
            return;
        }
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _items[i].IsSelected = true;
        UpdateSelectionUi();
    }

    private void SelectAll()
    {
        foreach (var item in _items) item.IsSelected = true;
        UpdateSelectionUi();
    }

    private void ClearSelection()
    {
        foreach (var item in _items) item.IsSelected = false;
        _anchor = null;
        UpdateSelectionUi();
    }

    private static RecentItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as RecentItem;

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift)) SelectRange(item);
        else if (mods.HasFlag(ModifierKeys.Control)) Toggle(item);
        else if (AnySelected) Toggle(item); // in selection mode, clicks keep selecting
        else Open(item);
    }

    private static void Open(RecentItem item)
    {
        if (item.IsVideo) VideoEditorWindow.Open(item.Path);
        else EditorWindow.OpenFile(item.Path);
    }

    private void SelectDot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        e.Handled = true; // don't let the tile's click open the editor
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SelectRange(item);
        else Toggle(item);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        var mods = Keyboard.Modifiers;
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
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            App.ShowSettings();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.E)
        {
            if (AnySelected) OutputService.ShowInFolder(SelectedItems.Select(i => i.Path).ToList());
            else OpenFolder_Click(this, e);
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.None && e.Key == Key.F5)
        {
            RefreshRecent();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.A && _items.Count > 0)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (!AnySelected)
        {
            return;
        }
        else if (mods == ModifierKeys.None && e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && mods is ModifierKeys.None or ModifierKeys.Shift)
        {
            DeleteItems(SelectedItems);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.C)
        {
            CopyItems(SelectedItems);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.None && e.Key == Key.Enter && SelectedItems is [var only])
        {
            Open(only);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.None && e.Key == Key.F2 && SelectedItems is [var renaming])
        {
            StartRename(renaming);
            e.Handled = true;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void SelectionClear_Click(object sender, RoutedEventArgs e) => ClearSelection();
    private void SelectionCopy_Click(object sender, RoutedEventArgs e) => CopyItems(SelectedItems);
    private void SelectionFolder_Click(object sender, RoutedEventArgs e) => OutputService.ShowInFolder(SelectedItems.Select(i => i.Path).ToList());
    private void SelectionDelete_Click(object sender, RoutedEventArgs e) => DeleteItems(SelectedItems);

    // ---------- Actions ----------

    /// <summary>
    /// One screenshot copies as an image (paste into chats/docs); a recording, or several items, copy as files
    /// (paste into Explorer, Discord…).
    /// </summary>
    private async void CopyItems(IReadOnlyList<RecentItem> items)
    {
        try
        {
            if (items is [{ IsVideo: true } video])
            {
                ClipboardService.CopyFile(video.Path);
            }
            else if (items is [var single])
            {
                await ClipboardService.CopyImageAsync(OutputService.LoadImage(single.Path));
            }
            else if (items.Count > 1)
            {
                var files = new StringCollection();
                files.AddRange(items.Select(i => i.Path).ToArray());
                Clipboard.SetFileDropList(files);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy recent");
        }
    }

    /// <summary>Moves to the Recycle Bin; with Shift held, asks and then deletes for good.</summary>
    private void DeleteItems(IReadOnlyList<RecentItem> items)
    {
        if (items.Count == 0) return;
        bool permanent = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (permanent && !LuminaDialog.ConfirmPermanentDelete(this, items is [var only] ? only.Name : $"{items.Count} items"))
            return;
        foreach (var item in items)
        {
            if (permanent) OutputService.DeletePermanently(item.Path);
            else OutputService.Recycle(item.Path);
            item.IsSelected = false;
        }
        UpdateSelectionUi();
        RefreshRecent();
    }

    // ---------- Rename ----------

    private void StartRename(RecentItem item)
    {
        foreach (var other in _items) other.IsRenaming = false;
        item.IsRenaming = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible || ItemOf(box) is not { } item) return;
        box.Text = Path.GetFileNameWithoutExtension(item.Name);
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ItemOf(box) is not { } item) return;
        if (e.Key == Key.Enter) FinishRename(item, box.Text);
        else if (e.Key == Key.Escape) FinishRename(item, null);
        else return;
        e.Handled = true;
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && ItemOf(box) is { IsRenaming: true } item) FinishRename(item, box.Text);
    }

    /// <summary>Renames the file (keeping its extension and folder); null cancels.</summary>
    private void FinishRename(RecentItem item, string? newName)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        string name = newName?.Trim() ?? "";
        if (name.Length == 0 || name == Path.GetFileNameWithoutExtension(item.Path)) return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            LuminaDialog.Alert(this, "That name won't work", "File names can't contain \\ / : * ? \" < > |", DialogKind.Warning);
            return;
        }
        bool open = Application.Current.Windows.OfType<EditorWindow>().Any(w => SamePath(w.FilePath, item.Path))
                    || Application.Current.Windows.OfType<VideoEditorWindow>().Any(w => SamePath(w.FilePath, item.Path));
        if (open)
        {
            LuminaDialog.Alert(this, $"{item.Name} is open in an editor", "Close it there first, then rename it.", DialogKind.Warning);
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(item.Path)!, name + Path.GetExtension(item.Path));
        if (File.Exists(target) && !SamePath(target, item.Path))
        {
            LuminaDialog.Alert(this, "That name is taken", $"There's already a {Path.GetFileName(target)} in this folder.", DialogKind.Warning);
            return;
        }
        try
        {
            File.Move(item.Path, target);
            ProjectStore.Move(item.Path, target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Rename");
            LuminaDialog.Alert(this, "Couldn't rename it", ex.Message);
            return;
        }
        RefreshRecent();
    }

    private static bool SamePath(string? a, string b) => a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ---------- Context menu ----------

    private void Recent_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement tile || ItemOf(sender) is not { } item || item.IsRenaming) return;
        e.Handled = true;

        // Like Explorer: right-clicking outside the selection switches the selection to that tile.
        if (!item.IsSelected && AnySelected)
        {
            ClearSelection();
            Toggle(item);
        }
        var targets = item.IsSelected ? SelectedItems : new List<RecentItem> { item };

        var menu = new ContextMenu { PlacementTarget = tile, Placement = PlacementMode.MousePoint };
        if (targets is [var one])
        {
            menu.Items.Add(one.IsVideo
                ? MenuEntry("Play and trim", "IconPlay", () => Open(one), "Enter")
                : MenuEntry("Edit", "IconPen", () => Open(one), "Enter"));
            menu.Items.Add(MenuEntry(one.IsVideo ? "Copy video" : "Copy image", "IconCopy", () => CopyItems(targets), "Ctrl+C"));
            menu.Items.Add(MenuEntry("Rename", "IconText", () => StartRename(one), "F2"));
            menu.Items.Add(MenuEntry("Show in folder", "IconFolder", () => OutputService.ShowInFolder(one.Path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(one.IsSelected ? "Deselect" : "Select", IconSelectCircle, () => Toggle(one)));
        }
        else
        {
            menu.Items.Add(MenuEntry($"Copy {targets.Count} files", "IconCopy", () => CopyItems(targets), "Ctrl+C"));
            menu.Items.Add(MenuEntry("Show in folder", "IconFolder", () => OutputService.ShowInFolder(targets.Select(i => i.Path).ToList())));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Select all", IconSelectCircle, SelectAll, "Ctrl+A"));
            menu.Items.Add(MenuEntry("Clear selection", "IconClose", ClearSelection, "Esc"));
        }
        menu.Items.Add(new Separator());
        var delete = MenuEntry(targets.Count == 1 ? "Delete" : $"Delete {targets.Count} items", "IconTrash", () => DeleteItems(targets), "Del");
        delete.ToolTip = "Hold Shift to delete permanently";
        delete.Style = (Style)FindResource("DangerMenuItem");
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    private MenuItem MenuEntry(string header, string iconKey, Action action, string gesture = "") =>
        MenuEntry(header, (Geometry)FindResource(iconKey), action, gesture);

    private MenuItem MenuEntry(string header, Geometry icon, Action action, string gesture = "")
    {
        var entry = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Icon = new ShapePath { Style = (Style)FindResource("Icon"), Data = icon },
        };
        entry.Click += (_, _) => action();
        return entry;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
