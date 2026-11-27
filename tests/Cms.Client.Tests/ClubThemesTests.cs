using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Club themes (cyber, pro-dark, light, arcade), the WCAG contrast ratio and the theme fields of the client settings.</summary>
public sealed class ClubThemesTests
{
    [Theory]
    [InlineData("cyber", "cyber")]
    [InlineData("PRO-DARK", "pro-dark")]
    [InlineData(" light ", "light")]
    [InlineData("arcade", "arcade")]
    [InlineData("neon", "cyber")]
    [InlineData("", "cyber")]
    [InlineData(null, "cyber")]
    public void Normalize_maps_unknown_to_cyber(string? input, string expected) => Assert.Equal(expected, ClubThemes.Normalize(input));

    [Fact]
    public void Find_returns_null_for_an_unknown_theme()
    {
        Assert.Null(ClubThemes.Find("neon"));
        Assert.Null(ClubThemes.Find(null));
        Assert.Equal("arcade", ClubThemes.Find("Arcade"));
        Assert.Equal(new[] { "cyber", "pro-dark", "light", "arcade" }, ClubThemes.All);
    }

    [Fact]
    public void Default_modes()
    {
        Assert.Equal("dark", ClubThemes.DefaultMode("cyber"));
        Assert.Equal("dark", ClubThemes.DefaultMode("pro-dark"));
        Assert.Equal("light", ClubThemes.DefaultMode("light"));
        Assert.Equal("dark", ClubThemes.DefaultMode("arcade"));
        Assert.Equal("dark", ClubThemes.DefaultMode("neon"));
    }

    [Fact]
    public void Every_theme_has_a_surface_in_both_modes()
    {
        foreach (var theme in ClubThemes.All)
            foreach (var mode in ClubThemes.Modes)
                Assert.Matches("^#[0-9A-F]{6}$", ClubThemes.SurfaceHex(theme, mode));
        Assert.Equal("#0D0D15", ClubThemes.SurfaceHex("cyber", "dark"));
        // A dark mode has a dark surface, a light mode a light one
        foreach (var theme in ClubThemes.All)
            Assert.True(ColorContrast.Ratio(ClubThemes.SurfaceHex(theme, "light"), ClubThemes.SurfaceHex(theme, "dark")) > 10, theme);
    }

    [Fact]
    public void Contrast_black_white_is_21() => Assert.Equal(21.0, ColorContrast.Ratio("#000000", "#FFFFFF"), 1);

    [Fact]
    public void Contrast_is_symmetric_and_one_for_the_same_colour()
    {
        Assert.Equal(1.0, ColorContrast.Ratio("#777777", "#777777"), 3);
        // WCAG example: #767676 on white is 4.54:1
        Assert.Equal(4.54, ColorContrast.Ratio("#FFFFFF", "#767676"), 2);
        Assert.Equal(ColorContrast.Ratio("#123456", "#ABCDEF"), ColorContrast.Ratio("#ABCDEF", "#123456"), 6);
    }

    [Theory]
    [InlineData("#00f0ff", true, "#00F0FF")]
    [InlineData(" #00F0FF ", true, "#00F0FF")]
    [InlineData("00f0ff", false, "")]
    [InlineData("#0f0", false, "")]
    [InlineData("#00f0ff00", false, "")]
    [InlineData("#GGGGGG", false, "")]
    [InlineData("red", false, "")]
    [InlineData(null, false, "")]
    public void Hex_parse(string? s, bool ok, string norm)
    {
        Assert.Equal(ok, ColorContrast.TryParseHex(s, out var n));
        if (ok) Assert.Equal(norm, n);
    }

    [Fact]
    public void An_accent_must_fit_both_modes_of_the_theme()
    {
        // Bright cyan: fine on the dark surface, unreadable on the light one
        Assert.True(ColorContrast.Ratio("#00F0FF", ClubThemes.SurfaceHex("cyber", "dark")) >= 3);
        Assert.True(ColorContrast.Ratio("#00F0FF", ClubThemes.SurfaceHex("cyber", "light")) < 3);
        Assert.False(ClubThemes.AccentFits("cyber", "#00F0FF"));
        // A middle tone passes on both
        Assert.True(ClubThemes.AccentFits("cyber", "#E0457B"));
        Assert.True(ClubThemes.AccentContrast("cyber", "#E0457B") >= ClubThemes.MinAccentContrast);
    }

