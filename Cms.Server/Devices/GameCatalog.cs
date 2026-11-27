using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Devices;

/// <summary>Errors of the game catalog: { error, code, args } like the billing errors (texts in admin-ui/app/i18n.ts).</summary>
public static class GameErrors
{
    public static readonly BillingError NotFound = new("game_not_found", "Game not found", StatusCodes.Status404NotFound);
    public static readonly BillingError BadName = new("game_bad_name", "The name is required (at most 100 characters).");
    public static readonly BillingError BadCategory = new("game_bad_category", "The category is required (at most 50 characters).");
    public static readonly BillingError NameTaken = new("game_name_taken", "A game with this name is already in the catalog.", StatusCodes.Status409Conflict);
    public static readonly BillingError BadKind = new("game_bad_kind", "The launch kind must be exe, steam or url.");
    public static readonly BillingError BadExe = new("game_bad_exe", "The program must be an absolute Windows path to an .exe file (C:\\... or %ProgramFiles%\\...).");
    public static readonly BillingError BadArgs = new("game_bad_args", "The arguments must be one line of at most 500 characters.");
    public static readonly BillingError BadWorkdir = new("game_bad_workdir", "The working directory must be an absolute Windows path.");
    public static readonly BillingError BadSteamId = new("game_bad_steam_id", "The Steam AppID must be a positive number.");
    public static readonly BillingError BadUrl = new("game_bad_url", "The link must use a game launcher scheme: {0}.");
    public static readonly BillingError BadProcess = new("game_bad_process", "The process name must be a file name such as cs2 or cs2.exe.");
    public static readonly BillingError BadColor = new("game_bad_color", "The colour must be #rrggbb.");
    public static readonly BillingError BadZone = new("game_bad_zone", "Choose at least one zone, or all zones.");
    public static readonly BillingError HasHistory = new("game_has_history", "The game has play history; take it off the catalog (inactive) instead.", StatusCodes.Status409Conflict);
    public static readonly BillingError BadAccountPlatform = new("game_bad_account_platform", "The club account platform must be empty or one of: {0}.");

    public static BillingError ForRule(string code) => code switch
    {
        "game_bad_exe" => BadExe,
        "game_bad_args" => BadArgs,
        "game_bad_workdir" => BadWorkdir,
        "game_bad_steam_id" => BadSteamId,
        "game_bad_url" => BadUrl.With(string.Join(", ", GameLaunchRules.UrlSchemes)),
        _ => BadKind
    };
}

/// <summary>Create or update a catalog entry. Fields of other launch kinds are ignored and stored empty.</summary>
public sealed record GameRequest(
    string? Name, string? Category, string? Kind,
    string? ExePath = null, string? Arguments = null, string? WorkingDirectory = null,
    int? SteamAppId = null, string? LaunchUrl = null, string? ProcessName = null,
    bool AllZones = true, IReadOnlyList<Guid>? ZoneIds = null, string? Color = null, int SortOrder = 0, bool IsActive = true,
    string? AccountPlatform = null);

public sealed record GameView(
    Guid Id, string Name, string Category, string Kind, string? ExePath, string? Arguments, string? WorkingDirectory,
    int? SteamAppId, string? LaunchUrl, string? ProcessName, bool AllZones, IReadOnlyList<Guid> ZoneIds,
    string Color, string Initials, int SortOrder, bool IsActive, DateTimeOffset UpdatedUtc, int Plays)
{
    /// <summary>The game needs a club account of this platform (account pool); null = none.</summary>
    public string? AccountPlatform { get; init; }
}

