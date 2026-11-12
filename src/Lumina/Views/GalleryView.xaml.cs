using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumina.Editor;
using Lumina.Recording;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>A row of tiles in the gallery's virtualized list.</summary>
public sealed class GalleryTileRow(List<GalleryItem> items)
{
    public List<GalleryItem> Items { get; } = items;
}

/// <summary>A date heading ("Today", "October") above a group of rows.</summary>
public sealed class GalleryHeaderRow(string title, int count, bool first)
{
    public string Title { get; } = title;
    public string CountText { get; } = count.ToString("N0", CultureInfo.CurrentCulture);
    public Thickness Margin { get; } = new(2, first ? 2 : 18, 0, 10);
}

/// <summary>
/// Screenshots and recordings as tiles, with search, filters, sorting, date groups, keyboard navigation,
/// multi-select, copy, rename, reveal and delete. Keeps itself up to date from <see cref="LibraryIndex"/>;
/// the host window forwards keys to <see cref="HandleKey"/>.
/// </summary>
public partial class GalleryView : UserControl
{
    private static readonly Geometry IconSelectCircle = Frozen(Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M8,12.5 L11,15.5 L16.5,9.5"));
    /// <summary>A tile's width plus the gap after it.</summary>
    private const double TileStride = 214;

    public static readonly DependencyProperty IsSelectingProperty = DependencyProperty.Register(
        nameof(IsSelecting), typeof(bool), typeof(GalleryView));

    private int _refreshVersion;
    private bool _subscribed, _stale, _loadedOnce;
    private List<LibraryEntry> _entries = new();
    /// <summary>Tiles by path, kept across refreshes so unchanged files keep their thumbnail and state.</summary>
    private Dictionary<string, GalleryItem> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private List<GalleryItem> _items = new();
    private List<object> _rows = new();
    private Dictionary<GalleryItem, (int Row, int Col)> _positions = new();
    private int _columns = 4;
    /// <summary>Last clicked or toggled tile, where Shift ranges start.</summary>
    private GalleryItem? _anchor;
    /// <summary>The keyboard cursor; only drawn (IsCurrent) once the arrow keys are used.</summary>
    private GalleryItem? _current;
    /// <summary>After deleting with the keyboard, the cursor lands on whatever took this position.</summary>
    private int? _cursorAfterRefresh;
    private string _query = "";
    private GalleryFilter _filter;
    private GallerySort _sort;
    private bool _groupByDate;

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
        RecentList.SizeChanged += (_, _) => UpdateColumns();
        RecentList.PreviewMouseWheel += (_, _) => StopScrolling();
        RecentList.PreviewMouseLeftButtonDown += (_, _) => StopScrolling();
        Unloaded += (_, _) => StopScrolling();
    }

    // ---------- Options ----------

    /// <summary>Show at most this many files; 0 shows everything.</summary>
    public int Limit { get; set; }

    /// <summary>Headings by day, week and month (only when sorted by date).</summary>
    public bool GroupByDate
    {
        get => _groupByDate;
        set
        {
            _groupByDate = value;
            if (_loadedOnce) BuildRows(null);
        }
    }

    /// <summary>Words to look for in file names, source apps and markup text; every word has to match.</summary>
    public string Query
    {
        get => _query;
        set
        {
            value = value?.Trim() ?? "";
            if (value == _query) return;
            _query = value;
            ApplyView(scrollToTop: true);
        }
    }

    public GalleryFilter Filter
    {
        get => _filter;
        set
        {
            if (value == _filter) return;
            _filter = value;
            ApplyView(scrollToTop: true);
        }
    }

