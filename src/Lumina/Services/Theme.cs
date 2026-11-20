using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Lumina.Services;

/// <summary>What the user picked in Settings, Appearance: a preset plus any changes made on top of it.</summary>
public sealed class ThemeSettings
{
    public string Preset { get; set; } = Theme.DefaultPreset;
    /// <summary>Colors changed from the preset, by token name (see <see cref="Theme.Tokens"/>), as #RRGGBB.</summary>
    public Dictionary<string, string> Colors { get; set; } = new();
    /// <summary>Corner radius of cards; null keeps the preset's.</summary>
    public int? Corners { get; set; }
    /// <summary>Blend the accent into a second color; null keeps the preset's choice.</summary>
    public bool? Gradient { get; set; }
    /// <summary>How much the accent tints the grays, 0 to 1; null keeps the preset's.</summary>
    public double? Tint { get; set; }
    /// <summary>Typeface pairing (see <see cref="Theme.Fonts"/>); null keeps the preset's.</summary>
    public string? Font { get; set; }
    /// <summary>Border weight in px, 1 or 2; null keeps the preset's.</summary>
    public int? Lines { get; set; }
    /// <summary>How things sit above the background (see <see cref="Theme.Depths"/>); null keeps the preset's.</summary>
    public string? Depth { get; set; }
    /// <summary>What's behind the window's content (see <see cref="Theme.Patterns"/>); null keeps the preset's.</summary>
    public string? Pattern { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustomized => Colors.Count > 0 || Corners != null || Gradient != null || Tint != null
                                || Font != null || Lines != null || Depth != null || Pattern != null;

    public ThemeSettings Clone() => new()
    {
        Preset = Preset,
        Colors = new Dictionary<string, string>(Colors),
        Corners = Corners,
        Gradient = Gradient,
        Tint = Tint,
        Font = Font,
        Lines = Lines,
        Depth = Depth,
        Pattern = Pattern,
    };

    public void Reset()
    {
        Colors.Clear();
        Corners = null;
        Gradient = null;
        Tint = null;
        Font = null;
        Lines = null;
        Depth = null;
        Pattern = null;
    }
}

/// <summary>A starting point for a theme. Everything that isn't listed is worked out from these colors.</summary>
public sealed class ThemePreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Caption { get; init; }
    public required Color Window { get; init; }
    public required Color Panel { get; init; }
    public required Color Text { get; init; }
    public required Color Accent { get; init; }
    public required Color Accent2 { get; init; }
    public required Color Danger { get; init; }
    public double Tint { get; init; }
    public int Corners { get; init; } = 10;
    public bool Gradient { get; init; }
    public string Font { get; init; } = "segoe";
    public int Lines { get; init; } = 1;
    public string Depth { get; init; } = "soft";
    public string Pattern { get; init; } = "none";
    /// <summary>Accents offered as one-click choices on the Appearance page.</summary>
    public Color[] Suggestions { get; init; } = [];
    /// <summary>Hand-tuned values used instead of the worked-out ones while the colors they depend on are unchanged.</summary>
    public IReadOnlyDictionary<string, Color> Exact { get; init; } = new Dictionary<string, Color>();
}

/// <summary>Every color the app uses, worked out from a preset and the user's changes.</summary>
public sealed class ThemeTokens
{
    public required IReadOnlyDictionary<string, Color> Colors { get; init; }
    public required int Corners { get; init; }
    public required bool Gradient { get; init; }
    public required double Tint { get; init; }
    public required Theme.FontChoice Font { get; init; }
    public required int Lines { get; init; }
    public required string Depth { get; init; }
    public required string Pattern { get; init; }
    public required bool IsDark { get; init; }
    public required ThemePreset Preset { get; init; }

    public Color this[string token] => Colors[token];
}

/// <summary>
/// Builds the app's palette from the chosen preset and swaps it in live. Styles refer to the palette with
/// DynamicResource, so every open window follows a change at once.
/// </summary>
public static class Theme
{
    public const string DefaultPreset = "graphite";

    /// <summary>A color the user can change, with the label the Appearance page shows for it.</summary>
    public sealed record Token(string Key, string Label, string Description, bool IsInput);

