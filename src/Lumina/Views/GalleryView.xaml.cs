using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lumina.Editor;
using Lumina.Recording;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>
/// Screenshots and recordings as tiles, newest first, with multi-select, copy, rename, reveal and delete.
/// Keeps itself up to date from <see cref="LibraryIndex"/>; the host window forwards keys to <see cref="HandleKey"/>.
/// </summary>
public partial class GalleryView : UserControl
{
    private static readonly Geometry IconSelectCircle = Frozen(Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M8,12.5 L11,15.5 L16.5,9.5"));

    private int _refreshVersion;
    private bool _subscribed, _stale;
    private List<GalleryItem> _items = new();
    private GalleryItem? _anchor;

    public GalleryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe(true);
        Unloaded += (_, _) => Subscribe(false);
        // Changes that arrive while the window is hidden (in the tray) are picked up when it's shown again.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _stale) Refresh();
        };
    }

    /// <summary>Show at most this many of the newest files; 0 shows everything.</summary>
    public int Limit { get; set; }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public bool AnySelected => _items.Any(i => i.IsSelected);
    public IReadOnlyList<string> SelectedPaths => SelectedItems.Select(i => i.Path).ToList();
    private List<GalleryItem> SelectedItems => _items.Where(i => i.IsSelected).ToList();
    private Window? Owner => Window.GetWindow(this);

    private void Subscribe(bool on)
    {
        if (on == _subscribed) return;
        _subscribed = on;
        if (on)
        {
            App.Instance.Controller.Captured += Refresh;
            App.Instance.Recorder.StateChanged += RecorderChanged;
            LibraryIndex.Changed += Refresh;
            Ffmpeg.StateChanged += FfmpegChanged;
            Refresh();
        }
        else
        {
            App.Instance.Controller.Captured -= Refresh;
            App.Instance.Recorder.StateChanged -= RecorderChanged;
            LibraryIndex.Changed -= Refresh;
            Ffmpeg.StateChanged -= FfmpegChanged;
        }
    }

    /// <summary>Recordings get their thumbnails once FFmpeg is installed.</summary>
    private void FfmpegChanged()
    {
        if (!Ffmpeg.IsAvailable) return;
        foreach (var item in _items.Where(i => i.IsVideo && i.Thumb == null)) item.EnsureThumb();
    }

    private void RecorderChanged()
    {
        if (!App.Instance.Recorder.IsRecording) Refresh(); // a recording was just saved (or discarded)
    }

    public async void Refresh()
    {
        if (!IsVisible && _items.Count > 0)
        {
            _stale = true;
            return;
        }
        _stale = false;
        int version = ++_refreshVersion;
        List<LibraryEntry> entries;
        try
        {
            entries = await LibraryIndex.ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading the gallery");
            return;
        }
        if (version != _refreshVersion) return;

        // Unchanged files keep their tile (thumbnail, selection, rename in progress); new or changed ones get a new one.
        var previous = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var items = new List<GalleryItem>();
        foreach (var entry in Limit > 0 ? entries.Take(Limit) : entries)
        {
            if (previous.TryGetValue(entry.Path, out var old) && ReferenceEquals(old.Entry, entry))
            {
                old.RefreshInfo();
                items.Add(old);
                continue;
            }
            items.Add(new GalleryItem(entry) { IsSelected = old?.IsSelected ?? false });
        }
        _anchor = _anchor == null ? null : items.FirstOrDefault(i => string.Equals(i.Path, _anchor.Path, StringComparison.OrdinalIgnoreCase));

        var known = previous.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool same = items.Count == _items.Count && items.Zip(_items).All(p => ReferenceEquals(p.First, p.Second));
        _items = items;
        if (!same)
        {
            RecentList.ItemsSource = items;
            AnimateNewTiles(items, known);
        }
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUi();
    }

    /// <summary>Tiles that weren't on screen before cascade in; existing ones stay put.</summary>
    private void AnimateNewTiles(List<GalleryItem> items, HashSet<string> known)
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

    private void Tile_Loaded(object sender, RoutedEventArgs e) => ItemOf(sender)?.EnsureThumb();

    // ---------- Selection ----------

    private void UpdateSelectionUi()
    {
        int count = _items.Count(i => i.IsSelected);
        Fx.Reveal(SelectionBar, count > 0);
        Fx.Reveal(RecentHint, count == 0, offsetY: -4);
        SelectionText.Text = $"{count} selected";
        RecentList.Tag = count > 0; // shows every tile's select circle while in selection mode
    }

    private void Toggle(GalleryItem item)
    {
        item.IsSelected = !item.IsSelected;
        _anchor = item;
        UpdateSelectionUi();
    }

    /// <summary>Shift+click: selects everything between the last clicked tile and this one.</summary>
    private void SelectRange(GalleryItem item)
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

    public void SelectAll()
    {
        foreach (var item in _items) item.IsSelected = true;
        UpdateSelectionUi();
    }

    public void ClearSelection()
    {
        foreach (var item in _items) item.IsSelected = false;
        _anchor = null;
        UpdateSelectionUi();
    }

    private static GalleryItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as GalleryItem;

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift)) SelectRange(item);
        else if (mods.HasFlag(ModifierKeys.Control)) Toggle(item);
        else if (AnySelected) Toggle(item); // in selection mode, clicks keep selecting
        else Open(item);
    }

    private static void Open(GalleryItem item)
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

    /// <summary>Gallery shortcuts; returns true when the key was used. Call it from the window's PreviewKeyDown.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.A && _items.Count > 0)
        {
            SelectAll();
            return true;
        }
        if (!AnySelected) return false;
        switch (e.Key)
        {
            case Key.Escape when mods == ModifierKeys.None:
                ClearSelection();
                return true;
            case Key.Delete when mods is ModifierKeys.None or ModifierKeys.Shift:
                DeleteItems(SelectedItems);
                return true;
            case Key.C when mods == ModifierKeys.Control:
                CopyItems(SelectedItems);
                return true;
            case Key.E when mods == ModifierKeys.Control:
                OutputService.ShowInFolder(SelectedPaths);
                return true;
            case Key.Enter when mods == ModifierKeys.None && SelectedItems is [var only]:
                Open(only);
                return true;
            case Key.F2 when mods == ModifierKeys.None && SelectedItems is [var renaming]:
                StartRename(renaming);
                return true;
        }
        return false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void SelectionClear_Click(object sender, RoutedEventArgs e) => ClearSelection();
    private void SelectionCopy_Click(object sender, RoutedEventArgs e) => CopyItems(SelectedItems);
    private void SelectionFolder_Click(object sender, RoutedEventArgs e) => OutputService.ShowInFolder(SelectedPaths);
    private void SelectionDelete_Click(object sender, RoutedEventArgs e) => DeleteItems(SelectedItems);

    // ---------- Actions ----------

    /// <summary>
    /// One screenshot copies as an image (paste into chats/docs); a recording, or several items, copy as files
    /// (paste into Explorer, Discord and so on).
    /// </summary>
    private static async void CopyItems(IReadOnlyList<GalleryItem> items)
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
            Log.Error(ex, "Copy from gallery");
        }
    }

    /// <summary>Moves to the Recycle Bin; with Shift held, asks and then deletes for good.</summary>
    private void DeleteItems(IReadOnlyList<GalleryItem> items)
    {
        if (items.Count == 0) return;
        bool permanent = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (permanent && !LuminaDialog.ConfirmPermanentDelete(Owner, items is [var only] ? only.Name : $"{items.Count} items"))
            return;
        foreach (var item in items)
        {
            if (permanent) OutputService.DeletePermanently(item.Path);
            else OutputService.Recycle(item.Path);
            item.IsSelected = false;
        }
        UpdateSelectionUi();
        Refresh();
    }

    // ---------- Rename ----------

    private void StartRename(GalleryItem item)
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
    private void FinishRename(GalleryItem item, string? newName)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        string name = newName?.Trim() ?? "";
        if (name.Length == 0 || name == Path.GetFileNameWithoutExtension(item.Path)) return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            LuminaDialog.Alert(Owner, "That name won't work", "File names can't contain \\ / : * ? \" < > |", DialogKind.Warning);
            return;
        }
        bool open = Application.Current.Windows.OfType<EditorWindow>().Any(w => SamePath(w.FilePath, item.Path))
                    || Application.Current.Windows.OfType<VideoEditorWindow>().Any(w => SamePath(w.FilePath, item.Path));
        if (open)
        {
            LuminaDialog.Alert(Owner, $"{item.Name} is open in an editor", "Close it there first, then rename it.", DialogKind.Warning);
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(item.Path)!, name + Path.GetExtension(item.Path));
        if (File.Exists(target) && !SamePath(target, item.Path))
        {
            LuminaDialog.Alert(Owner, "That name is taken", $"There's already a {Path.GetFileName(target)} in this folder.", DialogKind.Warning);
            return;
        }
        try
        {
            File.Move(item.Path, target);
            ProjectStore.Move(item.Path, target);
            LibraryIndex.Move(item.Path, target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Rename");
            LuminaDialog.Alert(Owner, "Couldn't rename it", ex.Message);
            return;
        }
        Refresh();
    }

    public static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ---------- Context menu ----------

    private void Tile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement tile || ItemOf(sender) is not { } item || item.IsRenaming) return;
        e.Handled = true;

        // Like Explorer: right-clicking outside the selection switches the selection to that tile.
        if (!item.IsSelected && AnySelected)
        {
            ClearSelection();
            Toggle(item);
        }
        var targets = item.IsSelected ? SelectedItems : new List<GalleryItem> { item };

        var menu = new ContextMenu { PlacementTarget = tile, Placement = PlacementMode.MousePoint };
        if (targets is [var one])
        {
            menu.Items.Add(one.IsVideo
                ? Menus.Entry("Play and trim", "IconPlay", () => Open(one), "Enter")
                : Menus.Entry("Edit", "IconPen", () => Open(one), "Enter"));
            menu.Items.Add(Menus.Entry(one.IsVideo ? "Copy video" : "Copy image", "IconCopy", () => CopyItems(targets), "Ctrl+C"));
            menu.Items.Add(Menus.Entry("Rename", "IconText", () => StartRename(one), "F2"));
            menu.Items.Add(Menus.Entry("Show in folder", "IconFolder", () => OutputService.ShowInFolder(one.Path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Entry(one.IsSelected ? "Deselect" : "Select", IconSelectCircle, () => Toggle(one)));
        }
        else
        {
            menu.Items.Add(Menus.Entry($"Copy {targets.Count} files", "IconCopy", () => CopyItems(targets), "Ctrl+C"));
            menu.Items.Add(Menus.Entry("Show in folder", "IconFolder", () => OutputService.ShowInFolder(targets.Select(i => i.Path).ToList())));
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Entry("Select all", IconSelectCircle, SelectAll, "Ctrl+A"));
            menu.Items.Add(Menus.Entry("Clear selection", "IconClose", ClearSelection, "Esc"));
        }
        menu.Items.Add(new Separator());
        var delete = Menus.Entry(targets.Count == 1 ? "Delete" : $"Delete {targets.Count} items", "IconTrash", () => DeleteItems(targets), "Del");
        delete.ToolTip = "Hold Shift to delete permanently";
        delete.Style = (Style)FindResource("DangerMenuItem");
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
