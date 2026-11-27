using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The demo has a catalog, games on the map and top games in the analytics.</summary>
public sealed class DemoGameTests : IClassFixture<DemoFactory>
{
    private readonly DemoFactory _factory;

    public DemoGameTests(DemoFactory factory) => _factory = factory;

    [Fact]
    public async Task The_demo_club_plays_games_of_its_catalog()
    {
        var owner = await _factory.LoginAsync("demo-owner", "demo");
        var games = (await owner.GetFromJsonAsync<JsonElement>("/api/games")).EnumerateArray().ToList();
        Assert.True(games.Count >= 15, $"{games.Count} games");
        Assert.Single(games, g => !g.GetProperty("isActive").GetBoolean());
        Assert.Contains(games, g => g.GetProperty("kind").GetString() == "url");
        Assert.Contains(games, g => g.GetProperty("kind").GetString() == "exe");
        Assert.Contains(games, g => !g.GetProperty("allZones").GetBoolean() && g.GetProperty("zoneIds").GetArrayLength() > 0);

        var devices = (await owner.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray().ToList();
        var playing = devices.Where(d => d.GetProperty("currentGame").ValueKind == JsonValueKind.String).ToList();
        Assert.NotEmpty(playing);
        Assert.All(playing, d => Assert.Equal("occupied", d.GetProperty("occupancyStatus").GetString()));

        var top = (await owner.GetFromJsonAsync<JsonElement>("/api/analytics?period=30d")).GetProperty("topGames").EnumerateArray().ToList();
        Assert.Equal(10, top.Count);
        Assert.Contains("Counter-Strike 2", top.Take(3).Select(g => g.GetProperty("name").GetString()));
        Assert.True(top.Sum(g => g.GetProperty("share").GetDecimal()) <= 100.5m);
    }
}
