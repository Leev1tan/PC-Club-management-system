using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// The lock screen theme (Cms.Launcher/Themes/*.xaml), parsed as XML on Linux: the same keys in every theme, the same
/// colours as the default mode of the admin UI theme, no literal colour or font left in the window; the logo file is
/// trusted only with the right hash; the agent downloads the logo only when its hash changes.
/// </summary>
public sealed class LauncherThemeTests
{
    public static readonly string[] Keys =
    {
        "Brush.BgAbyss", "Brush.BgPanel", "Brush.BgCard", "Brush.BgElevated", "Brush.Border", "Brush.Text", "Brush.TextSecondary",
        "Brush.TextMuted", "Brush.Accent", "Brush.AccentContrast", "Brush.Ok", "Brush.Warn", "Brush.Danger", "Font.Display", "Font.Body",
        "Font.Mono", "Radius.Small", "Radius.Medium", "Glow.Opacity",
        // extra keys of this project: top bars, accent text, event colour, the lock overlay, glow colours, the wordmark font
        "Brush.BgDark", "Brush.AccentText", "Brush.Event", "Brush.Overlay", "Color.Accent", "Color.Danger", "Color.Ok", "Color.Warn", "Font.Wordmark",
    };

    /// <summary>Brush keys and the CSS token of app/themes.css with the same colour.</summary>
    private static readonly (string Brush, string Token)[] SameAsCss =
    {
        ("Brush.BgAbyss", "--bg-abyss"), ("Brush.BgDark", "--bg-dark"), ("Brush.BgPanel", "--bg-panel"), ("Brush.BgCard", "--bg-card"),
        ("Brush.BgElevated", "--bg-elevated"), ("Brush.Border", "--border"), ("Brush.Text", "--text-primary"), ("Brush.TextSecondary", "--text-secondary"),
        ("Brush.TextMuted", "--text-muted"), ("Brush.Accent", "--accent"), ("Brush.AccentText", "--accent"), ("Brush.AccentContrast", "--accent-contrast"),
        ("Brush.Ok", "--ok"), ("Brush.Warn", "--warn"), ("Brush.Danger", "--danger"), ("Brush.Event", "--event"),
    };

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, XElement> Resources(string theme)
    {
        var doc = XDocument.Load(ThemeTokensParityTests.RepoFile($"Cms.Launcher/Themes/{LockScreenTheme.DictionaryName(theme)}.xaml"));
        return doc.Root!.Elements().Where(e => e.Attribute(X + "Key") != null).ToDictionary(e => e.Attribute(X + "Key")!.Value);
    }

    private static string BrushHex(XElement e) => e.Attribute("Color")!.Value.ToUpperInvariant();

    [Fact]
    public void Every_theme_dictionary_defines_exactly_the_keys()
    {
        foreach (var theme in ClubThemes.All)
            Assert.Equal(Keys.OrderBy(k => k), Resources(theme).Keys.OrderBy(k => k));
    }

    [Fact]
    public void The_lock_screen_colours_are_the_admin_ui_colours_of_the_default_mode()
    {
        var css = ThemeTokensParityTests.Blocks();
        foreach (var theme in ClubThemes.All)
        {
            var res = Resources(theme);
            var mode = ClubThemes.DefaultMode(theme);
            Assert.Equal(ClubThemes.SurfaceHex(theme, mode), BrushHex(res["Brush.BgPanel"]));
            var tokens = css[(theme, mode)];
            foreach (var (brush, token) in SameAsCss)
                Assert.True(BrushHex(res[brush]) == tokens[token].ToUpperInvariant(), $"{theme} {brush} {BrushHex(res[brush])} != {token} {tokens[token]}");
            Assert.Equal(BrushHex(res["Brush.Accent"]), res["Color.Accent"].Value.Trim().ToUpperInvariant());
            Assert.Equal(BrushHex(res["Brush.Danger"]), res["Color.Danger"].Value.Trim().ToUpperInvariant());
            // Fonts are the embedded files (Fonts/*.ttf) and exist with their licence
            foreach (var key in new[] { "Font.Display", "Font.Body", "Font.Mono", "Font.Wordmark" })
            {
                var family = Regex.Match(res[key].Value, "#(.+)$").Groups[1].Value.Trim();
                Assert.True(LockScreenTheme.FontFiles.ContainsKey(family), $"{theme} {key}: {family} is not an embedded font");
                foreach (var file in LockScreenTheme.FontFiles[family])
                    Assert.True(File.Exists(ThemeTokensParityTests.RepoFile("Cms.Launcher/Fonts/" + file)), file);
            }
        }
        foreach (var licence in LockScreenTheme.FontFiles.Values.SelectMany(f => f).Select(f => Regex.Replace(f, "-[A-Za-z]+\\.ttf$", "-OFL.txt")).Distinct())
            Assert.Contains("SIL OPEN FONT LICENSE", File.ReadAllText(ThemeTokensParityTests.RepoFile("Cms.Launcher/Fonts/" + licence)).ToUpperInvariant());
    }