public static class GameCatalog
{
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Checks the request and writes it into the entity (new or existing). Returns null or the error.</summary>
    public static async Task<BillingError?> ApplyAsync(CmsDbContext db, GameEntity game, GameRequest req, CancellationToken ct = default)
    {
        var name = Clean(req.Name);
        if (name is null or { Length: > 100 }) return GameErrors.BadName;
        var category = Clean(req.Category);
        if (category is null or { Length: > 50 }) return GameErrors.BadCategory;
        var kind = Clean(req.Kind)?.ToLowerInvariant();
        if (kind == null || !GameLaunchKinds.All.Contains(kind)) return GameErrors.BadKind;

        var launch = kind switch
        {
            GameLaunchKinds.Exe => new GameLaunch(kind, Clean(req.ExePath), Clean(req.Arguments), Clean(req.WorkingDirectory)),
            GameLaunchKinds.Steam => new GameLaunch(kind, SteamAppId: req.SteamAppId),
            _ => new GameLaunch(kind, LaunchUrl: Clean(req.LaunchUrl))
        };
        var rule = GameLaunchRules.Validate(launch);
        if (rule != null) return GameErrors.ForRule(rule);

        string? process = null;
        if (Clean(req.ProcessName) is { } p)
        {
            process = GameLaunchRules.NormalizeProcessName(p);
            if (process == null) return GameErrors.BadProcess;
        }
        var color = Clean(req.Color);
        if (color != null && !GameTile.IsColor(color)) return GameErrors.BadColor;
        string? accountPlatform = null;
        if (Clean(req.AccountPlatform) is { } ap)
        {
            accountPlatform = AccountPlatforms.Normalize(ap);
            if (accountPlatform == null) return GameErrors.BadAccountPlatform.With(string.Join(", ", AccountPlatforms.All));
        }

        var zoneIds = (req.ZoneIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (!req.AllZones)
        {
            if (zoneIds.Count == 0) return GameErrors.BadZone;
            var known = await db.Zones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).CountAsync(ct);
            if (known != zoneIds.Count) return GameErrors.BadZone;
        }

        var lower = name.ToLowerInvariant();
        var others = await db.Games.AsNoTracking().Where(g => g.Id != game.Id).Select(g => g.Name).ToListAsync(ct);
        if (others.Any(n => n.ToLowerInvariant() == lower)) return GameErrors.NameTaken;

        game.Name = name;
        game.Category = category;
        game.LaunchKind = kind;
        game.ExePath = launch.ExePath;
        game.Arguments = launch.Arguments;
        game.WorkingDirectory = launch.WorkingDirectory;
        game.SteamAppId = launch.SteamAppId;
        game.LaunchUrl = launch.LaunchUrl;
        game.ProcessName = process;
        game.AllZones = req.AllZones;
        game.Color = color?.ToLowerInvariant() ?? (GameTile.IsColor(game.Color) && game.CreatedUtc != default ? game.Color : GameTile.DefaultColor(name));
        game.SortOrder = Math.Clamp(req.SortOrder, -10_000, 10_000);
        game.IsActive = req.IsActive;
        game.AccountPlatform = accountPlatform;

        var current = await db.GameZones.Where(z => z.GameId == game.Id).ToListAsync(ct);
        var wanted = req.AllZones ? new List<Guid>() : zoneIds;
        db.GameZones.RemoveRange(current.Where(z => !wanted.Contains(z.ZoneId)));
        foreach (var zoneId in wanted.Where(id => current.All(z => z.ZoneId != id)))
            db.GameZones.Add(new GameZoneEntity { GameId = game.Id, ZoneId = zoneId });
        return null;
    }

    public static async Task<List<GameView>> ViewsAsync(CmsDbContext db, Guid? id = null, CancellationToken ct = default)
    {
        var games = await db.Games.AsNoTracking().Where(g => id == null || g.Id == id).ToListAsync(ct);
        var ids = games.Select(g => g.Id).ToList();
        var zones = (await db.GameZones.AsNoTracking().Where(z => ids.Contains(z.GameId)).ToListAsync(ct))
            .GroupBy(z => z.GameId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(z => z.ZoneId).ToList());
        var plays = await db.GamePlays.AsNoTracking().Where(p => ids.Contains(p.GameId))
            .GroupBy(p => p.GameId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return games
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameView(g.Id, g.Name, g.Category, g.LaunchKind, g.ExePath, g.Arguments, g.WorkingDirectory,
                g.SteamAppId, g.LaunchUrl, g.ProcessName, g.AllZones, zones.TryGetValue(g.Id, out var z) ? z : Array.Empty<Guid>(),
                g.Color, GameTile.Initials(g.Name), g.SortOrder, g.IsActive, g.UpdatedUtc, plays.TryGetValue(g.Id, out var n) ? n : 0)
            { AccountPlatform = g.AccountPlatform })
            .ToList();
    }

    /// <summary>Active entries for the zone of a PC (AllZones, or listed for the zone), in catalog order.</summary>
    public static async Task<List<CatalogGame>> ForZoneAsync(CmsDbContext db, Guid? zoneId, CancellationToken ct = default)
    {
        var inZone = zoneId == null ? new List<Guid>()
            : await db.GameZones.AsNoTracking().Where(z => z.ZoneId == zoneId).Select(z => z.GameId).ToListAsync(ct);
        var games = await db.Games.AsNoTracking().Where(g => g.IsActive && (g.AllZones || inZone.Contains(g.Id))).ToListAsync(ct);
        return games
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CatalogGame
            {
                Id = g.Id, Name = g.Name, Category = g.Category, Kind = g.LaunchKind, ExePath = g.ExePath, Arguments = g.Arguments,
                WorkingDirectory = g.WorkingDirectory, SteamAppId = g.SteamAppId, LaunchUrl = g.LaunchUrl,
                ProcessName = g.ProcessName ?? (g.LaunchKind == GameLaunchKinds.Exe ? GameLaunchRules.NormalizeProcessName(Path.GetFileName(g.ExePath?.Replace('\\', '/'))) : null),
                Color = g.Color, Initials = GameTile.Initials(g.Name), SortOrder = g.SortOrder, AccountPlatform = g.AccountPlatform
            })
            .ToList();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Strong ETag of the catalog JSON of a PC: the agent asks again with If-None-Match and gets 304.</summary>
    public static (string Json, string ETag) Serialize(IReadOnlyList<CatalogGame> games)
    {
        var json = JsonSerializer.Serialize(games, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();
        return (json, $"\"{hash}\"");
    }

    /// <summary>A short text of the launch, for audit summaries.</summary>
    public static string LaunchText(GameEntity g) => g.LaunchKind switch
    {
        GameLaunchKinds.Steam => $"Steam {g.SteamAppId}",
        GameLaunchKinds.Url => g.LaunchUrl is { Length: > 60 } u ? u[..60] + "..." : g.LaunchUrl ?? "",
        _ => g.ExePath ?? ""
    };
}
