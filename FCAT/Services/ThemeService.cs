using System.Windows;
using System.Windows.Media;

namespace FCAT.Services;

public enum AppTheme { Nebula, Carbon, Photon, Rust }

/// <summary>
/// Live colour theming. The themed palette keys (accent + surfaces + hairlines + glow) are referenced
/// app-wide via <c>DynamicResource</c>; switching a theme replaces those brush/Color resource entries,
/// so every binding re-resolves and the whole UI retints instantly - no relaunch. (WPF freezes the
/// XAML-declared resource brushes, so they can't be mutated in place; we swap the entries instead.)
/// Semantic colours (gold/amber/green/critical, text tiers) stay fixed across themes and remain
/// StaticResource - only the accent + surfaces + hairlines + glow move.
/// </summary>
public static class ThemeService
{
    // Nebula - violet accent over cool near-black surfaces (the default).
    private static readonly Dictionary<string, string> Nebula = new()
    {
        ["EveWindowBg"]      = "#101019",
        ["EvePanelBg"]       = "#191926",
        ["EveRaisedBg"]      = "#23233a",
        ["EveRowAltBg"]      = "#4d1e1e30",
        ["EveHairline"]      = "#2b2b40",
        ["EveHairlineSoft"]  = "#1c1c2b",
        ["EveHairlineStrong"]= "#3a3a54",
        ["EveBorderBrush"]   = "#2b2b40",
        ["EveAccent"]        = "#a98ce0",
        ["EveAccentDim"]     = "#5b5080",
        ["EveAccentBright"]  = "#bda2ea",
        ["EveAccentFaint"]   = "#22a98ce0",
        ["EveBlueBrush"]     = "#8f86d6",
        ["EveBlueDimBrush"]  = "#4a4570",
        ["EveTopBarBrush"]   = "#cc12121d",
        ["EveNavRailBrush"]  = "#990a0d13",
        ["EveGlowTopColor"]  = "#2b1f52",
        ["EveGlowMidColor"]  = "#1a1236",
        ["EveBackdropTopColor"] = "#101019",
        ["EveBackdropMidColor"] = "#0d0d16",
        ["EveBackdropBotColor"] = "#0b0b12",
    };

    // Carbon - warm amber/gold accent over warm near-black surfaces.
    private static readonly Dictionary<string, string> Carbon = new()
    {
        ["EveWindowBg"]      = "#12100f",
        ["EvePanelBg"]       = "#1d1a16",
        ["EveRaisedBg"]      = "#2a2620",
        ["EveRowAltBg"]      = "#4d302518",
        ["EveHairline"]      = "#37312a",
        ["EveHairlineSoft"]  = "#221e18",
        ["EveHairlineStrong"]= "#4a4235",
        ["EveBorderBrush"]   = "#37312a",
        ["EveAccent"]        = "#d99a3f",
        ["EveAccentDim"]     = "#7a5c2e",
        ["EveAccentBright"]  = "#e6b45c",
        ["EveAccentFaint"]   = "#26d99a3f",
        ["EveBlueBrush"]     = "#d4a655",
        ["EveBlueDimBrush"]  = "#6e5a34",
        ["EveTopBarBrush"]   = "#cc1b140d",
        ["EveNavRailBrush"]  = "#99130d06",
        ["EveGlowTopColor"]  = "#3a2a12",
        ["EveGlowMidColor"]  = "#241a0c",
        ["EveBackdropTopColor"] = "#14110c",
        ["EveBackdropMidColor"] = "#0f0d09",
        ["EveBackdropBotColor"] = "#0c0a07",
    };