    [Fact]
    public void MainWindow_has_no_literal_colour_or_font()
    {
        var xaml = File.ReadAllText(ThemeTokensParityTests.RepoFile("Cms.Launcher/MainWindow.xaml"));
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\\b", xaml);
        Assert.DoesNotMatch("FontFamily=\"(?!\\{DynamicResource )", xaml);
        Assert.DoesNotMatch("Property=\"FontFamily\" Value=\"(?!\\{DynamicResource )", xaml);
        // The code-behind builds the lock overlay, the order panel and dialogs: theme resources there too
        foreach (var file in Directory.GetFiles(ThemeTokensParityTests.RepoFile("Cms.Launcher"), "*.cs"))
        {
            if (Path.GetFileName(file) == "MainWindow.Games.cs") continue; // game tile colours are data (catalog)
            var code = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(code, "Color\\.FromA?rgb\\(|new FontFamily\\(\"|Brushes\\.(?!Transparent)"), $"{Path.GetFileName(file)} has a literal colour or font");
        }
    }

    [Theory]
    [InlineData("cyber", "Cyber")]
    [InlineData("PRO-DARK", "ProDark")]
    [InlineData("light", "Light")]
    [InlineData("arcade", "Arcade")]
    [InlineData("neon", "Cyber")]
    [InlineData(null, "Cyber")]
    public void A_theme_maps_to_its_dictionary(string? theme, string name) => Assert.Equal(name, LockScreenTheme.DictionaryName(theme));

    [Fact]
    public void Owner_accent_shades_read_well()
    {
        Assert.False(LockScreenTheme.TryAccent("javascript:", out _));
        Assert.True(LockScreenTheme.TryAccent("#e0457b", out var rgb));
        Assert.Equal(((byte)0xE0, (byte)0x45, (byte)0x7B), rgb);
        // Text on the accent fill: black or white, whichever reads better
        Assert.Equal("#000000", LockScreenTheme.OnAccentHex("#FFD23F"));
        Assert.Equal("#FFFFFF", LockScreenTheme.OnAccentHex("#1F5FD6"));
        // Text in the accent colour reaches 4.5:1 on every surface of the theme
        var surfaces = new[] { "#050508", "#0D0D15", "#111119" };
        var text = LockScreenTheme.AccentTextHex("#7A2E8F", surfaces, "#E8E8F0");
        Assert.All(surfaces, s => Assert.True(ColorContrast.Ratio(text, s) >= 4.5, $"{text} on {s}"));
        Assert.Equal("#00F0FF", LockScreenTheme.AccentTextHex("#00F0FF", surfaces, "#E8E8F0"));
    }

    // ── Logo file ──

    private static readonly byte[] Png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes("IHDR fake image data for the hash")).ToArray();
    private static readonly byte[] Jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccms-logo-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* temp */ } }
    }

    [Fact]
    public void LogoLoader_trusts_only_a_png_or_jpeg_with_the_right_hash()
    {
        using var dir = new TempDir();
        var path = dir.File(AgentFiles.ClubLogoFile);
        File.WriteAllBytes(path, Png);
        Assert.Equal(Png, LogoLoader.ReadTrusted(path, Sha(Png)));
        Assert.Equal(Png, LogoLoader.ReadTrusted(path, Sha(Png).ToUpperInvariant()));
        Assert.Null(LogoLoader.ReadTrusted(path, Sha(Jpeg)));          // wrong hash (a half-written or old file)
        Assert.Null(LogoLoader.ReadTrusted(path, "../x"));            // not a hash
        Assert.Null(LogoLoader.ReadTrusted(dir.File("missing.img"), Sha(Png)));
        File.WriteAllBytes(path, Jpeg);
        Assert.Equal(Jpeg, LogoLoader.ReadTrusted(path, Sha(Jpeg)));
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllBytes(path, svg);
        Assert.Null(LogoLoader.ReadTrusted(path, Sha(svg)));          // right hash, but not an image the lock screen shows
        var big = new byte[524289];
        Png.CopyTo(big, 0);
        File.WriteAllBytes(path, big);
        Assert.Null(LogoLoader.ReadTrusted(path, Sha(big)));          // over 512 KB
        Assert.Equal(524288, LogoLoader.MaxBytes);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public byte[]? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal("/api/public/theme/logo", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body ?? Array.Empty<byte>()) });
        }
    }

    [Fact]
    public async Task The_agent_downloads_the_logo_when_the_hash_changes_and_removes_it_when_it_is_gone()
    {
        using var dir = new TempDir();
        var server = new FakeServer { Body = Png };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://club-server:5081/") };
        var path = dir.File(AgentFiles.ClubLogoFile);

        Assert.Equal(ClubLogoSync.Result.Downloaded, await ClubLogoSync.SyncAsync(http, dir.Path, Sha(Png), default));
        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.Equal(ClubLogoSync.Result.Unchanged, await ClubLogoSync.SyncAsync(http, dir.Path, Sha(Png), default));
        Assert.Equal(1, server.Requests);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));

        // The server answers with other bytes than the heartbeat promised: the old file stays
        server.Body = Jpeg;
        Assert.Equal(ClubLogoSync.Result.Failed, await ClubLogoSync.SyncAsync(http, dir.Path, Sha(Encoding.ASCII.GetBytes("other")), default));
        Assert.Equal(Png, File.ReadAllBytes(path));
        // Not 200, or too big: nothing written
        server.Status = HttpStatusCode.NotFound;
        Assert.Equal(ClubLogoSync.Result.Failed, await ClubLogoSync.SyncAsync(http, dir.Path, Sha(Jpeg), default));
        server.Status = HttpStatusCode.OK;
        server.Body = new byte[LogoLoader.MaxBytes + 1];
        Assert.Equal(ClubLogoSync.Result.Failed, await ClubLogoSync.SyncAsync(http, dir.Path, Sha(server.Body), default));
        Assert.Equal(Png, File.ReadAllBytes(path));

        // A new logo replaces the file; no logo removes it
        server.Body = Jpeg;
        Assert.Equal(ClubLogoSync.Result.Downloaded, await ClubLogoSync.SyncAsync(http, dir.Path, Sha(Jpeg), default));
        Assert.Equal(Jpeg, File.ReadAllBytes(path));
        Assert.Equal(ClubLogoSync.Result.Removed, await ClubLogoSync.SyncAsync(http, dir.Path, null, default));
        Assert.False(File.Exists(path));
        Assert.Equal(ClubLogoSync.Result.Unchanged, await ClubLogoSync.SyncAsync(http, dir.Path, null, default));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    // ── Review fixes: a stalled download, an undecodable logo, the decode size ──

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct); // a half-open connection: no byte, no end
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }

    [Fact]
    public async Task A_stalled_logo_download_gives_up_after_the_time_limit()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new StalledServer()) { BaseAddress = new Uri("http://club-server:5081/"), Timeout = Timeout.InfiniteTimeSpan };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await ClubLogoSync.SyncAsync(http, dir.Path, Sha(Png), default, TimeSpan.FromMilliseconds(300));
        Assert.Equal(ClubLogoSync.Result.Failed, result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Equal(TimeSpan.FromSeconds(30), ClubLogoSync.DefaultTimeLimit);
        // The service stop token still stops it as a cancellation
        using var stop = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClubLogoSync.SyncAsync(http, dir.Path, Sha(Png), stop.Token, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(null, null, false, false, LogoDisplay.Action.Keep)]
    [InlineData(null, "a", false, false, LogoDisplay.Action.ShowText)]      // the club removed the logo
    [InlineData("a", "a", true, true, LogoDisplay.Action.Keep)]
    [InlineData("b", "a", false, false, LogoDisplay.Action.Keep)]          // the agent still downloads: the old logo stays
    [InlineData("b", "a", true, true, LogoDisplay.Action.ShowImage)]
    [InlineData("b", "a", true, false, LogoDisplay.Action.ShowText)]       // the right bytes, but Windows cannot decode them
    [InlineData("b", null, true, false, LogoDisplay.Action.ShowText)]
    public void The_lock_screen_never_keeps_a_replaced_logo_that_cannot_be_decoded(string? wanted, string? shown, bool trusted, bool decoded, LogoDisplay.Action action) =>
        Assert.Equal(action, LogoDisplay.Decide(wanted, shown, trusted, decoded));

    private static byte[] PngHeader(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    private static byte[] JpegHeader(int w, int h) => new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9,
    };

    [Fact]
    public void LogoLoader_reads_the_pixel_size_from_the_header()
    {
        Assert.True(LogoLoader.TryReadSize(PngHeader(300, 120), out var w, out var h));
        Assert.Equal((300, 120), (w, h));
        Assert.True(LogoLoader.TryReadSize(JpegHeader(1024, 768), out w, out h));
        Assert.Equal((1024, 768), (w, h));
        Assert.False(LogoLoader.TryReadSize(Encoding.UTF8.GetBytes("<svg/>"), out _, out _));
        Assert.False(LogoLoader.TryReadSize(PngHeader(0, 10), out _, out _));
        Assert.False(LogoLoader.TryReadSize(PngHeader(300, 120)[..20], out _, out _));
    }

    [Theory]
    [InlineData(300, 120, null, null)]   // fits the box: decoded as it is, never scaled up
    [InlineData(20, 20, null, null)]
    [InlineData(1000, 400, null, 192)]
    [InlineData(1024, 1024, null, 192)]
    [InlineData(1024, 1, 640, null)]     // a very wide logo: limited by the width, not blown up to 192 px high
    [InlineData(640, 192, null, null)]
    public void The_lock_screen_decodes_the_logo_at_most_640_x_192(int w, int h, int? decodeWidth, int? decodeHeight) =>
        Assert.Equal((decodeWidth, decodeHeight), LockScreenTheme.LogoDecodeSize(w, h));
}