    /// <summary>The five colors everything else is worked out from, then the rest (the "all colors" list).</summary>
    public static readonly Token[] Tokens =
    [
        new("Accent", "Accent", "Buttons, switches and what's selected", true),
        new("Window", "Background", "The window behind everything", true),
        new("Panel", "Surface", "Cards, panels and menus", true),
        new("Text", "Text", "Headings and body text", true),
        new("Danger", "Danger", "Delete, errors and recording", true),
        new("Accent2", "Gradient end", "Where the accent blends to", true),
        new("Raised", "Controls", "Buttons, boxes and dropdowns", false),
        new("Hover", "Hover", "Under the pointer", false),
        new("Pressed", "Pressed", "While clicking", false),
        new("Stroke", "Lines", "Borders and dividers", false),
        new("StrokeStrong", "Strong lines", "Borders of popups and keys", false),
        new("Text2", "Secondary text", "Descriptions and labels", false),
        new("Text3", "Hint text", "Times, counts and hints", false),
        new("AccentHover", "Accent hover", "Accent under the pointer", false),
        new("AccentSoft", "Accent wash", "Behind selected items", false),
        new("AccentText", "Accent text", "Links and selected text", false),
        new("OnAccent", "Text on accent", "Labels on accent buttons", false),
    ];

    /// <summary>
    /// A typeface pairing: one for text and controls, one for titles. "./Assets/Fonts/#Name" is a font built into
    /// the app (Assets/Fonts, SIL Open Font License). <paramref name="Pixel"/>: the title font is drawn on a grid of
    /// 1/20 em, so titles are set at multiples of 20 px, where each of its pixels covers whole screen pixels.
    /// </summary>
    public sealed record FontChoice(string Id, string Label, string Ui, string Display, bool Pixel = false)
    {
        /// <summary>Segoe ships with hints for Windows' pixel-snapped text; the bundled fonts have none.</summary>
        public bool Hinted => Id == "segoe";
    }

    private const string Bundled = "./Assets/Fonts/#";

    public static readonly FontChoice[] Fonts =
    [
        new("segoe", "Segoe", "Segoe UI Variable Text, Segoe UI", "Segoe UI Variable Display, Segoe UI"),
        // Geist's tight spacing breaks up at body sizes on Windows, so it sets titles, over Inter.
        new("geist", "Geist", Bundled + "Inter, Segoe UI", Bundled + "Geist, Segoe UI"),
        new("serif", "Serif", Bundled + "Inter, Segoe UI", Bundled + "Newsreader, Georgia"),
        new("rounded", "Rounded", Bundled + "Nunito, Segoe UI", Bundled + "Nunito, Segoe UI"),
        new("pixel", "Pixel", Bundled + "Inter, Segoe UI", Bundled + "Pixelify Sans, Segoe UI", Pixel: true),
    ];

    /// <summary>A title size for the current font: as given, or the nearest size a pixel font draws cleanly at.</summary>
    public static double TitleSize(double normal, ThemeTokens? t = null) =>
        (t ?? Current).Font.Pixel ? Math.Max(20, Math.Round(normal / 20) * 20) : normal;

    /// <summary>Applies the title text settings (pixel snapping and edges) to a title built in code.</summary>
    public static void StyleTitle(FrameworkElement title)
    {
        title.SetResourceReference(TextOptions.TextFormattingModeProperty, "TitleTextMode");
        title.SetResourceReference(TextOptions.TextRenderingModeProperty, "TitleRenderMode");
    }

