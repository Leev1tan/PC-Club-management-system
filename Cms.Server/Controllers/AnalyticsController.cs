using Cms.Server.Analytics;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cms.Server.Controllers;

/// <summary>
/// Owner analytics (Owner, Manager): revenue split PC time / bar and by payment method, refunds, bonus,
/// average ticket, occupancy per zone and per weekday x club hour, cash variance of the shifts, and a
/// reconciliation with the ledger and the Z-reports. Periods are club days (club time zone).
/// </summary>
[ApiController]
[Route("api/analytics")]
[Authorize(Policy = Policies.Management)]
public class AnalyticsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly ClubClock _clock;

    public AnalyticsController(CmsDbContext db, ClubClock? clock = null)
    {
        _db = db;
        _clock = clock ?? ClubClock.System();
    }

    private static readonly object BadPeriod = new
    {
        error = "period must be today, 7d or 30d, or give from and to (yyyy-MM-dd, club dates, at most 366 days, not in the future).",
        code = "bad_period"
    };

    /// <summary>period = today | 7d (default) | 30d, or from + to (club dates, inclusive).</summary>
    [HttpGet]
    public async Task<ActionResult<AnalyticsReport>> Get([FromQuery] string? period = null, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var p = AnalyticsPeriod.Resolve(period, from, to, _clock);
        if (p == null) return BadRequest(BadPeriod);
        return Ok(await AnalyticsService.BuildAsync(_db, p, _clock, HttpContext?.RequestAborted ?? CancellationToken.None));
    }

    /// <summary>The same period as CSV (UTF-8 with BOM). lang: uk (default) or en; sep: semicolon (default, decimal comma) or comma.</summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] string? period = null, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null,
        [FromQuery] string? lang = null, [FromQuery] string? sep = null)
    {
        var p = AnalyticsPeriod.Resolve(period, from, to, _clock);
        if (p == null) return BadRequest(BadPeriod);
        var report = await AnalyticsService.BuildAsync(_db, p, _clock, HttpContext?.RequestAborted ?? CancellationToken.None);
        var bytes = AnalyticsCsv.Build(report, await _db.CurrencyAsync(), lang, sep);
        return File(bytes, Cms.Server.Shifts.CsvExport.ContentType, $"analytics-{p.From:yyyy-MM-dd}_{p.To:yyyy-MM-dd}.csv");
    }
}
