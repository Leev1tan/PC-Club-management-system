using System.Windows;
using System.Windows.Media;
using Cms.Client;

namespace Cms.Launcher;

/// <summary>
/// Switches the lock screen theme at run time: the dictionary Themes/&lt;Name&gt;.xaml goes to
/// Application.Resources.MergedDictionaries[0], and every {DynamicResource …} of the window follows without a restart.
/// An owner accent replaces Brush.Accent and Color.Accent; the text shade (Brush.AccentText) and the text on accent fills
/// (Brush.AccentContrast) are computed like in the admin UI (Cms.Client.LockScreenTheme).
/// </summary>
public static class ThemeManager
{
    public static void Apply(Application app, string theme, string? accentHex)
    {
        var name = LockScreenTheme.DictionaryName(theme);
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Cms.Launcher;component/Themes/{name}.xaml", UriKind.Absolute) };
        if (LockScreenTheme.TryAccent(accentHex, out _))
        {
            var hex = accentHex!.Trim().ToUpperInvariant();
            var surfaces = new[] { "Brush.BgAbyss", "Brush.BgDark", "Brush.BgPanel", "Brush.BgCard", "Brush.BgElevated" }.Select(k => Hex(dict[k])).ToList();
            dict["Brush.Accent"] = Frozen(hex);
            dict["Color.Accent"] = Parse(hex);
            dict["Brush.AccentText"] = Frozen(LockScreenTheme.AccentTextHex(hex, surfaces, Hex(dict["Brush.Text"])));
            dict["Brush.AccentContrast"] = Frozen(LockScreenTheme.OnAccentHex(hex));
        }
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        return brush;
    }

    private static string Hex(object resource) =>
        resource is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "#000000";
}