    /// <summary>True when the screen scale is 100%, 200% and so on.</summary>
    public static bool WholeScreenScale()
    {
        try
        {
            return GetDpiForSystem() % 96 == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static readonly Uri FontBase = new("pack://application:,,,/Lumina;component/");

    /// <summary>A font family from a FontChoice source, finding the app's own fonts as well as installed ones.</summary>
    public static FontFamily Family(string source) => new(FontBase, source);

    /// <summary>Flat: lines only. Soft: blurred shadows. Glow: soft, and accent buttons glow. Blocky: hard offset shadows and bevels.</summary>
    public static readonly (string Id, string Label)[] Depths = [("flat", "Flat"), ("soft", "Soft"), ("glow", "Glow"), ("blocky", "Blocky")];

    public static readonly (string Id, string Label)[] Patterns = [("none", "Plain"), ("dots", "Dots"), ("blocks", "Blocks")];

    private static readonly string[] NeutralTokens = ["Raised", "Hover", "Pressed", "Stroke", "StrokeStrong", "Text2", "Text3", "ScrollThumb", "ScrollThumbHover", "Dot"];
    private static readonly string[] AccentTokens = ["AccentHover", "AccentSoft", "AccentText", "OnAccent"];

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly IReadOnlyList<ThemePreset> Presets =
    [
        new()
        {
            Id = "midnight", Name = "Midnight", Caption = "The original look",
            Window = C("#0E0D15"), Panel = C("#15141F"), Text = C("#EEECF8"),
            Accent = C("#7C5CFF"), Accent2 = C("#3D8BFF"), Danger = C("#FF5C7A"),
            Tint = 0.3, Corners = 12, Gradient = true,
            Suggestions = [C("#7C5CFF"), C("#3D8BFF"), C("#2FBF9A"), C("#FF7A59")],
            Exact = new Dictionary<string, Color>
            {
                ["Raised"] = C("#1C1B29"), ["Hover"] = C("#25243A"), ["Pressed"] = C("#2E2C48"),
                ["Stroke"] = C("#262438"), ["StrokeStrong"] = C("#36345A"), ["Text2"] = C("#A19EBE"),
                ["ScrollThumb"] = C("#33315A"), ["ScrollThumbHover"] = C("#5A56A0"), ["Dot"] = C("#24223A"),
                ["AccentHover"] = C("#8E74FF"), ["AccentSoft"] = C("#2A2352"), ["AccentText"] = C("#BCAEFF"),
                ["OnAccent"] = Colors.White,
            },
        },
        new()
        {
            Id = "graphite", Name = "Graphite", Caption = "Flat, gray and precise",
            Window = C("#111214"), Panel = C("#18191C"), Text = C("#ECEDEF"),
            Accent = C("#E8E9EB"), Accent2 = C("#9AA0A8"), Danger = C("#F2555A"),
            Corners = 6, Font = "geist", Depth = "flat", Pattern = "dots",
            Suggestions = [C("#E8E9EB"), C("#5B9BFF"), C("#3FC495"), C("#F2A33A")],
        },
        new()
        {
            Id = "paper", Name = "Paper", Caption = "Light, with serif titles",
            Window = C("#F4F2EC"), Panel = C("#FFFFFF"), Text = C("#1A1915"),
            Accent = C("#1E6E54"), Accent2 = C("#2B7A9E"), Danger = C("#C23A36"),
            Corners = 10, Font = "serif",
            Suggestions = [C("#1E6E54"), C("#2456C9"), C("#B5471B"), C("#1A1915")],
        },
        new()
        {
            Id = "ember", Name = "Ember", Caption = "Warm, round and glowing",
            Window = C("#13110F"), Panel = C("#1B1815"), Text = C("#F2EDE6"),
            Accent = C("#F0A23B"), Accent2 = C("#E8603C"), Danger = C("#FF6B5A"),
            Corners = 14, Font = "rounded", Depth = "glow",
            Suggestions = [C("#F0A23B"), C("#E8734A"), C("#D9C38F"), C("#8FBF6A")],
        },
        new()
        {
            Id = "strider", Name = "Strider", Caption = "Crimson, chunky and square",
            Window = C("#191214"), Panel = C("#251C1E"), Text = C("#F5EFEC"),
            Accent = C("#C8303B"), Accent2 = C("#F2963A"), Danger = C("#FF7A2E"),
            Tint = 0.06, Corners = 0, Font = "pixel", Lines = 2, Depth = "blocky",
            Suggestions = [C("#C8303B"), C("#F2963A"), C("#1FA595"), C("#3C8527")],
        },
        SystemPreset(),
    ];

    public static ThemePreset Find(string? id) =>
        id == "system" ? SystemPreset() : Presets.FirstOrDefault(p => p.Id == id) ?? Presets.First(p => p.Id == DefaultPreset);

    /// <summary>Follows Windows: light or dark apps, and the accent color from Personalization.</summary>
    public static ThemePreset SystemPreset()
    {
        bool light = WindowsUsesLightTheme();
        var accent = WindowsAccent(light) ?? (light ? C("#005FB8") : C("#60CDFF"));
        return light
            ? new ThemePreset
            {
                Id = "system", Name = "System", Caption = "Follows Windows",
                Window = C("#F3F3F3"), Panel = C("#FBFBFB"), Text = C("#1B1B1B"),
                Accent = accent, Accent2 = accent, Danger = C("#C42B1C"), Corners = 8,
                Suggestions = [accent, C("#005FB8"), C("#0F7B0F"), C("#9D5D00")],
            }
            : new ThemePreset
            {
                Id = "system", Name = "System", Caption = "Follows Windows",
                Window = C("#202020"), Panel = C("#2B2B2B"), Text = C("#FFFFFF"),
                Accent = accent, Accent2 = accent, Danger = C("#FF99A4"), Corners = 8,
                Suggestions = [accent, C("#60CDFF"), C("#6CCB5F"), C("#FFB900")],
            };
    }

    public static ThemeTokens Current { get; private set; } = Resolve(new ThemeSettings());

    /// <summary>Raised after the palette changes, for code that drew with the old colors.</summary>
    public static event Action? Changed;

    private static bool _watchingWindows;

    /// <summary>Builds the palette for these settings and puts it in the app's resources.</summary>
    public static void Apply(ThemeSettings settings)
    {
        Current = Resolve(settings);
        var app = Application.Current;
        if (app == null) return;
        var palette = BuildResources(Current);
        var merged = app.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Contains(PaletteMarker));
        if (old != null) merged[merged.IndexOf(old)] = palette;
        else merged.Insert(0, palette);

        if (!_watchingWindows)
        {
            _watchingWindows = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle
                    && Current.Preset.Id == "system")
                    app.Dispatcher.BeginInvoke(() => Apply(App.Settings.Appearance));
            };
        }
        Changed?.Invoke();
    }

    private const string PaletteMarker = "Lumina.Palette";

    /// <summary>Works out every token. Pure: nothing is applied.</summary>
    public static ThemeTokens Resolve(ThemeSettings settings)
    {
        var preset = Find(settings.Preset);
        Color Input(string key, Color fallback) => TryGet(settings.Colors, key) ?? fallback;

        var window = Input("Window", preset.Window);
        var panel = Input("Panel", preset.Panel);
        var text = Input("Text", preset.Text);
        var accent = Input("Accent", preset.Accent);
        var accent2 = Input("Accent2", preset.Accent2);
        var danger = Input("Danger", preset.Danger);
        double tint = Math.Clamp(settings.Tint ?? preset.Tint, 0, 1);
        string depth = Pick(settings.Depth, preset.Depth, Depths);
        bool dark = Luminance(window) < 0.4;

        var t = new Dictionary<string, Color>
        {
            ["Window"] = window, ["Panel"] = panel, ["Text"] = text,
            ["Accent"] = accent, ["Accent2"] = accent2, ["Danger"] = danger,
        };

        // Grays lean toward the accent by the tint amount.
        var fg = Mix(text, accent, tint * 0.5);
        double[] k = dark ? [0.05, 0.10, 0.15, 0.08, 0.17] : [0.04, 0.07, 0.11, 0.10, 0.20];
        t["Raised"] = Mix(panel, fg, k[0]);
        t["Hover"] = Mix(panel, fg, k[1]);
        t["Pressed"] = Mix(panel, fg, k[2]);
        t["Stroke"] = Mix(panel, fg, k[3]);
        t["StrokeStrong"] = Mix(panel, fg, k[4]);
        // Secondary and hint text fade toward the background, but never below 7:1 and 4.5:1 on either surface.
        t["Text2"] = Fade(fg, window, panel, 7, 0.36);
        t["Text3"] = Fade(fg, window, panel, 4.5, 0.56);
        t["ScrollThumb"] = Mix(window, fg, dark ? 0.16 : 0.22);
        t["ScrollThumbHover"] = Mix(window, fg, dark ? 0.32 : 0.4);
        t["Dot"] = Mix(window, fg, dark ? 0.1 : 0.14);

        t["OnAccent"] = Contrast(Colors.White, accent) >= Contrast(C("#141414"), accent) ? Colors.White : C("#141414");
        t["AccentHover"] = Luminance(accent) > 0.5 ? Mix(accent, Colors.Black, 0.08) : Mix(accent, Colors.White, 0.12);
        t["AccentSoft"] = Mix(panel, accent, dark ? (tint > 0 ? 0.22 : 0.16) : 0.1);
        t["AccentText"] = Toward(accent, text, panel, 4.5);

        // A preset's hand-tuned values hold as long as what they were tuned against is unchanged.
        bool neutralsAsPreset = window == preset.Window && panel == preset.Panel && text == preset.Text
                                && tint == preset.Tint && (tint == 0 || accent == preset.Accent);
        bool accentAsPreset = accent == preset.Accent && panel == preset.Panel && text == preset.Text;
        foreach (var (key, value) in preset.Exact)
        {
            if (NeutralTokens.Contains(key) ? neutralsAsPreset : AccentTokens.Contains(key) && accentAsPreset) t[key] = value;
        }

        // Blocky outlines are darker than what they wrap, like an inked edge, and controls stand further out.
        if (depth == "blocky")
        {
            var ink = dark ? Colors.Black : text;
            t["Stroke"] = Mix(window, ink, dark ? 0.5 : 0.22);
            t["StrokeStrong"] = Mix(window, ink, dark ? 0.75 : 0.4);
            t["Raised"] = Mix(panel, fg, dark ? 0.09 : 0.05);
            t["Hover"] = Mix(panel, fg, dark ? 0.15 : 0.09);
            t["Pressed"] = Mix(panel, fg, dark ? 0.05 : 0.12);
        }

        // Then anything set by hand in the full color list.
        foreach (var token in Tokens.Where(x => !x.IsInput))
            if (TryGet(settings.Colors, token.Key) is { } c) t[token.Key] = c;

        return new ThemeTokens
        {
            Colors = t,
            Corners = Math.Clamp(settings.Corners ?? preset.Corners, 0, 24),
            Gradient = settings.Gradient ?? preset.Gradient,
            Tint = tint,
            Font = Fonts.FirstOrDefault(f => f.Id == settings.Font) ?? Fonts.FirstOrDefault(f => f.Id == preset.Font) ?? Fonts[0],
            Lines = Math.Clamp(settings.Lines ?? preset.Lines, 1, 2),
            Depth = depth,
            Pattern = Pick(settings.Pattern, preset.Pattern, Patterns),
            IsDark = dark,
            Preset = preset,
        };
    }

    private static string Pick(string? chosen, string preset, (string Id, string Label)[] choices) =>
        choices.Any(c => c.Id == chosen) ? chosen! : choices.Any(c => c.Id == preset) ? preset : choices[0].Id;

    private static ResourceDictionary BuildResources(ThemeTokens t)
    {
        var d = new ResourceDictionary { [PaletteMarker] = true };
        SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        d["WindowBrush"] = Brush(t["Window"]);
        d["PanelBrush"] = Brush(t["Panel"]);
        d["RaisedBrush"] = Brush(t["Raised"]);
        d["HoverBrush"] = Brush(t["Hover"]);
        d["PressedBrush"] = Brush(t["Pressed"]);
        d["StrokeBrush"] = Brush(t["Stroke"]);
        d["StrokeStrongBrush"] = Brush(t["StrokeStrong"]);
        d["TextBrush"] = Brush(t["Text"]);
        d["Text2Brush"] = Brush(t["Text2"]);
        d["Text3Brush"] = Brush(t["Text3"]);
        d["AccentBrush"] = Brush(t["Accent"]);
        d["AccentHoverBrush"] = Brush(t["AccentHover"]);
        d["AccentSoftBrush"] = Brush(t["AccentSoft"]);
        d["AccentTextBrush"] = Brush(t["AccentText"]);
        d["OnAccentBrush"] = Brush(t["OnAccent"]);
        d["DangerBrush"] = Brush(t["Danger"]);
        d["DangerSoftBrush"] = Brush(Mix(t["Panel"], t["Danger"], t.IsDark ? 0.18 : 0.1));
        d["ScrollThumbBrush"] = Brush(t["ScrollThumb"]);
        d["ScrollThumbHoverBrush"] = Brush(t["ScrollThumbHover"]);
        d["ScrimBrush"] = Brush(Color.FromArgb(t.IsDark ? (byte)0xA6 : (byte)0x8C, t["Window"].R, t["Window"].G, t["Window"].B));
        d["AccentColor"] = t["Accent"];
        d["Accent2Color"] = t.Gradient ? t["Accent2"] : t["Accent"];

        Brush fill;
        if (t.Gradient)
        {
            var g = new LinearGradientBrush(t["Accent"], t["Accent2"], new Point(0, 0), new Point(1, 1));
            g.Freeze();
            fill = g;
        }
        else fill = Brush(t["Accent"]);
        d["AccentFill"] = fill;

        var dots = new DrawingGroup();
        dots.Children.Add(new GeometryDrawing(Brush(t["Window"]), null, new RectangleGeometry(new Rect(0, 0, 22, 22))));
        dots.Children.Add(new GeometryDrawing(Brush(t["Dot"]), null, new EllipseGeometry(new Point(11, 11), 1, 1)));
        var grid = new DrawingBrush(dots) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 22, 22), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        grid.Freeze();
        d["DotGridBrush"] = grid;
        d["WindowFill"] = WindowFill(t);

        d["UiFont"] = Family(t.Font.Ui);
        d["DisplayFont"] = Family(t.Font.Display);
        // Pixel-snapped ("Display") text needs hinted fonts; unhinted ones break up into stray pixels there, so
        // they're laid out at their true shapes instead. Pixel titles snap again, and stay hard-edged where the
        // screen scale is a whole number, so each font pixel is a crisp block.
        d["TextMode"] = t.Font.Hinted ? TextFormattingMode.Display : TextFormattingMode.Ideal;
        d["TitleTextMode"] = t.Font.Hinted || t.Font.Pixel ? TextFormattingMode.Display : TextFormattingMode.Ideal;
        d["TitleRenderMode"] = t.Font.Pixel ? (WholeScreenScale() ? TextRenderingMode.Aliased : TextRenderingMode.Grayscale) : TextRenderingMode.Auto;
        d["PageTitleSize"] = TitleSize(24, t);
        d["SectionTitleSize"] = TitleSize(18, t);
        // Small numbers (the recording timer) stay in the text font when titles are pixel type.
        d["NumberFont"] = Family(t.Font.Pixel ? t.Font.Ui : t.Font.Display);

        // Border weight: most outlines, and keycaps with their heavier bottom edge.
        int w = t.Lines;
        d["Line"] = new Thickness(w);
        d["LineKey"] = new Thickness(w, w, w, w + 1);

        // Depth: shadows under floating things and cards, a glow on accent buttons, bevels on buttons.
        bool blocky = t.Depth == "blocky";
        d["CardShadow"] = null;
        switch (t.Depth)
        {
            case "flat":
                d["FloatShadow"] = Shadow(10, 2, t.IsDark ? 0.35 : 0.1);
                d["SoftShadow"] = null;
                break;
            case "blocky":
                // A solid ledge straight below, no blur: things look like slabs, not glowing cards.
                var hard = HardShadow(t);
                d["FloatShadow"] = Shadow(0, 4, 1, hard);
                d["SoftShadow"] = Shadow(0, 3, 1, hard);
                break;
            default:
                d["FloatShadow"] = Shadow(24, 4, t.IsDark ? 0.45 : 0.16);
                d["SoftShadow"] = Shadow(14, 2, t.IsDark ? 0.35 : 0.12);
                break;
        }
        d["AccentGlow"] = t.Depth == "glow" ? Shadow(22, 0, t.IsDark ? 0.55 : 0.35, t["Accent"]) : null;
        // Blocky buttons: a hairline of light on top and a thick darker ledge at the bottom.
        d["BevelTop"] = new Thickness(0, blocky ? 1 : 0, 0, 0);
        d["BevelBottom"] = new Thickness(0, 0, 0, blocky ? 3 : 0);
        d["BevelFace"] = new Thickness(0, 0, 0, blocky ? 3 : 0); // keeps labels off the ledge
        // An accent fill on a bordered button: inside the inked outline when Blocky, over the whole button otherwise.
        d["FillInset"] = new Thickness(blocky ? w : 0);
        d["BevelLightBrush"] = Brush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        d["BevelShadeBrush"] = Brush(Color.FromArgb(0x6B, 0, 0, 0));

        // One radius scale, from the card corners: Midnight's 12 gives 14 / 12 / 9 / 7 / 5.
        int r = t.Corners;
        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        d["RadiusLarge"] = new CornerRadius(r + 2);
        d["RadiusCard"] = new CornerRadius(r);
        d["RadiusControl"] = new CornerRadius(Round(r * 0.75));
        d["RadiusItem"] = new CornerRadius(Round(r * 0.58));
        d["RadiusKey"] = new CornerRadius(Math.Max(2, Round(r * 0.42)));
        d["RadiusCardTop"] = new CornerRadius(Math.Max(0, r - w), Math.Max(0, r - w), 0, 0);
        // Switches, slider knobs and scroll thumbs: fully round, unless corners are square.
        d["RadiusPill"] = new CornerRadius(r == 0 ? 0 : 999);
        return d;
    }

    private static DropShadowEffect Shadow(double blur, double depth, double opacity, Color? color = null, double direction = 270)
    {
        var e = new DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = direction, Opacity = opacity, Color = color ?? Colors.Black };
        e.Freeze();
        return e;
    }

    /// <summary>The Blocky depth's shadow color: a darker shade of the window, or a light gray on light themes.</summary>
    public static Color HardShadow(ThemeTokens t) =>
        t.IsDark ? Mix(t["Window"], Colors.Black, 0.7) : Mix(t["Window"], t["Text"], 0.25);

    /// <summary>The window background: plain, or with the theme's pattern.</summary>
    public static Brush WindowFill(ThemeTokens t)
    {
        if (t.Pattern == "dots") return DotPattern(t);
        if (t.Pattern == "blocks") return BlockPattern(t);
        var plain = new SolidColorBrush(t["Window"]);
        plain.Freeze();
        return plain;
    }

    /// <summary>A faint dot every 20 px.</summary>
    private static Brush DotPattern(ThemeTokens t)
    {
        var dot = Mix(t["Window"], t["Text"], t.IsDark ? 0.09 : 0.12);
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(t["Window"]), null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(dot), null, new EllipseGeometry(new Point(10, 10), 0.9, 0.9)));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 20, 20), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        b.Freeze();
        return b;
    }

    /// <summary>Rough stone: a tile of 3 px blocks in three close shades of the window color.</summary>
    private static Brush BlockPattern(ThemeTokens t)
    {
        const int n = 16, px = 3;
        var light = Mix(t["Window"], t["Text"], t.IsDark ? 0.035 : 0.045);
        var dark = Mix(t["Window"], Colors.Black, t.IsDark ? 0.3 : 0.05);
        var rng = new Random(7); // the same stone every time
        var pixels = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            double roll = rng.NextDouble();
            var c = roll < 0.2 ? light : roll < 0.42 ? dark : t["Window"];
            pixels[i * 4] = c.B;
            pixels[i * 4 + 1] = c.G;
            pixels[i * 4 + 2] = c.R;
            pixels[i * 4 + 3] = 255;
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, pixels, n * 4);
        var b = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n * px, n * px), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.NearestNeighbor);
        b.Freeze();
        return b;
    }

    // ---------- Windows ----------

    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The Windows accent in the shade Windows itself uses on dark or light app backgrounds.</summary>
    private static Color? WindowsAccent(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } p)
            {
                int i = light ? 4 : 1; // dark shade 1 on light backgrounds, light shade 2 on dark ones
                return Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the Windows accent color");
        }
        return null;
    }

    // ---------- Color math ----------

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static Color? TryGet(Dictionary<string, string> colors, string key) =>
        colors.TryGetValue(key, out var hex) ? TryParse(hex) : null;

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Fades text toward the background by <paramref name="start"/>, backing off until it reads at <paramref name="target"/> on both surfaces.</summary>
    private static Color Fade(Color fg, Color bg, Color surface, double target, double start)
    {
        for (double t = start; t > 0; t -= 0.02)
        {
            var c = Mix(fg, bg, t);
            if (Contrast(c, bg) >= target && Contrast(c, surface) >= target) return c;
        }
        return fg;
    }

    /// <summary>Moves a color toward another until it reads at <paramref name="target"/> on <paramref name="bg"/>.</summary>
    private static Color Toward(Color a, Color b, Color bg, double target)
    {
        for (double t = 0; t <= 1; t += 0.05)
        {
            var c = Mix(a, b, t);
            if (Contrast(c, bg) >= target) return c;
        }
        return b;
    }
}