    public GallerySort Sort
    {
        get => _sort;
        set
        {
            if (value == _sort) return;
            _sort = value;
            ApplyView(scrollToTop: true);
        }
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>Shows a "View all" link beside the title.</summary>
    public bool ShowViewAll { get; set; }

    /// <summary>The hint under "Your captures show up here".</summary>
    public string EmptyHint { get; set; } = "Use your capture shortcut, or pick a mode above.";

    /// <summary>Anything is selected; every tile shows its select circle.</summary>
    public bool IsSelecting
    {
        get => (bool)GetValue(IsSelectingProperty);
        private set => SetValue(IsSelectingProperty, value);
    }

    /// <summary>How many files are shown, out of how many in total. Raised whenever the shown set changes.</summary>
    public event Action<int, int>? ViewChanged;

    /// <summary>The "View all" link was clicked.</summary>
    public event Action? ViewAllRequested;

    /// <summary>"Clear search and filters" was clicked on the no-matches screen.</summary>
    public event Action? ClearFiltersRequested;

    public int ShownCount => _items.Count;
    public int TotalCount => _entries.Count;
    public bool AnySelected => _items.Any(i => i.IsSelected);
    public IReadOnlyList<string> SelectedPaths => SelectedItems.Select(i => i.Path).ToList();
    private List<GalleryItem> SelectedItems => _items.Where(i => i.IsSelected).ToList();
    private Window? Owner => Window.GetWindow(this);

    /// <summary>What keyboard actions apply to: the selection, or else the tile under the keyboard cursor.</summary>
    private List<GalleryItem> Targets =>
        AnySelected ? SelectedItems : _current is { IsCurrent: true } current ? new List<GalleryItem> { current } : new List<GalleryItem>();

    private void ViewAll_Click(object sender, RoutedEventArgs e) => ViewAllRequested?.Invoke();
    private void ClearFilters_Click(object sender, RoutedEventArgs e) => ClearFiltersRequested?.Invoke();

    // ---------- Loading ----------

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

    /// <summary>Re-reads the folders (only changed files are looked at) and updates the tiles.</summary>
    public async void Refresh()
    {
        if (!IsVisible && _loadedOnce)
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
        _entries = entries;
        ApplyView(scrollToTop: false);
        _loadedOnce = true;
    }

    /// <summary>Filters, sorts and lays out the tiles from the last scan, without touching the disk.</summary>
    private void ApplyView(bool scrollToTop)
    {
        IEnumerable<LibraryEntry> shown = _entries;
        shown = _filter switch
        {
            GalleryFilter.Screenshots => shown.Where(e => !e.IsVideo),
            GalleryFilter.Recordings => shown.Where(e => e.IsVideo),
            GalleryFilter.Edited => shown.Where(e => e.IsEdited),
            _ => shown,
        };
        var words = _query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0) shown = shown.Where(e => words.All(w => Matches(e, w)));
        shown = _sort switch
        {
            GallerySort.Oldest => shown.OrderBy(e => e.ModifiedUtc),
            GallerySort.Name => shown.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            GallerySort.Largest => shown.OrderByDescending(e => e.Size),
            _ => shown.OrderByDescending(e => e.ModifiedUtc),
        };
        if (Limit > 0) shown = shown.Take(Limit);

        // Unchanged files keep their tile (thumbnail, selection, rename in progress); new or changed ones get a new one.
        bool firstLoad = !_loadedOnce;
        var current = new HashSet<LibraryEntry>(_entries);
        var tiles = new Dictionary<string, GalleryItem>(StringComparer.OrdinalIgnoreCase);
        var items = new List<GalleryItem>();
        int entering = 0;
        foreach (var entry in shown)
        {
            _tiles.TryGetValue(entry.Path, out var old);
            GalleryItem item;
            if (old != null && ReferenceEquals(old.Entry, entry))
            {
                old.RefreshInfo();
                item = old;
            }
            else
            {
                item = new GalleryItem(entry) { IsSelected = old?.IsSelected ?? false };
                // New files cascade in; on the first load only the first screenful does.
                if (entering < 24 && (firstLoad || old == null))
                    item.PendingEntrance = (firstLoad ? 180 + Math.Min(entering, 12) * 35 : entering * 35, firstLoad ? 1 : 0.92);
                entering++;
            }
            tiles[entry.Path] = item;
            items.Add(item);
        }
        // Tiles filtered out of view keep their thumbnail for when they come back, but not their selection.
        foreach (var (path, old) in _tiles)
        {
            if (tiles.ContainsKey(path) || !current.Contains(old.Entry)) continue;
            old.IsSelected = old.IsCurrent = old.IsRenaming = false;
            tiles[path] = old;
        }
        _tiles = tiles;

        var visible = items.ToHashSet();
        if (_anchor != null && !visible.Contains(_anchor)) _anchor = null;
        if (_current != null && !visible.Contains(_current)) _current = null;
        if (_cursorAfterRefresh is int index && items.Count > 0)
        {
            SetCurrent(items[Math.Min(index, items.Count - 1)], scroll: false);
            _cursorAfterRefresh = null;
        }

        bool same = items.Count == _items.Count && items.Zip(_items).All(p => ReferenceEquals(p.First, p.Second));
        _items = items;
        // Rebuild when the tiles change, or when grouping turns on or off without changing the order
        // (files named by date sort the same by name as by date).
        if (!same || _rows.Count == 0 || _rowsGrouped != IsGrouped) BuildRows(scrollToTop ? null : FirstVisibleItem());
        if (scrollToTop) Scroller?.ScrollToTop();

        UpdateEmptyState();
        UpdateSelectionUi();
        ViewAllButton.Visibility = ShowViewAll && _entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewAllText.Text = Limit > 0 && _entries.Count > Limit ? $"View all {_entries.Count:N0}" : "View all";
        ViewChanged?.Invoke(_items.Count, _entries.Count);
    }

