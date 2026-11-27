using Cms.Server.Data;
using Cms.Shared;

namespace Cms.Server.Demo;

/// <summary>
/// Demo game catalog and the games played in the demo sessions. Only names: the tiles are generated
/// (initials on a colour), no logos or other art. Categories are club data (Ukrainian, like the bar).
/// </summary>
public static partial class DemoSeeder
{
    /// <summary>Zones: null = all zones, else zone indexes (0 Standard, 1 VIP, 2 Bootcamp / Pro). Weights per zone index.</summary>
    private sealed record GameSpec(string Name, string Category, GameLaunch Launch, string? Process, int[]? Zones, double[] Weights, bool Active = true,
        string? AccountPlatform = null);

    private const string RiotClient = @"C:\Riot Games\Riot Client\RiotClientServices.exe";

    private static readonly GameSpec[] GameSpecs =
    {
        new("Counter-Strike 2", "Шутери", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 730), "cs2", null, new[] { 30.0, 20, 40 }),
        new("Dota 2", "MOBA", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 570), "dota2", null, new[] { 20.0, 12, 25 }),
        new("Valorant", "Шутери", new GameLaunch(GameLaunchKinds.Exe, RiotClient, "--launch-product=valorant --launch-patchline=live"),
            "VALORANT-Win64-Shipping", null, new[] { 14.0, 10, 20 }),
        new("Fortnite", "Королівська битва",
            new GameLaunch(GameLaunchKinds.Url, LaunchUrl: "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true"),
            "FortniteClient-Win64-Shipping", new[] { 0, 1 }, new[] { 12.0, 8, 0 }),
        new("PUBG: BATTLEGROUNDS", "Королівська битва", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 578080), "TslGame", null, new[] { 10.0, 8, 6 }),
        new("Grand Theft Auto V", "Пригоди", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 271590), "GTA5", new[] { 0, 1 }, new[] { 8.0, 14, 0 },
            AccountPlatform: AccountPlatforms.Steam),
        new("Apex Legends", "Королівська битва", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 1172470), "r5apex", null, new[] { 8.0, 6, 6 }),
        new("League of Legends", "MOBA", new GameLaunch(GameLaunchKinds.Exe, RiotClient, "--launch-product=league_of_legends --launch-patchline=live"),
            "League of Legends", null, new[] { 8.0, 5, 8 }),
        new("Marvel Rivals", "Шутери", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 2767030), "Marvel-Win64-Shipping", null, new[] { 7.0, 6, 4 }),
        new("Overwatch 2", "Шутери", new GameLaunch(GameLaunchKinds.Url, LaunchUrl: "battlenet://Pro"), "Overwatch", null, new[] { 5.0, 5, 4 }),
        new("Rocket League", "Спорт",
            new GameLaunch(GameLaunchKinds.Url, LaunchUrl: "com.epicgames.launcher://apps/9773aa1aa54f4f7b80e44bef04986cea%3A530145df28a24424923f5828cc9031a1%3ASugar?action=launch&silent=true"),
            "RocketLeague", new[] { 0, 1 }, new[] { 5.0, 5, 0 }),
        new("EA SPORTS FC 25", "Спорт", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 2669320), "FC25", new[] { 0, 1 }, new[] { 5.0, 8, 0 },
            AccountPlatform: AccountPlatforms.Steam),
        new("Rust", "Виживання", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 252490), "RustClient", null, new[] { 4.0, 6, 0 },
            AccountPlatform: AccountPlatforms.Steam),
        new("Deadlock", "MOBA", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 1422450), "deadlock", null, new[] { 4.0, 3, 6 }),
        new("Minecraft", "Пісочниця", new GameLaunch(GameLaunchKinds.Exe, @"C:\Program Files (x86)\Minecraft Launcher\MinecraftLauncher.exe"),
            "javaw", new[] { 0 }, new[] { 6.0, 0, 0 }),
        new("Steam", "Лаунчери", new GameLaunch(GameLaunchKinds.Exe, @"C:\Program Files (x86)\Steam\steam.exe"), "steam", null, new[] { 3.0, 3, 2 }),
        new("Discord", "Спілкування", new GameLaunch(GameLaunchKinds.Exe, @"%LOCALAPPDATA%\Discord\Update.exe", "--processStart Discord.exe"),
            "Discord", null, new[] { 2.0, 2, 1 }),
        new("Battlefield 2042", "Шутери", new GameLaunch(GameLaunchKinds.Steam, SteamAppId: 1517290), "BF2042", null, new[] { 0.0, 0, 0 }, Active: false),
    };

    /// <summary>Popularity of a demo game in a zone (zone index = sort order of the zone); 1 for an unknown name.</summary>
    internal static double GameWeight(string name, int zoneIndex)
    {
        var spec = GameSpecs.FirstOrDefault(g => g.Name == name);
        return spec == null ? 1 : zoneIndex >= 0 && zoneIndex < spec.Weights.Length ? spec.Weights[zoneIndex] : 1;
    }

    /// <summary>A catalog entry of the demo with its weights per zone index.</summary>
    private sealed record DemoGame(GameEntity Game, double[] Weights, int[]? Zones);

    private sealed partial class Seeding
    {
        private readonly List<DemoGame> _games = new();
        private readonly List<Guid> _zoneIds = new();

        public List<GamePlayEntity> Plays { get; } = new();

        private void SeedGames()
        {
            var created = HistoryStart.AddDays(-10);
            for (var i = 0; i < GameSpecs.Length; i++)
            {
                var spec = GameSpecs[i];
                var game = new GameEntity
                {
                    Id = Guid.NewGuid(), Name = spec.Name, Category = spec.Category, LaunchKind = spec.Launch.Kind,
                    ExePath = spec.Launch.ExePath, Arguments = spec.Launch.Arguments, WorkingDirectory = spec.Launch.WorkingDirectory,
                    SteamAppId = spec.Launch.SteamAppId, LaunchUrl = spec.Launch.LaunchUrl, ProcessName = spec.Process,
                    AllZones = spec.Zones == null, Color = GameTile.DefaultColor(spec.Name), SortOrder = (i + 1) * 10,
                    IsActive = spec.Active, CreatedUtc = created, UpdatedUtc = created, AccountPlatform = spec.AccountPlatform
                };
                Club.Add(game);
                _games.Add(new DemoGame(game, spec.Weights, spec.Zones));
            }
            foreach (var g in _games.Where(g => g.Zones != null))
                foreach (var z in g.Zones!)
                    Club.Add(new GameZoneEntity { GameId = g.Game.Id, ZoneId = _zoneIds[z] });
        }

        /// <summary>A game for a PC of the zone, weighted by the popularity in that zone.</summary>
        private DemoGame? PickGame(int zoneIndex, Guid? except = null)
        {
            var options = _games.Where(g => g.Game.IsActive && g.Game.Id != except && (g.Zones == null || g.Zones.Contains(zoneIndex)) && g.Weights[zoneIndex] > 0).ToList();
            var total = options.Sum(g => g.Weights[zoneIndex]);
            if (total <= 0) return null;
            var r = _rng.NextDouble() * total;
            foreach (var g in options)
            {
                r -= g.Weights[zoneIndex];
                if (r <= 0) return g;
            }
            return options[^1];
        }

        /// <summary>
        /// One to three games per session: the first a few minutes after the start, the last until a few
        /// minutes before the end. A running session keeps its last game open (the current game on the map).
        /// </summary>
        private void SeedPlays()
        {
            var zoneOf = _pcs.ToDictionary(p => p.Device.Id, p => _zoneIds.IndexOf(p.Device.ZoneId!.Value));
            foreach (var session in Sessions)
            {
                var end = session.Status == "active" ? (session.PausedUtc ?? _now) : session.EndUtc ?? _now;
                var length = (end - session.StartUtc).TotalMinutes;
                if (length < 12 || !zoneOf.TryGetValue(session.DeviceId, out var zone)) continue;
                var count = length < 50 ? 1 : _rng.NextDouble() switch { < 0.7 => 1, < 0.95 => 2, _ => 3 };
                var t = session.StartUtc.AddMinutes(_rng.Next(1, 6));
                Guid? last = null;
                for (var i = 0; i < count; i++)
                {
                    var pick = PickGame(zone, last);
                    if (pick == null) break;
                    var lastPlay = i == count - 1;
                    var stop = lastPlay ? end.AddMinutes(-_rng.Next(0, 4)) : t.AddMinutes(Math.Max(10, (end - t).TotalMinutes * _rng.Next(35, 65) / 100.0));
                    if (stop > end) stop = end;
                    if (stop <= t.AddMinutes(3)) break;
                    var open = lastPlay && session.Status == "active";
                    Plays.Add(new GamePlayEntity
                    {
                        Id = Guid.NewGuid(), DeviceId = session.DeviceId, GameId = pick.Game.Id, SessionId = session.Id, UserId = session.UserId,
                        StartUtc = t, EndUtc = open ? null : stop,
                        EndReason = open ? null : lastPlay ? GamePlayEndReasons.SessionEnded : GamePlayEndReasons.Switched
                    });
                    last = pick.Game.Id;
                    t = stop.AddMinutes(_rng.Next(1, 4));
                }
            }
        }
    }
}
