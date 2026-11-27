using System.Globalization;

namespace Cms.Shared;

/// <summary>
/// Club themes: one look for the admin UI, the public booking page and the lock screens (club setting club.theme).
/// The ids are fixed: cyber (default), pro-dark, light, arcade. Every theme has a dark and a light mode; staff may
/// switch only their own admin panel between the modes. The surface colours below are the --bg-panel values of
/// admin-ui/app/themes.css and the Brush.BgPanel values of Cms.Launcher/Themes/*.xaml (tests keep them equal).
/// </summary>
public static class ClubThemes
{
    public const string Cyber = "cyber";
    public const string ProDark = "pro-dark";
    public const string Light = "light";
    public const string Arcade = "arcade";

    public const string DarkMode = "dark";
    public const string LightMode = "light";

    /// <summary>An owner accent must reach this contrast ratio against the surface of both modes of the theme.</summary>
    public const double MinAccentContrast = 3.0;

    public static readonly IReadOnlyList<string> All = new[] { Cyber, ProDark, Light, Arcade };
    public static readonly IReadOnlyList<string> Modes = new[] { DarkMode, LightMode };

    private static readonly IReadOnlyDictionary<(string Theme, string Mode), string> Surfaces = new Dictionary<(string, string), string>
    {
        [(Cyber, DarkMode)] = "#0D0D15",
        [(Cyber, LightMode)] = "#F7F8FC",
        [(ProDark, DarkMode)] = "#171A20",
        [(ProDark, LightMode)] = "#FFFFFF",
        [(Light, LightMode)] = "#FFFEFB",
        [(Light, DarkMode)] = "#1E1D1A",
        [(Arcade, DarkMode)] = "#1D1240",
        [(Arcade, LightMode)] = "#FFFBEF",
    };

    /// <summary>The theme id (any case, spaces around it are ignored); null for an unknown theme.</summary>
    public static string? Find(string? theme)
    {
        var id = theme?.Trim().ToLowerInvariant();
        return id != null && All.Contains(id) ? id : null;
    }

    /// <summary>The theme, or cyber when it is missing or unknown (a newer server, a manual database edit).</summary>
    public static string Normalize(string? theme) => Find(theme) ?? Cyber;

    /// <summary>The mode a theme starts in: light for the Light theme, dark for the others.</summary>
    public static string DefaultMode(string? theme) => Normalize(theme) == Light ? LightMode : DarkMode;

    /// <summary>The panel surface colour (#RRGGBB) of the theme in the mode (an unknown mode means the default mode).</summary>
    public static string SurfaceHex(string? theme, string? mode)
    {
        var id = Normalize(theme);
        var m = mode?.Trim().ToLowerInvariant();
        if (m != DarkMode && m != LightMode) m = DefaultMode(id);
        return Surfaces[(id, m)];
    }

    /// <summary>The lower of the two contrast ratios of the accent against the surfaces of both modes.</summary>
    public static double AccentContrast(string? theme, string accentHex) =>
        Modes.Min(mode => ColorContrast.Ratio(accentHex, SurfaceHex(theme, mode)));

    /// <summary>True when the accent is readable as a UI colour (3:1) in both modes of the theme.</summary>
    public static bool AccentFits(string? theme, string accentHex) => AccentContrast(theme, accentHex) >= MinAccentContrast;
}

/// <summary>WCAG 2.x contrast ratio of two sRGB colours written as #RRGGBB.</summary>
public static class ColorContrast
{
    /// <summary>True for exactly "#RRGGBB" (hex digits in any case, spaces around it ignored); normalized is upper case.</summary>
    public static bool TryParseHex(string? s, out string normalized)
    {
        normalized = string.Empty;
        var v = s?.Trim();
        if (v == null || v.Length != 7 || v[0] != '#') return false;
        for (var i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(v[i])) return false;
        normalized = v.ToUpperInvariant();
        return true;
    }

    /// <summary>(L1 + 0.05) / (L2 + 0.05) with L1 the lighter relative luminance: 1 (same colour) to 21 (black on white).</summary>
    public static double Ratio(string hexA, string hexB)
    {
        var a = Luminance(hexA);
        var b = Luminance(hexB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Relative luminance (WCAG 2.x) of a #RRGGBB colour.</summary>
    public static double Luminance(string hex)
    {
        if (!TryParseHex(hex, out var h)) throw new ArgumentException($"Not a #RRGGBB colour: {hex}", nameof(hex));
        double Channel(int start)
        {
            var c = int.Parse(h.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
