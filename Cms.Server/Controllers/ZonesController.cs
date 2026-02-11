using Cms.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ZonesController : ControllerBase
{
    private readonly CmsDbContext _db;

    public ZonesController(CmsDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ZoneView>>> List()
    {
        var zones = await _db.Zones.AsNoTracking().OrderBy(z => z.SortOrder).ToListAsync();
        return Ok(zones.Select(z => new ZoneView(z.Id, z.Name, z.Color, z.SortOrder)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ZoneView>> Get(Guid id)
    {
        var z = await _db.Zones.FindAsync(id);
        if (z == null) return NotFound();
        return Ok(new ZoneView(z.Id, z.Name, z.Color, z.SortOrder));
    }

    [HttpPost]
    public async Task<ActionResult<ZoneView>> Create([FromBody] CreateZoneRequest req)
    {
        var zone = new ZoneEntity
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Color = req.Color ?? "#6366f1",
            SortOrder = req.SortOrder ?? 0
        };
        _db.Zones.Add(zone);
        await _db.SaveChangesAsync();
        return Ok(new ZoneView(zone.Id, zone.Name, zone.Color, zone.SortOrder));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ZoneView>> Update(Guid id, [FromBody] CreateZoneRequest req)
    {
        var zone = await _db.Zones.FindAsync(id);
        if (zone == null) return NotFound();
        zone.Name = req.Name;
        zone.Color = req.Color ?? zone.Color;
        zone.SortOrder = req.SortOrder ?? zone.SortOrder;
        await _db.SaveChangesAsync();
        return Ok(new ZoneView(zone.Id, zone.Name, zone.Color, zone.SortOrder));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var zone = await _db.Zones.FindAsync(id);
        if (zone == null) return NotFound();
        _db.Zones.Remove(zone);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateZoneRequest(string Name, string? Color, int? SortOrder);
public record ZoneView(Guid Id, string Name, string Color, int SortOrder);