    // Photon - cool azure accent over blue-tinted near-black (EVE's Photon UI).
    private static readonly Dictionary<string, string> Photon = new()
    {
        ["EveWindowBg"]      = "#0e1117",
        ["EvePanelBg"]       = "#171c25",
        ["EveRaisedBg"]      = "#232b38",
        ["EveRowAltBg"]      = "#4d1e2a3a",
        ["EveHairline"]      = "#28313f",
        ["EveHairlineSoft"]  = "#191f28",
        ["EveHairlineStrong"]= "#38434f",
        ["EveBorderBrush"]   = "#28313f",
        ["EveAccent"]        = "#4d9be0",
        ["EveAccentDim"]     = "#2f5a7e",
        ["EveAccentBright"]  = "#72b4ef",
        ["EveAccentFaint"]   = "#244d9be0",
        ["EveBlueBrush"]     = "#6f9fd0",
        ["EveBlueDimBrush"]  = "#3a5570",
        ["EveTopBarBrush"]   = "#cc10151d",
        ["EveNavRailBrush"]  = "#990a0e17",
        ["EveGlowTopColor"]  = "#163350",
        ["EveGlowMidColor"]  = "#0f2136",
        ["EveBackdropTopColor"] = "#0e1117",
        ["EveBackdropMidColor"] = "#0b0e13",
        ["EveBackdropBotColor"] = "#090b0f",
    };

    // Rust - oxidised orange-red accent over warm brown near-black (Minmatar).
    private static readonly Dictionary<string, string> Rust = new()
    {
        ["EveWindowBg"]      = "#13100e",
        ["EvePanelBg"]       = "#1f1815",
        ["EveRaisedBg"]      = "#2c231d",
        ["EveRowAltBg"]      = "#4d30201a",
        ["EveHairline"]      = "#392e27",
        ["EveHairlineSoft"]  = "#231b16",
        ["EveHairlineStrong"]= "#4c3d33",
        ["EveBorderBrush"]   = "#392e27",
        ["EveAccent"]        = "#c06544",
        ["EveAccentDim"]     = "#6e3a2a",
        ["EveAccentBright"]  = "#d67f5b",
        ["EveAccentFaint"]   = "#28c06544",
        ["EveBlueBrush"]     = "#c08a6a",
        ["EveBlueDimBrush"]  = "#6e4a34",
        ["EveTopBarBrush"]   = "#cc1a120d",
        ["EveNavRailBrush"]  = "#99130c07",
        ["EveGlowTopColor"]  = "#3b1d10",
        ["EveGlowMidColor"]  = "#241009",
        ["EveBackdropTopColor"] = "#13100e",
        ["EveBackdropMidColor"] = "#0f0b09",
        ["EveBackdropBotColor"] = "#0b0807",
    };

    private static Dictionary<string, string> Palette(AppTheme t) => t switch
    {
        AppTheme.Carbon => Carbon,
        AppTheme.Photon => Photon,
        AppTheme.Rust   => Rust,
        _               => Nebula,
    };

    public static AppTheme Current { get; private set; } = AppTheme.Nebula;

    public static AppTheme Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "carbon" => AppTheme.Carbon,
        "photon" => AppTheme.Photon,
        "rust"   => AppTheme.Rust,
        _        => AppTheme.Nebula,
    };

    public static string Name(AppTheme theme) => theme.ToString();

    /// <summary>Retints the shared palette to the given theme. Safe to call before or after the
    /// window is up; a no-op if the app resources aren't available yet.</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var palette = Palette(theme);
        var res = Application.Current?.Resources;
        if (res == null) return;

        foreach (var (key, hex) in palette)
        {
            var color = ParseColor(hex);
            if (key.EndsWith("Color", StringComparison.Ordinal))
            {
                res[key] = color;                       // glow gradient stops (DynamicResource Color)
            }
            else
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();                         // shared, immutable - we swap the entry to re-theme
                res[key] = brush;                       // DynamicResource refs re-resolve to this
            }
        }
    }

    /// <summary>Parses "#rrggbb" or "#aarrggbb".</summary>
    private static Color ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8)
            return Color.FromArgb(
                System.Convert.ToByte(s[..2], 16), System.Convert.ToByte(s[2..4], 16),
                System.Convert.ToByte(s[4..6], 16), System.Convert.ToByte(s[6..8], 16));
        return Color.FromRgb(
            System.Convert.ToByte(s[..2], 16), System.Convert.ToByte(s[2..4], 16), System.Convert.ToByte(s[4..6], 16));
    }
}
