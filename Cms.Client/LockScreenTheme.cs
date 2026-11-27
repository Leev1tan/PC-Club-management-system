using System.Globalization;
using Cms.Shared;

namespace Cms.Client;

/// <summary>
/// The logic of the lock screen theme without WPF types (Cms.Launcher/ThemeManager applies it): which dictionary a theme
/// uses, the embedded fonts, and the shades of an owner accent. The same rules as the admin UI (app/theme.tsx): text on an
/// accent fill is black or white, and text in the accent colour reaches 4.5:1 on every surface and on a 20 % accent fill.
/// </summary>
public static class LockScreenTheme
{
    /// <summary>Cms.Launcher/Themes/&lt;name&gt;.xaml of the theme (an unknown theme means Cyber).</summary>
    public static string DictionaryName(string? theme) => ClubThemes.Normalize(theme) switch
    {
        ClubThemes.ProDark => "ProDark",
        ClubThemes.Light => "Light",
        ClubThemes.Arcade => "Arcade",
        _ => "Cyber",
    };

    /// <summary>Font families embedded in Cms.Launcher (Fonts/*.ttf, SIL OFL; the licence of each is Fonts/&lt;Family&gt;-OFL.txt).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FontFiles = new Dictionary<string, string[]>
    {
        ["Exo 2"] = new[] { "Exo2-Regular.ttf", "Exo2-Bold.ttf" },
        ["JetBrains Mono"] = new[] { "JetBrainsMono-Regular.ttf", "JetBrainsMono-Bold.ttf" },
        ["Inter"] = new[] { "Inter-Regular.ttf", "Inter-Bold.ttf" },
        ["IBM Plex Sans"] = new[] { "IBMPlexSans-Regular.ttf", "IBMPlexSans-Bold.ttf" },
        ["IBM Plex Mono"] = new[] { "IBMPlexMono-Regular.ttf", "IBMPlexMono-Bold.ttf" },
        ["Press Start 2P"] = new[] { "PressStart2P-Regular.ttf" },
        ["Rubik"] = new[] { "Rubik-Regular.ttf", "Rubik-Bold.ttf" },
    };

    /// <summary>
    /// DecodePixelWidth / DecodePixelHeight for a logo of this pixel size: it fits a box of 640 x 192 and is never
    /// scaled up (null = decode as it is). Only one side is set, so the decoder keeps the aspect ratio.
    /// </summary>
    public static (int? Width, int? Height) LogoDecodeSize(int width, int height, int maxWidth = 640, int maxHeight = 192)
    {
        if (width <= maxWidth && height <= maxHeight) return (null, null);
        return (double)width / maxWidth > (double)height / maxHeight ? (maxWidth, null) : (null, maxHeight);
    }

    public static bool TryAccent(string? accent, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (!ColorContrast.TryParseHex(accent, out var hex)) return false;
        byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        rgb = (At(1), At(3), At(5));
        return true;
    }

    /// <summary>Black or white, whichever has the higher contrast on the accent.</summary>
    public static string OnAccentHex(string accent) =>
        ColorContrast.Ratio("#FFFFFF", accent) > ColorContrast.Ratio("#000000", accent) ? "#FFFFFF" : "#000000";

    /// <summary>The accent moved towards the text colour (in 2 % steps) until it reaches 4.5:1 on every surface and on a 20 % accent fill.</summary>
    public static string AccentTextHex(string accent, IReadOnlyCollection<string> surfaces, string text)
    {
        if (!ColorContrast.TryParseHex(accent, out var a) || !ColorContrast.TryParseHex(text, out var t)) return accent;
        double Worst(string c) => surfaces.Where(s => ColorContrast.TryParseHex(s, out _))
            .SelectMany(s => new[] { ColorContrast.Ratio(c, s), ColorContrast.Ratio(c, Mix(s, c, 0.2)) }).DefaultIfEmpty(21).Min();
        var result = a;
        for (var i = 1; i <= 50 && Worst(result) < 4.5; i++) result = Mix(a, t, i / 50.0);
        return result;
    }

    /// <summary>a moved towards b by w (0..1), as #RRGGBB.</summary>
    public static string Mix(string a, string b, double w)
    {
        int C(string hex, int i) => int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return "#" + string.Concat(Enumerable.Range(0, 3).Select(i => ((int)Math.Round(C(a, i) * (1 - w) + C(b, i) * w)).ToString("X2", CultureInfo.InvariantCulture)));
    }
}