    private static bool Matches(LibraryEntry e, string word) =>
        e.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)
        || (e.App?.Contains(word, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (e.MarkupText?.Contains(word, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private void UpdateEmptyState()
    {
        bool none = _entries.Count == 0, noMatches = !none && _items.Count == 0;
        EmptyText.Visibility = none || noMatches ? Visibility.Visible : Visibility.Collapsed;
        if (noMatches)
        {
            EmptyTitle.Text = _query.Length > 0 ? $"Nothing matches “{_query}”" : "Nothing here yet";
            EmptyHintText.Text = _query.Length > 0
                ? "Search looks at file names, the app something was captured from, and text in your markup."
                : "Nothing fits this filter yet.";
        }
        else
        {
            EmptyTitle.Text = "Your captures show up here";
            EmptyHintText.Text = EmptyHint;
        }
        EmptyIcon.Data = (Geometry)FindResource(noMatches ? "IconSearch" : "IconRegion");
        ClearFiltersButton.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Layout ----------

    private ScrollViewer? Scroller => RecentList.Template?.FindName("Scroller", RecentList) as ScrollViewer;

    /// <summary>Re-flows the rows when the list gets wide enough for one more (or one fewer) column.</summary>
    private void UpdateColumns()
    {
        double width = Scroller is { ViewportWidth: > 0 } s ? s.ViewportWidth : RecentList.ActualWidth;
        int columns = Math.Max(1, (int)((width + 14) / TileStride));
        if (columns == _columns) return;
        _columns = columns;
        if (_items.Count > 0) BuildRows(FirstVisibleItem());
    }

    private static string GroupOf(DateTime time, DateTime now)
    {
        var day = time.Date;
        if (day == now.Date) return "Today";
        if (day == now.Date.AddDays(-1)) return "Yesterday";
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek - (int)firstDay + 7) % 7));
        if (day >= weekStart && day < now.Date) return "Earlier this week";
        if (day.Year == now.Year && day.Month == now.Month && day < now.Date) return "Earlier this month";
        return day.Year == now.Year ? time.ToString("MMMM", CultureInfo.CurrentCulture) : time.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>Date headings only make sense when the list is in date order.</summary>
    private bool IsGrouped => _groupByDate && _sort is GallerySort.Newest or GallerySort.Oldest;
    private bool _rowsGrouped;

    /// <summary>Chunks the tiles into rows (under date headings when grouping), keeping <paramref name="keepInView"/> on screen.</summary>
    private void BuildRows(GalleryItem? keepInView)
    {
        var rows = new List<object>();
        var positions = new Dictionary<GalleryItem, (int, int)>();
        void AddTiles(List<GalleryItem> group)
        {
            for (int i = 0; i < group.Count; i += _columns)
            {
                var chunk = group.GetRange(i, Math.Min(_columns, group.Count - i));
                for (int c = 0; c < chunk.Count; c++) positions[chunk[c]] = (rows.Count, c);
                rows.Add(new GalleryTileRow(chunk));
            }
        }

        _rowsGrouped = IsGrouped;
        if (_rowsGrouped)
        {
            var now = DateTime.Now;
            string? label = null;
            var group = new List<GalleryItem>();
            void Flush()
            {
                if (group.Count == 0) return;
                rows.Add(new GalleryHeaderRow(label!, group.Count, rows.Count == 0));
                AddTiles(group);
                group = new List<GalleryItem>();
            }
            foreach (var item in _items)
            {
                string itemLabel = GroupOf(item.Entry.Modified, now);
                if (itemLabel != label) Flush();
                label = itemLabel;
                group.Add(item);
            }
            Flush();
        }
        else
        {
            AddTiles(_items);
        }
        _rows = rows;
        _positions = positions;
        RecentList.ItemsSource = rows;
        if (keepInView != null) ScrollToItem(keepInView, alignTop: true);
    }

    /// <summary>The first tile in the topmost row on screen, or null when scrolled to the top.</summary>
    private GalleryItem? FirstVisibleItem()
    {
        if (Scroller is not { } scroller || _rows.Count == 0 || scroller.VerticalOffset < 1) return null;
        for (double y = 4; y < Math.Min(scroller.ViewportHeight, 240); y += 24)
        {
            var hit = RecentList.InputHitTest(new Point(30, y)) as DependencyObject;
            for (var d = hit; d != null && d != RecentList; d = VisualTreeHelper.GetParent(d))
            {
                if (d is FrameworkElement { DataContext: GalleryItem item }) return item;
                if (d is FrameworkElement { DataContext: GalleryTileRow row }) return row.Items[0];
            }
        }
        return null;
    }

    /// <summary>
    /// Scrolls a tile's row into view: straight to the top of the list (after a re-flow), or smoothly by just
    /// enough to show it (keyboard moves).
    /// </summary>
    private void ScrollToItem(GalleryItem item, bool alignTop = false)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_positions.TryGetValue(item, out var pos) || Scroller is not { } scroller || FindPanel(RecentList) is not { } panel) return;
            if (RecentList.ItemContainerGenerator.ContainerFromIndex(pos.Row) is not FrameworkElement)
            {
                StopScrolling();
                panel.BringIndexIntoViewPublic(pos.Row); // too far away to glide to; jump there first
                RecentList.UpdateLayout();
            }
            if (RecentList.ItemContainerGenerator.ContainerFromIndex(pos.Row) is not FrameworkElement row) return;
            // Measure from where a glide in progress is heading, not where it is right now.
            double offset = _scrolling ? _scrollTo() : scroller.VerticalOffset;
            double top = row.TransformToAncestor(scroller).Transform(new Point()).Y - (offset - scroller.VerticalOffset);
            if (alignTop)
            {
                StopScrolling();
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + top);
            }
            else if (top < 0) SmoothScrollTo(offset + top - 8);
            else if (top + row.ActualHeight > scroller.ViewportHeight) SmoothScrollTo(offset + top + row.ActualHeight - scroller.ViewportHeight + 8);
        }, DispatcherPriority.Loaded);
    }

    // ---------- Smooth scrolling ----------

    private bool _scrolling;
    private double _scrollFrom;
    private Func<double> _scrollTo = () => 0;
    private TimeSpan _scrollDuration;
    private DateTime _scrollStart;
    /// <summary>Tiles that came into view mid-glide; their thumbnails load once it settles.</summary>
    private readonly HashSet<GalleryItem> _deferredThumbs = new();

    private void SmoothScrollTo(double offset) => SmoothScrollTo(() => offset);

    /// <summary>
    /// Glides to an offset, easing out. The target is re-read every frame, so "the bottom" stays right while
    /// the virtualized list firms up its estimated height.
    /// </summary>
    private void SmoothScrollTo(Func<double> target)
    {
        if (Scroller is not { } scroller) return;
        _scrollFrom = scroller.VerticalOffset;
        _scrollTo = () => Math.Clamp(target(), 0, scroller.ScrollableHeight);
        double distance = Math.Abs(_scrollTo() - _scrollFrom);
        if (distance < 1) return;
        _scrollDuration = TimeSpan.FromMilliseconds(Math.Clamp(160 + distance / 25, 160, 480));
        _scrollStart = DateTime.UtcNow;
        if (_scrolling) return;
        _scrolling = true;
        CompositionTarget.Rendering += OnScrollFrame;
    }

    private void OnScrollFrame(object? sender, EventArgs e)
    {
        if (Scroller is not { } scroller)
        {
            StopScrolling();
            return;
        }
        double t = Math.Clamp((DateTime.UtcNow - _scrollStart) / _scrollDuration, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        scroller.ScrollToVerticalOffset(_scrollFrom + (_scrollTo() - _scrollFrom) * eased);
        if (t >= 1) StopScrolling();
    }

    private void StopScrolling()
    {
        if (!_scrolling) return;
        _scrolling = false;
        CompositionTarget.Rendering -= OnScrollFrame;
        foreach (var item in _deferredThumbs) item.EnsureThumb();
        _deferredThumbs.Clear();
    }

    private static VirtualizingStackPanel? FindPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is VirtualizingStackPanel panel) return panel;
            if (FindPanel(child) is { } found) return found;
        }
        return null;
    }

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (_scrolling) _deferredThumbs.Add(item);
        else item.EnsureThumb();
        if (item.PendingEntrance is { } entrance && sender is UIElement tile)
        {
            item.PendingEntrance = null;
            Fx.PlayEntrance(tile, entrance.Delay, offsetY: 16, fromScale: entrance.Scale);
        }
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _deferredThumbs.Remove(item); // flew past; no thumbnail needed
    }

    // ---------- Selection ----------

    private void UpdateSelectionUi()
    {
        int count = _items.Count(i => i.IsSelected);
        Fx.Reveal(SelectionBar, count > 0);
        Fx.Reveal(RecentHint, count == 0, offsetY: -4);
        SelectionText.Text = $"{count:N0} selected";
        IsSelecting = count > 0;
    }

    private void Toggle(GalleryItem item)
    {
        item.IsSelected = !item.IsSelected;
        _anchor = item;
        UpdateSelectionUi();
    }

    /// <summary>Shift+click or Shift+arrow: selects everything between the anchor and this tile.</summary>
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
        HideCursor();
        _current = item;
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
        HideCursor();
        _current = item;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SelectRange(item);
        else Toggle(item);
    }

    // ---------- Keyboard ----------

    private void HideCursor()
    {
        if (_current != null) _current.IsCurrent = false;
    }

    private void SetCurrent(GalleryItem item, bool scroll = true)
    {
        HideCursor();
        _current = item;
        item.IsCurrent = true;
        if (scroll) ScrollToItem(item);
    }

    /// <summary>Puts the keyboard cursor on the first tile (for moving down out of a search box).</summary>
    public void FocusFirst()
    {
        Focus();
        if (_items.Count > 0) SetCurrent(_current is { } c && _items.Contains(c) ? c : _items[0]);
    }

    /// <summary>The tile an arrow key leads to from <paramref name="from"/>, or null at the edge.</summary>
    private GalleryItem? Neighbour(GalleryItem from, Key key)
    {
        int index = _items.IndexOf(from);
        switch (key)
        {
            case Key.Left: return index > 0 ? _items[index - 1] : null;
            case Key.Right: return index < _items.Count - 1 ? _items[index + 1] : null;
            case Key.Home: return _items[0];
            case Key.End: return _items[^1];
        }
        if (!_positions.TryGetValue(from, out var pos)) return null;
        int step = key is Key.Up or Key.PageUp ? -1 : 1;
        // Page keys move a screenful of rows, Up and Down one. Both skip date headings and keep the column,
        // as far as the row they land on is long.
        int rowsLeft = key is Key.PageUp or Key.PageDown ? RowsPerPage() : 1;
        GalleryTileRow? landed = null;
        for (int r = pos.Row + step; r >= 0 && r < _rows.Count && rowsLeft > 0; r += step)
        {
            if (_rows[r] is not GalleryTileRow row) continue;
            landed = row;
            rowsLeft--;
        }
        return landed?.Items[Math.Min(pos.Col, landed.Items.Count - 1)];
    }

    /// <summary>The height of a realized row of the same kind as <paramref name="row"/>, or a close guess.</summary>
    private double HeightOf(object row)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].GetType() != row.GetType()) continue;
            if (RecentList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement { ActualHeight: > 0 } container)
                return container.ActualHeight;
        }
        return row is GalleryTileRow ? 184 : 44;
    }

    private int RowsPerPage()
    {
        double tile = _rows.OfType<GalleryTileRow>().FirstOrDefault() is { } any ? HeightOf(any) : 184;
        return Math.Max(1, (int)((Scroller?.ViewportHeight ?? tile) / tile));
    }

    /// <summary>How far the list moves between two rows, so a page key keeps the cursor at the same spot on screen.</summary>
    private double DistanceBetween(int fromRow, int toRow)
    {
        double tile = 0, header = 0, total = 0;
        for (int r = Math.Min(fromRow, toRow); r < Math.Max(fromRow, toRow); r++)
        {
            if (_rows[r] is GalleryTileRow) total += tile > 0 ? tile : tile = HeightOf(_rows[r]);
            else total += header > 0 ? header : header = HeightOf(_rows[r]);
        }
        return toRow >= fromRow ? total : -total;
    }

    /// <summary>Gallery shortcuts; returns true when the key was used. Call it from the window's PreviewKeyDown.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_items.Count == 0) return false;

        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            && mods is ModifierKeys.None or ModifierKeys.Shift)
        {
            var from = _current is { } c && _items.Contains(c) ? c : null;
            bool jump = key is Key.Home or Key.End or Key.PageUp or Key.PageDown;
            // The first arrow press only shows the cursor (where it was, or on the first tile). Home, End and the
            // page keys act straight away, paging from the first tile on screen.
            if (jump && (from == null || !from.IsCurrent))
                from = key is Key.PageUp or Key.PageDown ? FirstVisibleItem() ?? _items[0] : from ?? _items[0];
            var to = from == null ? _items[0] : !from.IsCurrent && !jump ? from : Neighbour(from, key) ?? (jump ? from : null);
            if (to == null) return true;
            if (mods == ModifierKeys.Shift)
            {
                _anchor ??= from ?? to;
                foreach (var item in _items) item.IsSelected = false;
                SelectRange(to);
            }
            SetCurrent(to, scroll: !jump);
            if (key == Key.Home) SmoothScrollTo(() => 0);
            else if (key == Key.End) SmoothScrollTo(() => Scroller?.ScrollableHeight ?? 0);
            else if (jump && from != null && _positions.TryGetValue(from, out var a) && _positions.TryGetValue(to, out var b))
            {
                double offset = _scrolling ? _scrollTo() : Scroller?.VerticalOffset ?? 0;
                // On the first or last row there's no further row to page to; glide to that end instead.
                if (a.Row == b.Row) SmoothScrollTo(key == Key.PageUp ? () => 0 : () => Scroller?.ScrollableHeight ?? 0);
                else SmoothScrollTo(offset + DistanceBetween(a.Row, b.Row));
            }
            return true;
        }
        if (mods == ModifierKeys.Control && key == Key.A)
        {
            SelectAll();
            return true;
        }
        if (mods == ModifierKeys.None && key == Key.Space && _current is { IsCurrent: true } cursor)
        {
            Toggle(cursor);
            return true;
        }

        var targets = Targets;
        switch (key)
        {
            case Key.Escape when mods == ModifierKeys.None:
                if (AnySelected) ClearSelection();
                else if (_current is { IsCurrent: true }) HideCursor();
                else return false;
                return true;
            case Key.Delete when mods is ModifierKeys.None or ModifierKeys.Shift && targets.Count > 0:
                if (_current is { IsCurrent: true } && targets.Contains(_current))
                    _cursorAfterRefresh = _items.IndexOf(targets.OrderBy(_items.IndexOf).First());
                DeleteItems(targets);
                return true;
            case Key.C when mods == ModifierKeys.Control && targets.Count > 0:
                CopyItems(targets);
                return true;
            case Key.E when mods == ModifierKeys.Control && targets.Count > 0:
                OutputService.ShowInFolder(targets.Select(i => i.Path).ToList());
                return true;
            case Key.Enter when mods == ModifierKeys.None && targets is [var only]:
                Open(only);
                return true;
            case Key.F2 when mods == ModifierKeys.None && targets is [var renaming]:
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
        {
            _cursorAfterRefresh = null;
            return;
        }
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
        }, DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ItemOf(box) is not { } item) return;
        if (e.Key == Key.Enter) FinishRename(item, box.Text);
        else if (e.Key == Key.Escape) FinishRename(item, null);
        else return;
        e.Handled = true;
        Focus(); // back to the gallery, so the arrow keys keep working
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
            menu.Items.Add(Menus.Entry(one.IsSelected ? "Deselect" : "Select", IconSelectCircle, () => Toggle(one), "Space"));
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
