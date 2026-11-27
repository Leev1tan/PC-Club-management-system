using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

public sealed class GameGridTests
{
    private static CatalogGame Steam(string name, string category, int order, int appId = 730, string? process = null) =>
        new() { Id = Guid.NewGuid(), Name = name, Category = category, Kind = GameLaunchKinds.Steam, SteamAppId = appId, SortOrder = order, ProcessName = process };

    private static readonly CatalogGame[] Catalog =
    {
        Steam("Counter-Strike 2", "Шутери", 10, process: "cs2"),
        Steam("Dota 2", "MOBA", 20, 570),
        Steam("Valorant", "Шутери", 30),
        Steam("Rocket League", "Спорт", 40),
        new() { Id = Guid.NewGuid(), Name = "Broken", Category = "Інше", Kind = GameLaunchKinds.Url, LaunchUrl = "https://evil.example", SortOrder = 5 },
    };

    [Fact]
    public void Categories_follow_the_catalog_order()
    {
        Assert.Equal(new[] { "Інше", "Шутери", "MOBA", "Спорт" }, GameGrid.Categories(Catalog));
    }

    [Fact]
    public void Search_and_category_filter_the_grid_and_invalid_entries_never_show()
    {
        Assert.Equal(new[] { "Counter-Strike 2", "Dota 2", "Valorant", "Rocket League" }, GameGrid.Filter(Catalog, null, null).Select(g => g.Name));
        Assert.Equal(new[] { "Counter-Strike 2", "Valorant" }, GameGrid.Filter(Catalog, "шутери", "").Select(g => g.Name));
        Assert.Equal(new[] { "Counter-Strike 2" }, GameGrid.Filter(Catalog, null, "strike  counter").Select(g => g.Name));
        Assert.Equal(new[] { "Dota 2" }, GameGrid.Filter(Catalog, null, "DOTA").Select(g => g.Name));
        Assert.Empty(GameGrid.Filter(Catalog, "Інше", null)); // the invalid link is not shown
        Assert.Empty(GameGrid.Filter(Catalog, null, "minecraft"));
    }

    [Fact]
    public void A_program_starts_directly_a_link_through_the_shell()
    {
        string Expand(string s) => s.Replace("%LOCALAPPDATA%", @"C:\Users\player\AppData\Local");
        var discord = new CatalogGame
        {
            Id = Guid.NewGuid(), Name = "Discord", Kind = GameLaunchKinds.Exe, ExePath = @"%LOCALAPPDATA%\Discord\Update.exe", Arguments = "--processStart Discord.exe"
        };
        var start = GameStarter.Plan(discord, Expand)!;
        Assert.Equal(new GameStartInfo(@"C:\Users\player\AppData\Local\Discord\Update.exe", "--processStart Discord.exe",
            @"C:\Users\player\AppData\Local\Discord", false), start);

        Assert.Equal(new GameStartInfo("steam://rungameid/570", "", null, true), GameStarter.Plan(Catalog[1], Expand));
        var epic = new CatalogGame { Id = Guid.NewGuid(), Name = "Fortnite", Kind = GameLaunchKinds.Url, LaunchUrl = "com.epicgames.launcher://apps/x?action=launch" };
        Assert.True(GameStarter.Plan(epic, Expand)!.UseShellExecute);

        // Never an invalid entry, and an empty variable cannot turn the path into a relative one
        Assert.Null(GameStarter.Plan(Catalog[4], Expand));
        Assert.Null(GameStarter.Plan(discord, s => s.Replace("%LOCALAPPDATA%", "")));
    }
}

public sealed class GameTrackerTests
{
    private const int Session = 2;
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);

    private static CatalogGame Game(string name, string? process) => new() { Id = Guid.NewGuid(), Name = name, Kind = GameLaunchKinds.Steam, SteamAppId = 1, ProcessName = process };

    private static List<ProcessInfo> Ps(params (int Pid, string Name, int Session)[] p) => p.Select(x => new ProcessInfo(x.Pid, x.Name, x.Session)).ToList();

    [Fact]
    public void A_started_program_stops_when_its_process_is_gone_twice()
    {
        var tracker = new GameTracker();
        var game = Game("Valorant", "VALORANT-Win64-Shipping");
        Assert.Equal(new GameEvent(LauncherEvent.GameStarted, game.Id, T0), tracker.Started(game, 100, T0));
        Assert.Empty(tracker.Update(Ps((100, "RiotClientServices", Session)), Session, T0.AddSeconds(5)));
        Assert.Empty(tracker.Update(Ps((200, "VALORANT-Win64-Shipping", Session)), Session, T0.AddSeconds(10))); // the launcher exited, the game runs
        Assert.Empty(tracker.Update(Ps(), Session, T0.AddSeconds(15)));                                          // one miss is not a stop
        var events = tracker.Update(Ps(), Session, T0.AddSeconds(20));
        Assert.Equal(new[] { new GameEvent(LauncherEvent.GameStopped, game.Id, T0.AddSeconds(20)) }, events);
        Assert.Empty(tracker.Running);
    }

    [Fact]
    public void Processes_of_other_sessions_do_not_count_and_a_start_that_never_appears_times_out()
    {
        var tracker = new GameTracker();
        var cs = Game("CS2", "cs2");
        tracker.Started(cs, null, T0); // steam://rungameid/730: no process handle
        Assert.Empty(tracker.Update(Ps((300, "cs2", Session + 1)), Session, T0.AddMinutes(5))); // another user's cs2: still waiting
        Assert.Empty(tracker.Update(Ps(), Session, T0.AddMinutes(9)));
        var events = tracker.Update(Ps(), Session, T0.AddMinutes(11));
        Assert.Equal(LauncherEvent.GameStopped, Assert.Single(events).Type);
    }

    [Fact]
    public void When_the_current_game_stops_another_running_game_is_reported_again()
    {
        var tracker = new GameTracker();
        var cs = Game("CS2", "cs2");
        var discord = Game("Discord", "Discord");
        tracker.Started(cs, null, T0);
        tracker.Update(Ps((1, "cs2", Session)), Session, T0.AddSeconds(5));
        tracker.Started(discord, 50, T0.AddSeconds(6)); // the server shows Discord now
        tracker.Update(Ps((1, "cs2", Session), (50, "Discord", Session)), Session, T0.AddSeconds(10));
        tracker.Update(Ps((1, "cs2", Session)), Session, T0.AddSeconds(15));
        var events = tracker.Update(Ps((1, "cs2", Session)), Session, T0.AddSeconds(20));
        Assert.Equal(new[] { LauncherEvent.GameStopped, LauncherEvent.GameStarted }, events.Select(e => e.Type));
        Assert.Equal(new[] { discord.Id, cs.Id }, events.Select(e => e.GameId));
        Assert.Equal(new[] { cs.Id }, tracker.StopAll(T0.AddMinutes(1)).Select(e => e.GameId));
        Assert.Empty(tracker.StartedPids);
    }

    [Fact]
    public void Started_pids_are_kept_for_the_teardown()
    {
        var tracker = new GameTracker();
        tracker.Started(Game("A", null), 11, T0);
        tracker.Started(Game("B", "b"), null, T0);
        tracker.Started(Game("C", null), 13, T0);
        Assert.Equal(new[] { 11, 13 }, tracker.StartedPids);
    }
}
