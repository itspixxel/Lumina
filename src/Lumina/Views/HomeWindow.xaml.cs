using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using Lumina.Capture;
using Lumina.Services;
using Microsoft.Win32;

namespace Lumina.Views;

public sealed class RecentItem(string path, string name, string info, BitmapSource? thumb) : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Path { get; } = path;
    public string Name { get; } = name;
    public string Info { get; } = info;
    public BitmapSource? Thumb { get; } = thumb;

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
        Closed += (_, _) => App.Instance.Controller.Captured -= RefreshRecent;
        Activated += (_, _) => UpdateHint();
        Loaded += (_, _) => RefreshRecent();
    }

    private void UpdateHint()
    {
        var hotkeys = App.Settings.Hotkeys ?? new();
        var main = hotkeys.FirstOrDefault(h => h.Mode == Capture.CaptureMode.Region) ?? hotkeys.FirstOrDefault();
        ShortcutPanel.Visibility = main != null ? Visibility.Visible : Visibility.Collapsed;
        ShortcutOffText.Visibility = main != null ? Visibility.Collapsed : Visibility.Visible;
        if (main != null) KeycapRow.Fill(ShortcutKeys, main.Parts());
    }

    public async void RefreshRecent()
    {
        int version = ++_refreshVersion;
        string dir = App.Settings.OutputDirectory;
        var items = await Task.Run(() => LoadRecent(dir, 60));
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

    private static List<RecentItem> LoadRecent(string dir, int count)
    {
        var result = new List<RecentItem>();
        if (!Directory.Exists(dir)) return result;

        var files = new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => OutputService.IsImageFile(f.FullName))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(count);

        foreach (var file in files)
        {
            BitmapSource? thumb = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = 360;
                bmp.UriSource = new Uri(file.FullName);
                bmp.EndInit();
                bmp.Freeze();
                thumb = bmp;
            }
            catch
            {
                // Still being written, or not a readable image; skip the thumbnail.
            }
            result.Add(new RecentItem(file.FullName, file.Name, FriendlyAge(file.LastWriteTime), thumb));
        }
        return result;
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OutputService.OpenOutputFolder();

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
        else if (mods == ModifierKeys.None && e.Key == Key.Delete)
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
            EditorWindow.OpenFile(only.Path);
            e.Handled = true;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void SelectionClear_Click(object sender, RoutedEventArgs e) => ClearSelection();
    private void SelectionCopy_Click(object sender, RoutedEventArgs e) => CopyItems(SelectedItems);
    private void SelectionFolder_Click(object sender, RoutedEventArgs e) => OutputService.ShowInFolder(SelectedItems.Select(i => i.Path).ToList());
    private void SelectionDelete_Click(object sender, RoutedEventArgs e) => DeleteItems(SelectedItems);

    // ---------- Actions ----------

    /// <summary>One capture copies as an image (paste into chats/docs); several copy as files (paste into Explorer, Discord…).</summary>
    private async void CopyItems(IReadOnlyList<RecentItem> items)
    {
        try
        {
            if (items is [var single])
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

    private void DeleteItems(IReadOnlyList<RecentItem> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            OutputService.Recycle(item.Path);
            item.IsSelected = false;
        }
        UpdateSelectionUi();
        RefreshRecent();
    }

    // ---------- Context menu ----------

    private void Recent_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement tile || ItemOf(sender) is not { } item) return;
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
            menu.Items.Add(MenuEntry("Edit", "IconPen", () => EditorWindow.OpenFile(one.Path), "Enter"));
            menu.Items.Add(MenuEntry("Copy image", "IconCopy", () => CopyItems(targets), "Ctrl+C"));
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