    [Fact]
    public void Old_client_settings_json_without_theme_means_cyber()
    {
        var s = ClientSettings.Parse("{\"language\":\"en\",\"currency\":\"EUR\"}")!;
        Assert.Equal("cyber", s.Theme);
        Assert.Null(s.Accent);
        Assert.Null(s.LogoSha256);
    }

    [Fact]
    public void Bad_accent_and_bad_logo_hash_are_dropped_by_Normalized()
    {
        var s = ClientSettings.Parse("{\"theme\":\"arcade\",\"accent\":\"javascript:1\",\"logoSha256\":\"../x\"}")!;
        Assert.Equal("arcade", s.Theme);
        Assert.Null(s.Accent);
        Assert.Null(s.LogoSha256);
    }

    [Fact]
    public void Good_values_are_kept_and_normalized()
    {
        var hash = new string('A', 64);
        var s = ClientSettings.Parse($"{{\"theme\":\"PRO-DARK\",\"accent\":\"#e0457b\",\"logoSha256\":\"{hash}\"}}")!;
        Assert.Equal("pro-dark", s.Theme);
        Assert.Equal("#E0457B", s.Accent);
        Assert.Equal(new string('a', 64), s.LogoSha256);
        Assert.Equal("cyber", ClientSettings.Parse("{\"theme\":\"neon\"}")!.Theme);
    }

    [Fact]
    public void SameAs_sees_a_theme_change()
    {
        Assert.False(new ClientSettings { Theme = "cyber" }.SameAs(new ClientSettings { Theme = "light" }));
        Assert.False(new ClientSettings { Accent = "#E0457B" }.SameAs(new ClientSettings()));
        Assert.False(new ClientSettings { LogoSha256 = new string('a', 64) }.SameAs(new ClientSettings()));
        Assert.True(new ClientSettings { Theme = "light", Accent = "#E0457B" }.SameAs(new ClientSettings { Theme = "light", Accent = "#E0457B" }));
    }

    [Fact]
    public void Theme_fields_survive_the_json_round_trip()
    {
        var hash = new string('b', 64);
        var json = new ClientSettings { Language = "en", Theme = "arcade", Accent = "#E0457B", LogoSha256 = hash }.ToJson();
        var back = ClientSettings.Parse(json)!;
        Assert.Equal(("arcade", "#E0457B", hash), (back.Theme, back.Accent, back.LogoSha256));
    }

    /// <summary>Windows test 2026-11-27, defect 9: after an upgrade client-settings.json had no "theme" until the first change.</summary>
    [Fact]
    public void A_settings_file_of_an_older_agent_is_written_again_when_it_lacks_a_field()
    {
        var received = new ClientSettings { Language = "uk", Currency = "UAH", Theme = "cyber", ReceivedUtc = DateTimeOffset.UnixEpoch };
        const string old = "{\"language\":\"uk\",\"currency\":\"UAH\",\"receivedUtc\":\"2026-11-27T10:00:00+00:00\"}";
        Assert.True(received.SameAs(ClientSettings.Parse(old)));         // the same values ...
        Assert.True(ClientSettings.LacksFields(old, received));            // ... but no "theme": written again
        Assert.False(ClientSettings.LacksFields(received.ToJson(), received));
        Assert.False(ClientSettings.LacksFields("{\"Language\":\"uk\",\"CURRENCY\":\"UAH\",\"theme\":\"cyber\",\"receivedUtc\":null}", received));
        // Null values (no accent, no logo) are not required
        var withLogo = new ClientSettings { Theme = "light", Accent = "#E0457B", LogoSha256 = new string('a', 64) };
        Assert.True(ClientSettings.LacksFields("{\"language\":\"uk\",\"currency\":\"UAH\",\"theme\":\"light\"}", withLogo)); // no accent, no logo key
        Assert.False(ClientSettings.LacksFields(withLogo.ToJson(), withLogo));
        Assert.True(ClientSettings.LacksFields(null, received));
        Assert.True(ClientSettings.LacksFields("[]", received));
        Assert.True(ClientSettings.LacksFields("not json", received));
    }
}
