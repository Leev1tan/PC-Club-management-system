using Cms.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TariffsController : ControllerBase
{
    private readonly CmsDbContext _db;

    public TariffsController(CmsDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TariffView>>> List()
    {
        var tariffs = await _db.Tariffs.AsNoTracking().ToListAsync();
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name);
        return Ok(tariffs.Select(t => new TariffView(
            t.Id, t.Name, t.PricePerHour, t.IsDefault, t.ZoneId,
            t.ZoneId.HasValue && zones.ContainsKey(t.ZoneId.Value) ? zones[t.ZoneId.Value] : null
        )));
    }

    [HttpPost]
    public async Task<ActionResult<TariffView>> Create([FromBody] CreateTariffRequest req)
    {
        var tariff = new TariffEntity
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            PricePerHour = req.PricePerHour,
            IsDefault = req.IsDefault,
            ZoneId = req.ZoneId
        };
        _db.Tariffs.Add(tariff);
        await _db.SaveChangesAsync();
        return Ok(new TariffView(tariff.Id, tariff.Name, tariff.PricePerHour, tariff.IsDefault, tariff.ZoneId, null));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TariffView>> Update(Guid id, [FromBody] CreateTariffRequest req)
    {
        var tariff = await _db.Tariffs.FindAsync(id);
        if (tariff == null) return NotFound();
        tariff.Name = req.Name;
        tariff.PricePerHour = req.PricePerHour;
        tariff.IsDefault = req.IsDefault;
        tariff.ZoneId = req.ZoneId;
        await _db.SaveChangesAsync();
        return Ok(new TariffView(tariff.Id, tariff.Name, tariff.PricePerHour, tariff.IsDefault, tariff.ZoneId, null));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tariff = await _db.Tariffs.FindAsync(id);
        if (tariff == null) return NotFound();
        _db.Tariffs.Remove(tariff);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateTariffRequest(string Name, decimal PricePerHour, bool IsDefault, Guid? ZoneId);
public record TariffView(Guid Id, string Name, decimal PricePerHour, bool IsDefault, Guid? ZoneId, string? ZoneName);
