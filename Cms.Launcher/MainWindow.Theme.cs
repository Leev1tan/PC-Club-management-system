using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

/// <summary>Theme resources for elements that the code creates (the keys of Themes/*.xaml).</summary>
internal static class Themed
{
    /// <summary>Text colour and font from the club theme; both follow a theme switch at run time.</summary>
    public static T Text<T>(this T element, string brushKey, string fontKey = "Font.Body") where T : FrameworkElement
    {
        element.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        element.SetResourceReference(TextElement.FontFamilyProperty, fontKey);
        return element;
    }
}

// The club theme on the lock screen: the agent writes theme, accent and logo hash to client-settings.json (heartbeat);
// RefreshClientSettings calls ApplyTheme every 2 s. A theme change needs no restart: ThemeManager swaps the dictionary
// and every {DynamicResource} follows. The logo is shown only when its bytes have the hash of the settings and Windows
// can decode them; otherwise the club name text stays.
public partial class MainWindow
{
    private readonly List<(DropShadowEffect Effect, string ColorKey)> _glows = new();
    private string? _themeApplied;
    private string? _logoShown;
    private string? _logoTried;
    private DateTimeOffset _logoTriedUtc;

    /// <summary>A glow in a colour of the theme (Color.*); its strength is Glow.Opacity of the theme (0: no glow).</summary>
    private void Glow(UIElement element, string colorKey, double blur)
    {
        var effect = new DropShadowEffect { BlurRadius = blur, ShadowDepth = 0 };
        _glows.Add((effect, colorKey));
        element.Effect = effect;
        ApplyGlow(effect, colorKey);
    }

    private void ApplyGlow(DropShadowEffect effect, string colorKey)
    {
        if (TryFindResource(colorKey) is Color color) effect.Color = color;
        effect.Opacity = TryFindResource("Glow.Opacity") is double opacity ? opacity : 0;
    }

    private void ApplyTheme(ClientSettings settings)
    {
        var key = settings.Theme + "|" + settings.Accent;
        if (key != _themeApplied)
        {
            try
            {
                ThemeManager.Apply(Application.Current, settings.Theme, settings.Accent);
                _themeApplied = key;
            }
            catch
            {
                // The last theme stays (the dictionaries are compiled into the exe, so this is not expected)
            }
            foreach (var (effect, colorKey) in _glows) ApplyGlow(effect, colorKey);
        }
        RefreshLogo(settings.LogoSha256);
    }

    private void RefreshLogo(string? sha256)
    {
        if (sha256 == _logoShown) return;
        if (sha256 == null)
        {
            ShowLogo(null, null);
            return;
        }
        // The agent may still download the new logo: try again every 10 s; the old logo stays until then
        var now = DateTimeOffset.UtcNow;
        if (sha256 == _logoTried && now - _logoTriedUtc < TimeSpan.FromSeconds(10)) return;
        _logoTried = sha256;
        _logoTriedUtc = now;
        var path = Path.Combine(AgentFiles.Directory, AgentFiles.ClubLogoFile);
        var bytes = File.Exists(path) && IsTrustedAgentFile(path) ? LogoLoader.ReadTrusted(path, sha256) : null;
        var image = bytes != null && LogoLoader.TryReadSize(bytes, out var width, out var height)
            ? Decode(bytes, LockScreenTheme.LogoDecodeSize(width, height))
            : null;
        // The right bytes that Windows cannot decode: the club name text, never the replaced logo
        switch (LogoDisplay.Decide(sha256, _logoShown, bytes != null, image != null))
        {
            case LogoDisplay.Action.ShowImage: ShowLogo(image, sha256); break;
            case LogoDisplay.Action.ShowText: ShowLogo(null, sha256); break;
        }
    }

    /// <summary>
    /// The image at a bounded size (at most 640 x 192, never scaled up), or null when Windows cannot decode the bytes.
    /// </summary>
    private static BitmapSource? Decode(byte[] bytes, (int? Width, int? Height) size)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (size.Width is { } w) image.DecodePixelWidth = w;
            if (size.Height is { } h) image.DecodePixelHeight = h;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void ShowLogo(BitmapSource? image, string? sha256)
    {
        _logoShown = sha256;
        var hasLogo = image != null;
        LoginLogo.Source = image;
        TopLogo.Source = image;
        LoginLogo.Visibility = TopLogo.Visibility = hasLogo ? Visibility.Visible : Visibility.Collapsed;
        LoginWordmark.Visibility = TopWordmark.Visibility = TopIcon.Visibility = hasLogo ? Visibility.Collapsed : Visibility.Visible;
    }
}
