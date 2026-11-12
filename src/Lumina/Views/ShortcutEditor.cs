using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lumina.Capture;
using CaptureMode = Lumina.Capture.CaptureMode;
using Lumina.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Lumina.Views;

/// <summary>Draws a key combination as a row of keycaps.</summary>
public static class KeycapRow
{
    public static void Fill(Panel panel, IEnumerable<string> parts)
    {
        panel.Children.Clear();
        foreach (var part in parts) panel.Children.Add(Cap(part));
    }

    public static Border Cap(string text) => new()
    {
        Style = (Style)Application.Current.Resources["Keycap"],
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold },
    };
}

/// <summary>
/// The list of global capture shortcuts in Settings. Click a shortcut, then press the new combination;
/// the keyboard hook does the listening so combinations Windows normally keeps (like Win + Shift + S) work too.
/// Changes apply immediately.
/// </summary>
public sealed class ShortcutEditor : StackPanel
{
    private static readonly (CaptureMode Mode, string Name)[] Modes =
        { (CaptureMode.Region, "Region"), (CaptureMode.Window, "Window"), (CaptureMode.Screen, "Full screen"), (CaptureMode.Freeform, "Freeform"), (CaptureMode.Record, "Record screen") };

    private readonly StackPanel _rows = new();
    private readonly TextBlock _message;
    private HotkeyBinding? _recording;
    private StackPanel? _recordingKeys;

    public ShortcutEditor()
    {
        Children.Add(_rows);

        var add = new Button
        {
            Style = (Style)Application.Current.Resources["GhostButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-10, 4, 0, 0),
            Content = new TextBlock { Text = "+  Add shortcut", Foreground = (Brush)Application.Current.Resources["AccentTextBrush"] },
        };
        add.Click += (_, _) =>
        {
            var binding = new HotkeyBinding();
            Bindings.Add(binding);
            Rebuild();
            StartRecording(binding);
        };
        Children.Add(add);

        _message = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        };
        Children.Add(_message);
        ShowHint();

        Loaded += (_, _) =>
        {
            Rebuild();
            if (Window.GetWindow(this) is { } window)
            {
                window.Deactivated += (_, _) => StopRecording();
                window.Closed += (_, _) => StopRecording();
            }
        };
    }

    private static List<HotkeyBinding> Bindings => App.Settings.Hotkeys ??= new();

    private void Rebuild()
    {
        _rows.Children.Clear();
        _recordingKeys = null;
        foreach (var binding in Bindings) _rows.Children.Add(Row(binding));
        if (Bindings.Count == 0)
            _rows.Children.Add(new TextBlock
            {
                Text = "No shortcuts. Lumina can still be opened from the tray icon.",
                Foreground = (Brush)Application.Current.Resources["Text2Brush"],
                Margin = new Thickness(0, 12, 0, 6),
            });
    }

    private FrameworkElement Row(HotkeyBinding binding)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        bool recording = binding == _recording;
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 0) };
        var recorder = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            Content = keys,
            ToolTip = "Click, then press the new shortcut",
        };
        if (recording)
        {
            recorder.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
            _recordingKeys = keys;
            ShowRecording(HotkeyModifiers.None);
        }
        else KeycapRow.Fill(keys, binding.Parts());
        recorder.Click += (_, _) =>
        {
            if (_recording == binding) StopRecording();
            else StartRecording(binding);
        };
        grid.Children.Add(recorder);

        var mode = new ComboBox { Width = 140, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "What this shortcut captures" };
        foreach (var (_, name) in Modes) mode.Items.Add(name);
        mode.SelectedIndex = Array.FindIndex(Modes, m => m.Mode == binding.Mode);
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0) return;
            binding.Mode = Modes[mode.SelectedIndex].Mode;
            Apply();
        };
        Grid.SetColumn(mode, 1);
        grid.Children.Add(mode);

        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Margin = new Thickness(4, 0, -6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove shortcut",
            Content = new ShapePath { Style = (Style)Application.Current.Resources["Icon"], Data = (Geometry)Application.Current.Resources["IconTrash"] },
        };
        remove.Click += (_, _) =>
        {
            if (_recording == binding) StopRecording(rebuild: false);
            Bindings.Remove(binding);
            Apply();
            Rebuild();
            ShowHint();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return grid;
    }

    private void StartRecording(HotkeyBinding binding)
    {
        if (App.Hook is not { } hook) return;
        StopRecording(rebuild: false);
        _recording = binding;
        hook.RecordProgress += OnRecordProgress;
        hook.Recording = true;
        Rebuild();
        ShowHint("Press the new shortcut, or Esc to cancel.");
    }

    private void StopRecording(bool rebuild = true)
    {
        if (_recording == null) return;
        if (App.Hook is { } hook)
        {
            hook.Recording = false;
            hook.RecordProgress -= OnRecordProgress;
        }
        // A new shortcut that never got keys is dropped.
        if (_recording.Key == 0) Bindings.Remove(_recording);
        _recording = null;
        if (rebuild)
        {
            Rebuild();
            ShowHint();
        }
    }

    private void ShowRecording(HotkeyModifiers mods)
    {
        if (_recordingKeys == null) return;
        KeycapRow.Fill(_recordingKeys, HotkeyBinding.Parts(mods, 0));
        _recordingKeys.Children.Add(new TextBlock
        {
            Text = mods == HotkeyModifiers.None ? "Press keys..." : "...",
            Foreground = (Brush)Application.Current.Resources["Text2Brush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        });
    }

    private void OnRecordProgress(HotkeyModifiers mods, int vk)
    {
        if (_recording is not { } binding) return;
        if (vk == 0)
        {
            ShowRecording(mods);
            return;
        }
        if (vk == KeyInterop.VirtualKeyFromKey(Key.Escape) && mods == HotkeyModifiers.None)
        {
            StopRecording();
            return;
        }

        var candidate = new HotkeyBinding { Modifiers = mods, Key = vk };
        bool weak = mods is HotkeyModifiers.None or HotkeyModifiers.Shift;
        if (weak && !HotkeyBinding.CanStandAlone(vk))
        {
            ShowRecording(HotkeyModifiers.None);
            ShowHint($"{candidate} would get in the way of typing. Add Ctrl, Alt or Win.", warn: true);
            return;
        }
        if (Bindings.FirstOrDefault(b => b != binding && b.SameKeys(candidate)) is { } taken)
        {
            ShowRecording(HotkeyModifiers.None);
            ShowHint($"{candidate} is already a shortcut for {Modes.First(m => m.Mode == taken.Mode).Name.ToLowerInvariant()}.", warn: true);
            return;
        }

        binding.Modifiers = mods;
        binding.Key = vk;
        StopRecording();
        Apply();
    }

    private void ShowHint(string? text = null, bool warn = false)
    {
        _message.Text = text ?? "Click a shortcut to change it. Win + Shift + S takes over from Snipping Tool while Lumina runs.";
        _message.Foreground = (Brush)Application.Current.Resources[warn ? "DangerBrush" : "Text2Brush"];
    }

    private static void Apply()
    {
        App.ApplyShortcuts();
        SettingsService.Save(App.Settings);
    }
}
