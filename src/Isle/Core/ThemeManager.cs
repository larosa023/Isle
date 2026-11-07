using System;
using System.Windows;
using System.Windows.Media;

namespace Isle.Core;

/// <summary>Publishes the island's brushes as application resources (DynamicResource "Island.*").</summary>
public static class ThemeManager
{
    static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);
    static bool _autoAccent = true;
    static SolidColorBrush _accentBrush = Frozen(DefaultAccent);

    public static bool IsLight { get; private set; }

    /// <summary>Per-activity accent when "auto", otherwise the fixed accent.</summary>
    public static Brush BrushFor(Color? color) =>
        _autoAccent && color.HasValue ? Frozen(color.Value) : _accentBrush;

    public static void Apply(AppSettings s)
    {
        var p = Palette(s);
        IsLight = p.light;

        var r = Application.Current.Resources;
        r["Island.Background"] = Frozen(p.bg);
        r["Island.Foreground"] = Frozen(p.fg);
        r["Island.Secondary"] = Frozen(p.sec);
        r["Island.Border"] = Frozen(p.border);
        r["Island.Button"] = Frozen(p.button);
        r["Island.ButtonHover"] = Frozen(p.hover);
        r["Island.Track"] = Frozen(p.light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        r["Island.PrimaryButton"] = Frozen(p.fg);
        r["Island.PrimaryButtonText"] = Frozen(p.light ? Colors.White : Colors.Black);

        if (TryParseColor(s.Accent, out var accent))
        {
            _autoAccent = false;
            _accentBrush = Frozen(accent);
        }
        else
        {
            _autoAccent = true;
            _accentBrush = Frozen(DefaultAccent);
        }
        r["Island.Accent"] = _accentBrush;
    }

    static (Color bg, Color fg, Color sec, Color border, Color button, Color hover, bool light) Palette(AppSettings s)
    {
        switch ((s.Theme ?? "").Trim().ToLowerInvariant())
        {
            case "graphite":
                return (Hex("#1C1C1E"), Hex("#F2F2F7"), Hex("#98989D"), Hex("#2C2C2E"), Hex("#2C2C2E"), Hex("#3A3A3C"), false);
            case "glass":
                return (Hex("#D9141416"), Hex("#FFFFFF"), Hex("#B0FFFFFF"), Hex("#33FFFFFF"), Hex("#26FFFFFF"), Hex("#40FFFFFF"), false);
            case "frost":
                return (Hex("#F2F2F7"), Hex("#000000"), Hex("#6C6C70"), Hex("#D1D1D6"), Hex("#E5E5EA"), Hex("#D1D1D6"), true);
            case "custom":
                var c = s.CustomTheme ?? new CustomThemeColors();
                var bg = HexOr(c.Background, Colors.Black);
                bool light = (bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114) > 150;
                return (bg, HexOr(c.Foreground, Colors.White), HexOr(c.Secondary, Hex("#8E8E93")),
                        HexOr(c.Border, Colors.Transparent), HexOr(c.Button, Hex("#1C1C1E")), HexOr(c.ButtonHover, Hex("#2C2C2E")), light);
            default: // midnight — true black, like the hardware island
                return (Hex("#000000"), Hex("#FFFFFF"), Hex("#8E8E93"), Hex("#00000000"), Hex("#1C1C1E"), Hex("#2C2C2E"), false);
        }
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }

    static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);
    static Color HexOr(string? s, Color fallback) => TryParseColor(s, out var c) ? c : fallback;

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
