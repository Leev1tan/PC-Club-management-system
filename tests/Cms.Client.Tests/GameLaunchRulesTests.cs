using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>The rules that the server checks on save and the lock screen checks again before a start.</summary>
public sealed class GameLaunchRulesTests
{
    [Theory]
    [InlineData(@"C:\Games\CS2\cs2.exe", true)]
    [InlineData(@"D:\Riot Games\Riot Client\RiotClientServices.exe", true)]
    [InlineData(@"%LOCALAPPDATA%\Discord\Update.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", true)]
    [InlineData(@"cs2.exe", false)]
    [InlineData(@"\\server\share\game.exe", false)]
    [InlineData(@"C:\Games\..\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Games\.\cs2.exe", false)]
    [InlineData(@"C:\Games\cs2.exe:evil.exe", false)]
    [InlineData("C:\\Games\\c\"s2.exe", false)]
    [InlineData(@"C:\Games\*.exe", false)]
    [InlineData(@"C:\Games\game.bat", false)]
    [InlineData(@"%ProgramFiles(x86)%\Steam\steam.exe", false)]
    public void Programs_need_an_absolute_windows_path_to_an_exe(string path, bool ok) =>
        Assert.Equal(ok, GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Exe, path)) == null);

    [Theory]
    [InlineData("steam://rungameid/730", true)]
    [InlineData("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true", true)]
    [InlineData("battlenet://Pro", true)]
    [InlineData("STEAM://rungameid/570", true)]
    [InlineData("https://store.steampowered.com", false)]
    [InlineData("http://example.com", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("steam://run/730 -console", false)]
    [InlineData("steam://run/730\"&calc", false)]
    [InlineData("", false)]
    public void Links_need_a_game_launcher_scheme(string url, bool ok) =>
        Assert.Equal(ok, GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Url, LaunchUrl: url)) == null);

    [Fact]
    public void Arguments_working_directory_steam_id_and_kind_are_checked()
    {
        Assert.Equal("game_bad_args", GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Exe, @"C:\G\g.exe", "a\r\nb")));
        Assert.Equal("game_bad_args", GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Exe, @"C:\G\g.exe", new string('x', 501))));
        Assert.Equal("game_bad_workdir", GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Exe, @"C:\G\g.exe", null, "relative")));
        Assert.Null(GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Exe, @"C:\G\g.exe", "-novid", @"C:\G")));
        Assert.Equal("game_bad_steam_id", GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 0)));
        Assert.Equal("game_bad_steam_id", GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Steam)));
        Assert.Null(GameLaunchRules.Validate(new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 730)));
        Assert.Equal("game_bad_kind", GameLaunchRules.Validate(new GameLaunch("cmd", @"C:\G\g.exe")));
        Assert.Equal("steam://rungameid/730", GameLaunchRules.SteamUrl(730));
    }

    [Theory]
    [InlineData("cs2.exe", "cs2")]
    [InlineData("VALORANT-Win64-Shipping", "VALORANT-Win64-Shipping")]
    [InlineData("League of Legends", "League of Legends")]
    [InlineData(@"C:\x\cs2.exe", null)]
    [InlineData("a|b", null)]
    [InlineData(" ", null)]
    public void Process_names_are_plain_file_names(string name, string? expected) =>
        Assert.Equal(expected, GameLaunchRules.NormalizeProcessName(name));

    [Theory]
    [InlineData("Counter-Strike 2", "CS")]
    [InlineData("Dota 2", "D2")]
    [InlineData("GTA V", "GTA")]
    [InlineData("PUBG: BATTLEGROUNDS", "PUBG")]
    [InlineData("CS2", "CS2")]
    [InlineData("Fortnite", "Fo")]
    [InlineData("Apex Legends", "AL")]
    [InlineData("EA SPORTS FC 25", "EA")]
    [InlineData("Ігри", "Іг")]
    [InlineData("!!!", "?")]
    public void Tiles_show_initials(string name, string initials) => Assert.Equal(initials, GameTile.Initials(name));

    [Fact]
    public void The_default_colour_depends_only_on_the_name()
    {
        Assert.Equal(GameTile.DefaultColor("Dota 2"), GameTile.DefaultColor(" dota 2 "));
        Assert.Contains(GameTile.DefaultColor("Counter-Strike 2"), GameTile.Palette);
        Assert.True(GameTile.Palette.Select(GameTile.IsColor).All(x => x));
        Assert.False(GameTile.IsColor("#12345"));
        Assert.False(GameTile.IsColor("red"));
    }

    [Fact]
    public void The_catalog_file_round_trips()
    {
        var file = new CatalogFile
        {
            ETag = "\"abc\"",
            Games = new[] { new CatalogGame { Id = Guid.NewGuid(), Name = "Dota 2", Kind = GameLaunchKinds.Steam, SteamAppId = 570, Initials = "D2" } }
        };
        var back = CatalogFile.Parse(file.ToJson())!;
        Assert.Equal("\"abc\"", back.ETag);
        var game = Assert.Single(back.Games);
        Assert.Equal((570, "D2"), (game.SteamAppId, game.Initials));
        Assert.Null(GameLaunchRules.Validate(game.Launch));
        Assert.Null(CatalogFile.Parse("{not json"));
    }
}
