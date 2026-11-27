using System.Text.RegularExpressions;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// admin-ui/app/themes.css: every theme has a dark and a light block, every block defines the same design tokens,
/// and the panel surface of each block is the value that the server uses for the accent check (ClubThemes.SurfaceHex).
/// </summary>
public sealed class ThemeTokensParityTests
{
    /// <summary>The tokens of Task 3 of the themes plan, and the extra ones that globals.css uses.</summary>
    public static readonly string[] RequiredTokens =
    {
        "--bg-abyss", "--bg-dark", "--bg-panel", "--bg-card", "--bg-elevated", "--bg-hover", "--border", "--border-strong",
        "--text-primary", "--text-secondary", "--text-muted", "--accent", "--accent-contrast", "--accent-soft", "--ok", "--warn",
        "--danger", "--info", "--glow-strength", "--radius-sm", "--radius-md", "--font-display", "--font-body", "--font-mono",
        // extra tokens of this project
        "--accent-2", "--accent-text", "--event", "--bg-glass", "--grid-faint", "--on-status", "--on-data", "--scanlines",
        "--radius-pill", "--label-case", "--tracking", "--font-wordmark", "--shape-chip", "--shape-control", "--shape-tile",
        "--shape-card-sm", "--shape-card", "--shape-dialog", "--shape-dot", "--card-shadow", "--decor",
        "--chart-time", "--chart-bar", "--chart-cash", "--chart-card", "--chart-balance", "--chart-bonus", "--chart-debt",
        "--ramp-0", "--ramp-1", "--ramp-2", "--ramp-3", "--ramp-4", "--ramp-5", "--ramp-6", "--ramp-7", "--ramp-8", "--ramp-9",
        "--ramp-ink-low", "--ramp-ink-high",
    };

    internal static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClubManagementSystem.sln")))
                return Path.Combine(dir.FullName, relative);
        }
        throw new InvalidOperationException("Repository root (ClubManagementSystem.sln) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Blocks of themes.css keyed by (theme, mode): token name -> value.</summary>
    internal static Dictionary<(string Theme, string Mode), Dictionary<string, string>> Blocks()
    {
        var css = Regex.Replace(File.ReadAllText(RepoFile("admin-ui/app/themes.css")), @"/\*.*?\*/", "", RegexOptions.Singleline);
        var blocks = new Dictionary<(string, string), Dictionary<string, string>>();
        foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selector = Regex.Match(rule.Groups[1].Value, "\\[data-theme=\"([a-z-]+)\"\\]\\[data-mode=\"(dark|light)\"\\]");
            if (!selector.Success) continue;
            // A component rule such as [data-theme="arcade"][data-mode="dark"] .btn is not a token block
            if (Regex.IsMatch(rule.Groups[1].Value, "\\[data-mode=\"(dark|light)\"\\]\\s*[.#a-z]")) continue;
            var key = (selector.Groups[1].Value, selector.Groups[2].Value);
            Assert.False(blocks.ContainsKey(key), $"two token blocks for {key}");
            blocks[key] = Regex.Matches(rule.Groups[2].Value, @"(--[a-z0-9-]+)\s*:\s*([^;]+);")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        }
        return blocks;
    }

    [Fact]
    public void Every_theme_has_a_dark_and_a_light_block()
    {
        var blocks = Blocks();
        var expected = ClubThemes.All.SelectMany(t => ClubThemes.Modes.Select(m => (t, m))).OrderBy(x => x).ToList();
        Assert.Equal(expected, blocks.Keys.OrderBy(x => x).ToList());
    }

    [Fact]
    public void Every_block_defines_the_same_full_token_list()
    {
        var blocks = Blocks();
        var first = blocks.First();
        foreach (var (key, tokens) in blocks)
        {
            var missing = RequiredTokens.Where(t => !tokens.ContainsKey(t)).ToList();
            Assert.True(missing.Count == 0, $"{key} misses {string.Join(", ", missing)}");
            Assert.True(tokens.Keys.ToHashSet().SetEquals(first.Value.Keys),
                $"{key} and {first.Key} define different tokens: {string.Join(", ", tokens.Keys.Except(first.Value.Keys).Concat(first.Value.Keys.Except(tokens.Keys)))}");
        }
    }

    [Fact]
    public void The_panel_surface_of_every_block_is_the_server_surface()
    {
        foreach (var ((theme, mode), tokens) in Blocks())
            Assert.Equal(ClubThemes.SurfaceHex(theme, mode), tokens["--bg-panel"].ToUpperInvariant());
    }

    [Fact]
    public void Text_colours_of_every_block_pass_WCAG_AA_on_every_surface()
    {
        var surfaces = new[] { "--bg-abyss", "--bg-dark", "--bg-panel", "--bg-card", "--bg-elevated" };
        foreach (var (key, tokens) in Blocks())
            foreach (var text in new[] { "--text-primary", "--text-secondary", "--text-muted", "--accent-text", "--ok", "--warn", "--danger", "--info", "--event" })
                foreach (var surface in surfaces)
                {
                    var fg = tokens[text] == "var(--accent)" ? tokens["--accent"] : tokens[text];
                    var ratio = ColorContrast.Ratio(fg, tokens[surface]);
                    Assert.True(ratio >= 4.5, $"{key}: {text} {fg} on {surface} {tokens[surface]} is {ratio:0.00}:1");
                    // Badges, banners and selected chips put the colour on a soft fill of itself (up to 20 %)
                    if (text is "--text-primary" or "--text-secondary" or "--text-muted") continue;
                    var tint = Mix(fg, tokens[surface], 0.20);
                    var onTint = ColorContrast.Ratio(fg, tint);
                    Assert.True(onTint >= 4.5, $"{key}: {text} {fg} on its 20 % fill over {surface} ({tint}) is {onTint:0.00}:1");
                }
    }

    /// <summary>The colour fg at the given opacity over bg (sRGB, as color-mix(in srgb, fg w, transparent) over bg).</summary>
    internal static string Mix(string fg, string bg, double w)
    {
        int C(string hex, int i) => Convert.ToInt32(hex.Substring(1 + 2 * i, 2), 16);
        return "#" + string.Concat(Enumerable.Range(0, 3).Select(i => ((int)Math.Round(C(fg, i) * w + C(bg, i) * (1 - w))).ToString("X2")));
    }
}
