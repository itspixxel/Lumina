using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumina.Services;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Lumina.Views;

/// <summary>
/// Settings, Appearance: pick a preset, then change any of its colors and its style (fonts, corners, lines, depth,
/// background pattern, accent gradient, gray tint). Every change shows in all windows at once.
/// </summary>
public sealed class AppearancePanel : StackPanel
{
    /// <summary>Height of a preset's picture; the selected ring is this plus 4 px on each side.</summary>
    public const double PreviewHeight = 92;
    public const double PreviewRingHeight = PreviewHeight + 8;

    /// <summary>Raised after any change, so the window can save.</summary>
    public event Action? Changed;

    private static ThemeSettings S => App.Settings.Appearance;

    private readonly Dictionary<string, RadioButton> _presetTiles = new();
    private readonly List<ColorRow> _colorRows = new();
    private readonly StackPanel _allColors = new() { Visibility = Visibility.Collapsed };
    private readonly Button _allToggle;
    private readonly TextBlock _allToggleText = new() { FontSize = 13 };
    private readonly WrapPanel _suggestions = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Dictionary<int, RadioButton> _corners = new();
    private readonly Dictionary<string, RadioButton> _fonts = new();
    private readonly Dictionary<int, RadioButton> _lines = new();
    private readonly Dictionary<string, RadioButton> _depths = new();
    private readonly Dictionary<string, RadioButton> _patterns = new();
    private readonly CheckBox _gradient = new();
    private readonly Border _gradientEndRow;
    private readonly Slider _tint = new() { Width = 190, Minimum = 0, Maximum = 100, IsSnapToTickEnabled = true, TickFrequency = 5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _tintText = new() { Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _readText;
    private readonly Border _readBadge = new() { Padding = new Thickness(10, 3, 11, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _readBadgeText = new() { FontSize = 12.5 };
    private readonly Button _resetAll;
    private readonly Popup _pickerPopup;
    private readonly ColorPicker _picker = new() { ShowEditorTools = false };
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private string? _pickingKey;
    private bool _syncing;

    private static readonly (string Label, int Radius)[] CornerChoices = [("Square", 0), ("Sharp", 6), ("Soft", 10), ("Round", 14)];

    public AppearancePanel()
    {
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            ApplyNow();
        };

        // ---------- Presets ----------
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, -16, -14) };
        foreach (var preset in Theme.Presets)
        {
            double r = PreviewRadius(preset.Corners);
            var tile = new RadioButton
            {
                Style = Res<Style>("PresetTile"),
                GroupName = "preset",
                Margin = new Thickness(0, 0, 16, 14),
                Content = PresetPreview(preset),
                Tag = new CornerRadius(r == 0 ? 0 : r + 4),
                ToolTip = preset.Id == "system" ? "Light or dark with Windows, in your Windows accent color" : null,
            };
            AutomationName(tile, $"{preset.Name} theme");
            tile.Checked += (_, _) => PickPreset(preset.Id);
            _presetTiles[preset.Id] = tile;
            tiles.Children.Add(tile);
        }
        Children.Add(Group("Theme", Card(tiles, new Thickness(18, 16, 18, 16))));

        // ---------- Colors ----------
        var colors = new StackPanel();
        foreach (var token in Theme.Tokens.Where(t => t.IsInput && t.Key != "Accent2"))
            colors.Children.Add(ColorRowFor(token, token.Key == "Accent" ? _suggestions : null).Row);
        foreach (var token in Theme.Tokens.Where(t => !t.IsInput))
            _allColors.Children.Add(ColorRowFor(token, null).Row);
        colors.Children.Add(_allColors);
        _allToggle = new Button
        {
            Style = Res<Style>("GhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-10, 8, 0, 8),
            Padding = new Thickness(10, 5, 10, 5),
            Content = _allToggleText,
        };
        _allToggleText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        _allToggle.Click += (_, _) =>
        {
            bool show = _allColors.Visibility != Visibility.Visible;
            _allColors.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SyncAllToggle();
            RowLines.Update(this);
        };
        colors.Children.Add(_allToggle);
        SyncAllToggle();
        Children.Add(Group("Colors", Card(colors)));

        // ---------- Style ----------
        var look = new StackPanel();

        look.Children.Add(Row(Choices(_fonts, "font", Theme.Fonts.Select(f => (f.Label, f.Id)), (chip, id) =>
            {
                // Each choice is written in its own title font, except pixel type, which breaks up this small.
                var font = Theme.Fonts.First(f => f.Id == id);
                if (!font.Pixel) chip.FontFamily = Theme.Family(font.Display);
            },
            id => S.Font = id == Theme.Find(S.Preset).Font ? null : id),
            "Font", "Geist, Serif and Pixel change titles; text stays in Inter."));
        look.Children.Add(Row(Choices(_corners, "corners", CornerChoices.Select(c => (c.Label, c.Radius)), null,
            radius => S.Corners = radius == Theme.Find(S.Preset).Corners ? null : radius),
            "Corners", "Cards, buttons, menus and the windows themselves."));
        look.Children.Add(Row(Choices(_lines, "lines", [("Thin", 1), ("Thick", 2)], null,
            weight => S.Lines = weight == Theme.Find(S.Preset).Lines ? null : weight),
            "Lines", "How heavy the borders of cards, buttons and boxes are."));
        look.Children.Add(Row(Choices(_depths, "depth", Theme.Depths.Select(d => (d.Label, d.Id)), null,
            id => S.Depth = id == Theme.Find(S.Preset).Depth ? null : id),
            "Depth", "No shadows, soft ones, a glow on accent buttons, or hard edges."));
        look.Children.Add(Row(Choices(_patterns, "pattern", Theme.Patterns.Select(x => (x.Label, x.Id)), null,
            id => S.Pattern = id == Theme.Find(S.Preset).Pattern ? null : id),
            "Background", "A faint pattern behind each window's content."));

        _gradient.Content = RowText("Accent gradient", "Blend the accent into a second color on buttons and switches.");
        _gradient.Click += (_, _) =>
        {
            bool on = _gradient.IsChecked == true;
            S.Gradient = on == Theme.Find(S.Preset).Gradient ? null : on;
            ApplyNow();
        };
        look.Children.Add(Row(_gradient));
        _gradientEndRow = ColorRowFor(Theme.Tokens.First(t => t.Key == "Accent2"), null).Row;
        look.Children.Add(_gradientEndRow);

        _tintText.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        _tint.ValueChanged += (_, e) =>
        {
            _tintText.Text = $"{(int)e.NewValue}%";
            if (_syncing) return;
            double tint = e.NewValue / 100;
            S.Tint = Math.Abs(tint - Theme.Find(S.Preset).Tint) < 0.001 ? null : tint;
            ApplySoon();
        };
        var tintBox = new StackPanel { Orientation = Orientation.Horizontal, Width = 240, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tintBox.Children.Add(_tint);
        tintBox.Children.Add(_tintText);
        look.Children.Add(Row(tintBox, "Gray tint", "How much the accent colors the grays. Midnight's violet grays are 30%."));

        _readBadge.Child = _readBadgeText;
        _readBadge.SetResourceReference(Border.CornerRadiusProperty, "RadiusItem");
        var readRow = Row(_readBadge, "Readability", " ");
        _readText = FindHint(readRow);
        look.Children.Add(readRow);
        Children.Add(Group("Style", Card(look)));

        // ---------- Reset, export, import ----------
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        _resetAll = new Button { Margin = new Thickness(0, 0, 8, 0) };
        _resetAll.Click += (_, _) =>
        {
            S.Reset();
            ApplyNow();
        };
        var export = new Button { Content = "Export theme...", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Save this theme as a file to share or keep" };
        export.Click += (_, _) => Export();
        var import = new Button { Content = "Import theme...", Style = Res<Style>("GhostButton"), ToolTip = "Load a theme file someone exported" };
        import.Click += (_, _) => Import();
        actions.Children.Add(_resetAll);
        actions.Children.Add(export);
        actions.Children.Add(import);
        Children.Add(actions);

        // ---------- The color picker, shared by every swatch ----------
        _pickerPopup = new Popup
        {
            Child = _picker,
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            PopupAnimation = PopupAnimation.None,
        };
        _picker.ColorChanged += (color, final) =>
        {
            if (_pickingKey == null) return;
            SetColor(_pickingKey, color);
            if (final) ApplyNow();
            else ApplySoon();
        };

        Loaded += (_, _) => Sync();
        Theme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        // Windows' own light/dark or accent changed under the System preset.
        if (!_applyTimer.IsEnabled) Sync();
    }

    // ---------- Changes ----------

    private void PickPreset(string id)
    {
        if (_syncing || id == S.Preset) return;
        var current = Theme.Find(S.Preset);
        if (S.IsCustomized)
        {
            string? answer = LuminaDialog.Show(Window.GetWindow(this), $"Switch to {Theme.Find(id).Name}?",
                $"Your changes to {current.Name} (its colors and style) are dropped. Export the theme first to keep a copy.",
                DialogKind.Question, null,
                new DialogButton("Keep editing", "cancel", Cancel: true),
                new DialogButton("Switch", "switch", Primary: true));
            if (answer != "switch")
            {
                Sync();
                return;
            }
        }
        S.Preset = id;
        S.Reset();
        ApplyNow();
    }

    /// <summary>A color equal to what the preset would give is no change at all, so it's dropped from the overrides.</summary>
    private static void SetColor(string key, Color color)
    {
        var without = S.Clone();
        without.Colors.Remove(key);
        var baseline = Theme.Resolve(without);
        if (baseline[key] == color) S.Colors.Remove(key);
        else S.Colors[key] = Theme.Hex(color);
    }

    private void ApplySoon()
    {
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void ApplyNow()
    {
        _applyTimer.Stop();
        Theme.Apply(S);
        Sync();
        Changed?.Invoke();
    }

    /// <summary>Puts the current theme into every control.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var t = Theme.Current;
            if (_presetTiles.TryGetValue(t.Preset.Id, out var tile)) tile.IsChecked = true;
            foreach (var row in _colorRows) row.Sync(t, S);

            _suggestions.Children.Clear();
            foreach (var color in t.Preset.Suggestions.Distinct())
                _suggestions.Children.Add(Suggestion(color, color == t["Accent"]));

            int nearest = CornerChoices.MinBy(c => Math.Abs(c.Radius - t.Corners)).Radius;
            foreach (var (radius, chip) in _corners) chip.IsChecked = radius == nearest;
            foreach (var (id, chip) in _fonts) chip.IsChecked = id == t.Font.Id;
            foreach (var (weight, chip) in _lines) chip.IsChecked = weight == t.Lines;
            foreach (var (id, chip) in _depths) chip.IsChecked = id == t.Depth;
            foreach (var (id, chip) in _patterns) chip.IsChecked = id == t.Pattern;
            _gradient.IsChecked = t.Gradient;
            _gradientEndRow.Visibility = t.Gradient ? Visibility.Visible : Visibility.Collapsed;
            _tint.Value = Math.Round(t.Tint * 100);
            _tintText.Text = $"{(int)_tint.Value}%";

            double body = Theme.Contrast(t["Text"], t["Panel"]);
            double hints = Math.Min(Theme.Contrast(t["Text3"], t["Window"]), Theme.Contrast(t["Text3"], t["Panel"]));
            bool ok = body >= 4.5 && hints >= 4.5;
            _readText.Text = $"Body text {body:0.0}:1, hints {hints:0.0}:1. 4.5:1 or more reads well.";
            _readBadgeText.Text = ok ? "Easy to read" : "Hard to read";
            _readBadgeText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "AccentTextBrush" : "DangerBrush");
            _readBadge.SetResourceReference(Border.BackgroundProperty, ok ? "AccentSoftBrush" : "DangerSoftBrush");

            _resetAll.Content = $"Reset to {t.Preset.Name}";
            _resetAll.IsEnabled = S.IsCustomized;
        }
        finally
        {
            _syncing = false;
        }
        RowLines.Update(this);
    }

    private void SyncAllToggle()
    {
        bool open = _allColors.Visibility == Visibility.Visible;
        _allToggleText.Text = open ? "Show fewer colors" : $"Show all {Theme.Tokens.Length - 1} colors";
    }

    // ---------- Export and import ----------

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export theme",
            Filter = "Lumina theme|*.json",
            FileName = $"Lumina {Theme.Current.Preset.Name}{(S.IsCustomized ? " custom" : "")}.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(S, FileOptions));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exporting a theme");
            LuminaDialog.Alert(Window.GetWindow(this), "Couldn't save the theme", ex.Message);
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import theme", Filter = "Lumina theme|*.json|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(dialog.FileName), FileOptions)
                         ?? throw new InvalidDataException("The file is empty.");
            if (Theme.Presets.All(p => p.Id != loaded.Preset)) throw new InvalidDataException($"It names a preset Lumina doesn't have ('{loaded.Preset}').");
            S.Preset = loaded.Preset;
            S.Reset();
            foreach (var (key, value) in loaded.Colors ?? new())
                if (Theme.Tokens.Any(t => t.Key == key) && Theme.TryParse(value) is { } c) S.Colors[key] = Theme.Hex(c);
            S.Corners = loaded.Corners is int r ? Math.Clamp(r, 0, 24) : null;
            S.Gradient = loaded.Gradient;
            S.Tint = loaded.Tint is double tint ? Math.Clamp(tint, 0, 1) : null;
            S.Font = Theme.Fonts.Any(f => f.Id == loaded.Font) ? loaded.Font : null;
            S.Lines = loaded.Lines is int lines ? Math.Clamp(lines, 1, 2) : null;
            S.Depth = Theme.Depths.Any(d => d.Id == loaded.Depth) ? loaded.Depth : null;
            S.Pattern = Theme.Patterns.Any(x => x.Id == loaded.Pattern) ? loaded.Pattern : null;
            ApplyNow();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Importing a theme");
            LuminaDialog.Alert(Window.GetWindow(this), "That isn't a Lumina theme", ex is JsonException ? "Lumina couldn't read the file." : ex.Message);
        }
    }

    // ---------- Building blocks ----------

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private static void AutomationName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static StackPanel Group(string title, UIElement content)
    {
        var group = new StackPanel { Tag = "group" };
        group.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SettingsGroupTitle") });
        group.Children.Add(content);
        return group;
    }

    private static Border Card(UIElement child, Thickness? padding = null)
    {
        var card = new Border { Style = Res<Style>("SettingsCard"), Child = child };
        if (padding is { } p) card.Padding = p;
        return card;
    }

    private static StackPanel RowText(string title, string hint)
    {
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SettingsRowTitle") });
        if (hint.Length > 0)
            words.Children.Add(new TextBlock { Text = hint, Style = Res<Style>("SettingsHint") });
        return words;
    }

    private static Border Row(UIElement control, string? title = null, string hint = "")
    {
        if (title == null) return new Border { Style = Res<Style>("SettingsRow"), Child = control };
        var dock = new DockPanel();
        DockPanel.SetDock(control, Dock.Right);
        dock.Children.Add(control);
        dock.Children.Add(RowText(title, hint));
        return new Border { Style = Res<Style>("SettingsRow"), Child = dock };
    }

    /// <summary>A row of chips in a box, one per choice; picking one stores it and applies the theme.</summary>
    private Border Choices<T>(Dictionary<T, RadioButton> chips, string group, IEnumerable<(string Label, T Value)> choices,
                              Action<RadioButton, T>? decorate, Action<T> store) where T : notnull
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in choices)
        {
            var chip = new RadioButton { Style = Res<Style>("Chip"), GroupName = group, Content = label, FontSize = 13 };
            decorate?.Invoke(chip, value);
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                store(value);
                ApplyNow();
            };
            chips[value] = chip;
            bar.Children.Add(chip);
        }
        var box = new Border
        {
            Padding = new Thickness(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = bar,
            Margin = new Thickness(20, 0, 0, 0),
        };
        box.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        box.SetResourceReference(Border.BorderThicknessProperty, "Line");
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");
        return box;
    }

    private static TextBlock FindHint(Border row) =>
        ((StackPanel)((DockPanel)row.Child).Children[1]).Children.OfType<TextBlock>().Last();

    private ColorRow ColorRowFor(Theme.Token token, Panel? extra)
    {
        var row = new ColorRow(token, extra);
        row.SwatchClicked += () => OpenPicker(row);
        row.HexEntered += color =>
        {
            SetColor(token.Key, color);
            ApplyNow();
        };
        row.ResetClicked += () =>
        {
            S.Colors.Remove(token.Key);
            ApplyNow();
        };
        _colorRows.Add(row);
        return row;
    }

    private void OpenPicker(ColorRow row)
    {
        _pickingKey = null;
        _picker.SetColor(Theme.Current[row.Token.Key]);
        _pickingKey = row.Token.Key;
        _pickerPopup.PlacementTarget = row.Swatch;
        _pickerPopup.HorizontalOffset = -200;
        _pickerPopup.IsOpen = true;
    }

    private Button Suggestion(Color color, bool selected)
    {
        var dot = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = Theme.Hex(color),
            Template = DotTemplate,
        };
        var fill = new Ellipse { Fill = new SolidColorBrush(color), StrokeThickness = selected ? 2 : 1, Margin = new Thickness(selected ? 0 : 2) };
        fill.SetResourceReference(Ellipse.StrokeProperty, selected ? "TextBrush" : "StrokeStrongBrush");
        dot.Content = fill;
        AutomationName(dot, $"Accent {Theme.Hex(color)}");
        dot.Click += (_, _) =>
        {
            SetColor("Accent", color);
            ApplyNow();
        };
        return dot;
    }

    private static readonly ControlTemplate DotTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<ContentPresenter /></ControlTemplate>");

    private static double PreviewRadius(int corners) => Math.Round(corners * 0.7);

    /// <summary>
    /// A small picture of a preset in its own style: its background and pattern, a card with "Aa" in its title
    /// font, its line weight and corners, and an accent button with its depth (shadow, glow or bevel).
    /// </summary>
    private static UIElement PresetPreview(ThemePreset preset)
    {
        var t = Theme.Resolve(new ThemeSettings { Preset = preset.Id });
        SolidColorBrush B(string key) => new(t[key]);
        Brush accent = t.Gradient ? new LinearGradientBrush(t["Accent"], t["Accent2"], 0) : B("Accent");
        double r = PreviewRadius(t.Corners), cardR = Math.Round(t.Corners * 0.5), pillR = Math.Round(t.Corners * 0.35);
        var line = new Thickness(t.Lines);

        var aa = new TextBlock
        {
            Text = "Aa",
            FontFamily = Theme.Family(t.Font.Display),
            FontSize = Theme.TitleSize(22, t),
            FontWeight = FontWeights.SemiBold,
            Foreground = B("Text"),
            Margin = new Thickness(0, -4, 0, 0),
        };
        if (t.Font.Pixel)
        {
            TextOptions.SetTextFormattingMode(aa, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(aa, Theme.WholeScreenScale() ? TextRenderingMode.Aliased : TextRenderingMode.Grayscale);
        }
        var words = new StackPanel();
        words.Children.Add(aa);
        words.Children.Add(new Border { Height = 4, Width = 34, CornerRadius = new CornerRadius(Math.Min(2, pillR)), Background = B("Text3"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 3, 0, 0) });

        var button = new Grid { Width = 34, Height = 14, VerticalAlignment = VerticalAlignment.Bottom };
        var fill = new Border { CornerRadius = new CornerRadius(pillR), Background = accent };
        if (t.Depth == "glow") fill.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = t["Accent"], Opacity = 0.7 };
        button.Children.Add(fill);
        if (t.Depth == "blocky")
            button.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 2), BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)) });
        var other = new Border { Width = 20, Height = 14, CornerRadius = new CornerRadius(pillR), Background = B("Raised"), BorderBrush = B("Stroke"), BorderThickness = line, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(other);
        buttons.Children.Add(button);

        var inner = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        inner.Children.Add(buttons);
        inner.Children.Add(words);

        var card = new Border
        {
            Background = B("Panel"),
            BorderBrush = B("Stroke"),
            BorderThickness = line,
            CornerRadius = new CornerRadius(cardR),
            Padding = new Thickness(10, 8, 9, 8),
            Child = inner,
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = t.Depth switch
            {
                "blocky" or "flat" => null,
                _ => new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = t.IsDark ? 0.4 : 0.12 },
            },
        };
        var preview = new Border
        {
            Height = PreviewHeight,
            Background = Theme.WindowFill(t),
            BorderBrush = B("StrokeStrong"),
            BorderThickness = line,
            CornerRadius = new CornerRadius(r),
            Padding = new Thickness(12, 12, 14, 14),
            Child = card,
            ClipToBounds = true,
        };

        var name = new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(1, 10, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var caption = new TextBlock { Text = preset.Caption, FontSize = 12.5, Margin = new Thickness(1, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel();
        stack.Children.Add(preview);
        stack.Children.Add(name);
        stack.Children.Add(caption);
        return stack;
    }

    /// <summary>One color: a swatch that opens the picker, its hex code, and a reset once it differs from the preset.</summary>
    private sealed class ColorRow
    {
        public Theme.Token Token { get; }
        public Border Row { get; }
        public Button Swatch { get; }
        public event Action? SwatchClicked;
        public event Action<Color>? HexEntered;
        public event Action? ResetClicked;

        private readonly Border _chip = new() { Width = 26, Height = 26, BorderThickness = new Thickness(1) };
        private readonly TextBox _hex = new()
        {
            Width = 100,
            Padding = new Thickness(5, 3, 5, 3), // the template applies it twice
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            CharacterCasing = CharacterCasing.Upper,
            MaxLength = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        private readonly Button _reset;

        public ColorRow(Theme.Token token, Panel? extra)
        {
            Token = token;
            _chip.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");
            _chip.SetResourceReference(Border.CornerRadiusProperty, "RadiusKey");
            Swatch = new Button
            {
                Style = Res<Style>("IconButton"),
                Padding = new Thickness(4),
                Content = _chip,
                ToolTip = $"Change {token.Label.ToLowerInvariant()}",
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationName(Swatch, $"Pick the {token.Label.ToLowerInvariant()} color");
            Swatch.Click += (_, _) => SwatchClicked?.Invoke();
            AutomationName(_hex, $"{token.Label} color code");
            _hex.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
            };
            _hex.LostKeyboardFocus += (_, _) => Commit();
            _reset = new Button
            {
                Style = Res<Style>("GhostButton"),
                Content = "Reset",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Back to the preset's color",
                Visibility = Visibility.Collapsed,
            };
            _reset.Click += (_, _) => ResetClicked?.Invoke();

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(_reset);
            controls.Children.Add(Swatch);
            controls.Children.Add(_hex);
            var words = RowText(token.Label, token.Description);
            if (extra != null) words.Children.Add(extra);
            var dock = new DockPanel();
            DockPanel.SetDock(controls, Dock.Right);
            dock.Children.Add(controls);
            dock.Children.Add(words);
            Row = new Border { Style = Res<Style>("SettingsRow"), Child = dock };
        }

        private void Commit()
        {
            var current = ((SolidColorBrush)_chip.Background).Color;
            if (Theme.TryParse(_hex.Text) is { } color) { if (color != current) HexEntered?.Invoke(color); }
            else _hex.Text = Theme.Hex(((SolidColorBrush)_chip.Background).Color);
        }

        public void Sync(ThemeTokens theme, ThemeSettings settings)
        {
            var color = theme[Token.Key];
            _chip.Background = new SolidColorBrush(color);
            if (!_hex.IsKeyboardFocused) _hex.Text = Theme.Hex(color);
            _reset.Visibility = settings.Colors.ContainsKey(Token.Key) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>Draws the line between setting rows, never above the first visible row of a card.</summary>
public static class RowLines
{
    public static void Update(DependencyObject root)
    {
        var rowStyle = Application.Current.TryFindResource("SettingsRow") as Style;
        foreach (var card in Descendants(root).OfType<Border>().Where(b => b.Style == Application.Current.TryFindResource("SettingsCard") as Style))
        {
            bool first = true;
            foreach (var row in RowsIn(card, rowStyle))
            {
                if (!IsShown(row, card)) continue;
                row.BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0);
                first = false;
            }
        }
    }

    /// <summary>Rows inside a card, in order, without looking inside a row.</summary>
    private static IEnumerable<Border> RowsIn(DependencyObject parent, Style? rowStyle)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Border b && b.Style == rowStyle) yield return b;
            else foreach (var r in RowsIn(child, rowStyle)) yield return r;
        }
    }

    private static bool IsShown(FrameworkElement element, DependencyObject stop)
    {
        for (DependencyObject? d = element; d != null && d != stop; d = LogicalTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
