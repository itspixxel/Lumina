using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Lumina.Services;

namespace Lumina.Views;

/// <summary>One tile in the gallery: a library entry plus its thumbnail and selection state.</summary>
public sealed class GalleryItem : INotifyPropertyChanged
{
    private bool _isSelected, _isRenaming, _thumbRequested;
    private BitmapSource? _thumb;
    private string _info;

    public GalleryItem(LibraryEntry entry)
    {
        Entry = entry;
        _thumb = ThumbnailCache.Peek(entry);
        _info = FriendlyAge(entry.Modified);
    }

    public LibraryEntry Entry { get; }
    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public bool IsVideo => Entry.IsVideo;

    /// <summary>How long ago it was saved.</summary>
    public string Info
    {
        get => _info;
        private set => Set(ref _info, value);
    }

    /// <summary>The name plus pixel size, file size and source app, for the tile's tooltip.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Width > 0) parts.Add($"{Entry.Width} × {Entry.Height}");
            parts.Add(FriendlySize(Entry.Size));
            if (!string.IsNullOrEmpty(Entry.App)) parts.Add(Entry.App!);
            return $"{Name}\n{string.Join("  ·  ", parts)}\n{Entry.Modified:d MMM yyyy, HH:mm}";
        }
    }

    public BitmapSource? Thumb
    {
        get => _thumb;
        private set => Set(ref _thumb, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>The tile's name is an edit box.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    /// <summary>Starts loading the thumbnail; called when the tile is first shown.</summary>
    public async void EnsureThumb()
    {
        if (_thumb != null || _thumbRequested) return;
        _thumbRequested = true;
        var thumb = await ThumbnailCache.GetAsync(Entry);
        if (thumb != null) Thumb = thumb;
        else _thumbRequested = false; // try again next time (FFmpeg may get installed, a file may finish writing)
    }

    public void RefreshInfo() => Info = FriendlyAge(Entry.Modified);

    public static string FriendlyAge(DateTime time)
    {
        var age = DateTime.Now - time;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 7) return time.ToString("dddd, HH:mm");
        return time.ToString("d MMM yyyy");
    }

    public static string FriendlySize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
