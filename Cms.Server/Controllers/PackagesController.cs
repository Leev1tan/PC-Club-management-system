using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Package catalog: fixed-price packages bound to a zone (3 h, 5 h, morning pack, night pack).
/// Front desk reads it (with "available now" and the end time if started now); Owner and Manager
/// change it. A package is sold by starting a session with packageId (POST /api/sessions).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.FrontDesk)]
public class PackagesController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public PackagesController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    /// <summary>Packages, optionally of one zone or of the zone of one PC; activeOnly hides archived ones.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PackageView>>> List([FromQuery] Guid? zoneId, [FromQuery] Guid? deviceId, [FromQuery] bool activeOnly = false)
    {
        if (deviceId.HasValue)
        {
            var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId.Value);
            if (device == null) return NotFound();
            zoneId = device.ZoneId;
            if (zoneId == null) return Ok(Array.Empty<PackageView>());
        }
        var packages = await _db.Packages.AsNoTracking()
            .Where(p => (zoneId == null || p.ZoneId == zoneId) && (!activeOnly || p.IsActive))
            .ToListAsync();
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id);
        var now = _clock.UtcNow;
        return Ok(packages
            .OrderBy(p => p.ZoneId.HasValue && zones.TryGetValue(p.ZoneId.Value, out var z) ? z.SortOrder : int.MaxValue)
            .ThenBy(p => p.SortOrder).ThenBy(p => p.Price)
            .Select(p => View(p, zones, now)));
    }

    [HttpPost]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<PackageView>> Create([FromBody] PackageRequest req)
    {
        var error = await ValidateAsync(req);
        if (error != null) return BadRequest(new { error, code = "bad_package" });
        var package = new PackageEntity { Id = Guid.NewGuid() };
        Apply(package, req);
        _db.Packages.Add(package);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.PackageCreated, "package", package.Id,
            $"Package '{package.Name}' created: {package.Price} ₴, {Describe(package)}", Details(package));
        return Ok(await ViewAsync(package.Id));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<PackageView>> Update(Guid id, [FromBody] PackageRequest req)
    {
        var package = await _db.Packages.FindAsync(id);
        if (package == null) return NotFound();
        var error = await ValidateAsync(req);
        if (error != null) return BadRequest(new { error, code = "bad_package" });
        var before = Details(package);
        var beforePrice = package.Price;
        Apply(package, req);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.PackageUpdated, "package", package.Id,
            $"Package '{package.Name}' changed: {beforePrice} -> {package.Price} ₴, {Describe(package)}" + (package.IsActive ? "" : " (not on sale)"),
            new { before, after = Details(package) });
        return Ok(await ViewAsync(id));
    }

    /// <summary>Removes a package from the catalog; sessions keep their history (package reference cleared).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var package = await _db.Packages.FindAsync(id);
        if (package == null) return NotFound();
        if (await _db.Sessions.AnyAsync(s => s.PackageId == id && s.Status == "active"))
            return Conflict(new { error = "The package is in use by an active session. Take it off sale instead.", code = "package_in_use" });
        _db.Packages.Remove(package);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.PackageDeleted, "package", id, $"Package '{package.Name}' deleted ({package.Price} ₴)", Details(package));
        return NoContent();
    }

    private async Task<PackageView> ViewAsync(Guid id)
    {
        var package = await _db.Packages.AsNoTracking().FirstAsync(p => p.Id == id);
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id);
        return View(package, zones, _clock.UtcNow);
    }

    private PackageView View(PackageEntity p, IReadOnlyDictionary<Guid, ZoneEntity> zones, DateTimeOffset now)
    {
        var availability = PackageWindows.Evaluate(p, now, _clock.Zone);
        return new PackageView(
            p.Id, p.Name, p.ZoneId, p.ZoneId.HasValue && zones.TryGetValue(p.ZoneId.Value, out var zone) ? zone.Name : null,
            p.Price, p.DurationMinutes,
            p.WindowStartMinute.HasValue ? TariffCalculator.MinuteText(p.WindowStartMinute.Value) : null,
            p.WindowEndMinute.HasValue ? TariffCalculator.MinuteText(p.WindowEndMinute.Value) : null,
            TariffCalculator.MaskToDays(p.DaysMask), p.IsActive, p.SortOrder,
            availability.Available, availability.Terms?.EndUtc, availability.Terms?.Minutes,
            availability.Error?.Code, availability.Error?.Message);
    }

    private static void Apply(PackageEntity p, PackageRequest req)
    {
        p.Name = req.Name!.Trim();
        p.ZoneId = req.ZoneId;
        p.Price = TariffCalculator.Round(req.Price);
        p.DurationMinutes = req.DurationMinutes is > 0 ? req.DurationMinutes : null;
        var window = !string.IsNullOrWhiteSpace(req.WindowStart) && !string.IsNullOrWhiteSpace(req.WindowEnd);
        p.WindowStartMinute = window && TariffCalculator.TryParseMinute(req.WindowStart, out var s) ? s : null;
        p.WindowEndMinute = window && TariffCalculator.TryParseMinute(req.WindowEnd, out var e) ? e : null;
        p.DaysMask = TariffCalculator.DaysToMask(req.Days) ?? 127;
        p.IsActive = req.IsActive ?? true;
        p.SortOrder = req.SortOrder ?? p.SortOrder;
    }

    private async Task<string?> ValidateAsync(PackageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 100) return "Name is required (at most 100 characters).";
        if (req.ZoneId == null) return "A package is bound to a zone: choose the zone.";
        if (!await _db.Zones.AnyAsync(z => z.Id == req.ZoneId)) return "Zone not found.";
        if (req.Price is <= 0 or > 100_000) return "Price must be between 0.01 and 100000.";
        if (req.DurationMinutes is < 0 or > 24 * 60) return "Duration must be between 1 and 1440 minutes.";
        var hasStart = !string.IsNullOrWhiteSpace(req.WindowStart);
        var hasEnd = !string.IsNullOrWhiteSpace(req.WindowEnd);
        if (hasStart != hasEnd) return "A time window needs both start and end.";
        if (hasStart)
        {
            if (!TariffCalculator.TryParseMinute(req.WindowStart, out var s) || s >= 24 * 60) return "Window start must be HH:mm (00:00-23:59).";
            if (!TariffCalculator.TryParseMinute(req.WindowEnd, out var e)) return "Window end must be HH:mm (00:00-24:00).";
            if (s == e) return "Window start and end are the same.";
        }
        if (!hasStart && req.DurationMinutes is not > 0) return "A package needs a duration, a time window, or both.";
        if (req.Days != null && TariffCalculator.DaysToMask(req.Days) == null) return "Days must be 1 (Monday) ... 7 (Sunday), at least one.";
        return null;
    }

    private static string Describe(PackageEntity p)
    {
        var parts = new List<string>();
        if (p.DurationMinutes.HasValue) parts.Add($"{p.DurationMinutes} min");
        if (p.WindowStartMinute.HasValue && p.WindowEndMinute.HasValue)
            parts.Add($"{TariffCalculator.MinuteText(p.WindowStartMinute.Value)}-{TariffCalculator.MinuteText(p.WindowEndMinute.Value)}");
        return string.Join(", ", parts);
    }

    private static object Details(PackageEntity p) => new
    {
        p.Name, p.ZoneId, p.Price, p.DurationMinutes,
        window = p.WindowStartMinute.HasValue && p.WindowEndMinute.HasValue
            ? $"{TariffCalculator.MinuteText(p.WindowStartMinute.Value)}-{TariffCalculator.MinuteText(p.WindowEndMinute.Value)}" : null,
        days = TariffCalculator.MaskToDays(p.DaysMask), p.IsActive
    };
}

/// <summary>
/// DurationMinutes and/or a window (WindowStart/WindowEnd "HH:mm"; end &lt;= start = past midnight).
/// Days: ISO weekdays on which the window starts (default all). IsActive: on sale (default true).
/// </summary>
public record PackageRequest(string? Name, Guid? ZoneId, decimal Price, int? DurationMinutes,
    string? WindowStart = null, string? WindowEnd = null, int[]? Days = null, bool? IsActive = null, int? SortOrder = null);

public record PackageView(
    Guid Id, string Name, Guid? ZoneId, string? ZoneName, decimal Price, int? DurationMinutes,
    string? WindowStart, string? WindowEnd, int[] Days, bool IsActive, int SortOrder,
    bool AvailableNow, DateTimeOffset? EndsAtIfStartedNow, int? MinutesIfStartedNow,
    string? UnavailableCode, string? UnavailableReason);
