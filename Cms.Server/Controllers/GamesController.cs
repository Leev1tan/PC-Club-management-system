using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Game catalog (spec area 8): every staff member reads it, Owner and Manager change it. Every change is
/// audited. The lock screens show the active entries of their zone (GET /api/devices/{id}/games).
/// </summary>
[ApiController]
[Route("api/games")]
[Authorize(Policy = Policies.AnyStaff)]
public class GamesController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public GamesController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<GameView>>> List() => Ok(await GameCatalog.ViewsAsync(_db));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GameView>> Get(Guid id)
    {
        var view = (await GameCatalog.ViewsAsync(_db, id)).SingleOrDefault();
        return view == null ? NotFound() : Ok(view);
    }

    [HttpPost]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<GameView>> Create([FromBody] GameRequest req)
    {
        var game = new GameEntity { Id = Guid.NewGuid() };
        var error = await GameCatalog.ApplyAsync(_db, game, req);
        if (error != null) return error.ToActionResult();
        game.CreatedUtc = game.UpdatedUtc = _clock.UtcNow;
        _db.Games.Add(game);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.GameCreated, "game", game.Id,
            $"Game '{game.Name}' added to the catalog: {game.LaunchKind} {GameCatalog.LaunchText(game)}, {ZonesText(game, req)}" + (game.IsActive ? "" : " (inactive)"),
            Details(game, req.ZoneIds));
        return Ok((await GameCatalog.ViewsAsync(_db, game.Id)).Single());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<GameView>> Update(Guid id, [FromBody] GameRequest req)
    {
        var game = await _db.Games.FindAsync(id);
        if (game == null) return NotFound();
        var before = Details(game, await _db.GameZones.AsNoTracking().Where(z => z.GameId == id).Select(z => z.ZoneId).ToListAsync());
        var error = await GameCatalog.ApplyAsync(_db, game, req);
        if (error != null) return error.ToActionResult();
        game.UpdatedUtc = _clock.UtcNow;
        await _db.SaveChangesAsync();
        var after = Details(game, req.AllZones ? null : req.ZoneIds);
        var changed = before.Where(kv => !Equals(Text(kv.Value), Text(after[kv.Key]))).Select(kv => kv.Key).ToList();
        if (changed.Count > 0)
            await _audit.WriteAsync(AuditActions.GameUpdated, "game", game.Id,
                $"Game '{game.Name}' changed: {string.Join(", ", changed)}",
                new { before = changed.ToDictionary(k => k, k => before[k]), after = changed.ToDictionary(k => k, k => after[k]) });
        return Ok((await GameCatalog.ViewsAsync(_db, game.Id)).Single());
    }

    /// <summary>Deletes an entry without play history; an entry with history is set inactive instead (409).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var game = await _db.Games.FindAsync(id);
        if (game == null) return NotFound();
        if (await _db.GamePlays.AnyAsync(p => p.GameId == id)) return GameErrors.HasHistory.ToActionResult();
        _db.Games.Remove(game);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.GameDeleted, "game", id, $"Game '{game.Name}' deleted from the catalog", new { game.Name, game.LaunchKind });
        return NoContent();
    }

    private string ZonesText(GameEntity game, GameRequest req) =>
        game.AllZones ? "all zones" : $"{req.ZoneIds?.Distinct().Count() ?? 0} zone(s)";

    private static Dictionary<string, object?> Details(GameEntity g, IReadOnlyList<Guid>? zoneIds) => new()
    {
        ["name"] = g.Name, ["category"] = g.Category, ["kind"] = g.LaunchKind, ["exePath"] = g.ExePath, ["arguments"] = g.Arguments,
        ["workingDirectory"] = g.WorkingDirectory, ["steamAppId"] = g.SteamAppId, ["launchUrl"] = g.LaunchUrl,
        ["processName"] = g.ProcessName, ["allZones"] = g.AllZones,
        ["zones"] = g.AllZones ? null : zoneIds?.Distinct().OrderBy(z => z).ToList(),
        ["color"] = g.Color, ["sortOrder"] = g.SortOrder, ["active"] = g.IsActive, ["accountPlatform"] = g.AccountPlatform
    };

    private static string? Text(object? value) => value switch
    {
        null => null,
        IEnumerable<Guid> ids => string.Join(",", ids.OrderBy(x => x)),
        _ => value.ToString()
    };
}
