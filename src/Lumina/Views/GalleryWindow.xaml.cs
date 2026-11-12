using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>Every screenshot and recording, with search, filters, sorting and date groups.</summary>
public partial class GalleryWindow : ChromeWindow
{
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public GalleryWindow()
    {
        InitializeComponent();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Gallery.Query = SearchBox.Text;
        };
        Gallery.ViewChanged += UpdateTitle;
        Gallery.ClearFiltersRequested += ClearAll;

        Gallery.Sort = App.Settings.GallerySort;
        foreach (ComboBoxItem item in SortBox.Items)
            if (item.Tag is string tag && tag == App.Settings.GallerySort.ToString()) SortBox.SelectedItem = item;
        if (SortBox.SelectedItem == null) SortBox.SelectedIndex = 0;
    }

    /// <summary>Opens the gallery (or brings it forward), optionally ready to type a search.</summary>
    public static void ShowGallery(bool focusSearch = false)
    {
        var window = Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault() ?? new GalleryWindow();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (focusSearch) window.FocusSearch();
    }

    private void FocusSearch()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void UpdateTitle(int shown, int total)
    {
        bool narrowed = Gallery.Query.Length > 0 || Gallery.Filter != GalleryFilter.All;
        Gallery.Title = narrowed ? $"{shown:N0} of {total:N0}" : total == 1 ? "1 capture" : $"{total:N0} captures";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _searchDelay.Stop();
        if (empty) Gallery.Query = ""; // clearing is instant
        else _searchDelay.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ClearAll()
    {
        SearchBox.Clear();
        foreach (var child in LogicalTreeHelper.GetChildren(FilterChips))
            if (child is RadioButton { Tag: "All" } all) all.IsChecked = true;
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<GalleryFilter>(tag, out var filter) && Gallery != null)
            Gallery.Filter = filter;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<GallerySort>(tag, out var sort)) return;
        Gallery.Sort = sort;
        if (App.Settings.GallerySort == sort) return;
        App.Settings.GallerySort = sort;
        SettingsService.Save(App.Settings);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Menus.ShowOutputFolders((UIElement)sender);

    private void Settings_Click(object sender, RoutedEventArgs e) => App.ShowSettings();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool inSearch = SearchBox.IsKeyboardFocusWithin;

        if (mods == ModifierKeys.Control && e.Key == Key.F)
        {
            FocusSearch();
            e.Handled = true;
            return;
        }
        if (inSearch)
        {
            if (e.Key == Key.Escape)
            {
                // First Esc clears the search, the next one leaves the box.
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                else Gallery.Focus();
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Enter && mods == ModifierKeys.None)
            {
                if (_searchDelay.IsEnabled) // don't wait for the typing pause
                {
                    _searchDelay.Stop();
                    Gallery.Query = SearchBox.Text;
                }
                Gallery.FocusFirst();
                e.Handled = true;
            }
            return;
        }
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return; // renaming, or the sort list

        if (Gallery.HandleKey(e))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && mods == ModifierKeys.None && (SearchBox.Text.Length > 0 || Gallery.Filter != GalleryFilter.All))
        {
            ClearAll();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.E)
        {
            Menus.ShowOutputFolders(this);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            App.ShowSettings();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.None && e.Key == Key.F5)
        {
            Gallery.Refresh();
            e.Handled = true;
        }
        else if (mods is ModifierKeys.None or ModifierKeys.Shift && IsTypingKey(e.Key))
        {
            // Start typing anywhere to search; the key's character follows focus into the box.
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
    }

    private static bool IsTypingKey(Key key) =>
        key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9 or Key.OemMinus or Key.OemPeriod;
}
