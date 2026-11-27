using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// The money ledger of the whole club (Owner, Manager): read-only list and the reconciliation of the
/// cached player balances with the ledger. Rows cannot be changed or deleted through any API.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.Management)]
public class LedgerController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly ClubClock _clock;

    public LedgerController(CmsDbContext db, ClubClock? clock = null)
    {
        _db = db;
        _clock = clock ?? ClubClock.System();
    }

    /// <summary>
    /// Ledger rows of a date range in club time (from and to inclusive, at most 366 days) as CSV for
    /// accounting (UTF-8 with BOM). lang: uk (default) or en; sep: semicolon (default, decimal comma) or comma.
    /// </summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? lang = null, [FromQuery] string? sep = null)
    {
        if (from == null || to == null || to < from || to.Value.DayNumber - from.Value.DayNumber > 365)
            return BadRequest(new { error = "Give from and to (yyyy-MM-dd, at most 366 days).", code = "bad_range" });
        var fromUtc = ClubTimeZone.LocalToUtc(from.Value.ToDateTime(TimeOnly.MinValue), _clock.Zone);
        var toUtc = ClubTimeZone.LocalToUtc(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), _clock.Zone);
        var (bytes, _) = await Cms.Server.Shifts.CsvExport.LedgerAsync(_db, _clock, fromUtc, toUtc, lang, sep, HttpContext.RequestAborted);
        return File(bytes, Cms.Server.Shifts.CsvExport.ContentType, $"ledger-{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.csv");
    }

    /// <summary>Newest first; filters: type, userId, guests (rows without a player); paging with beforeId.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<LedgerEntryView>>> List([FromQuery] int take = 100, [FromQuery] long? beforeId = null,
        [FromQuery] string? type = null, [FromQuery] Guid? userId = null, [FromQuery] bool guests = false)
    {
        var q = _db.Ledger.AsNoTracking().AsQueryable();
        if (beforeId.HasValue) q = q.Where(l => l.Id < beforeId.Value);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(l => l.Type == type.Trim());
        if (userId.HasValue) q = q.Where(l => l.UserId == userId);
        if (guests) q = q.Where(l => l.UserId == null);
        var rows = await q.OrderByDescending(l => l.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return Ok(rows.Select(LedgerEntryView.From));
    }

    /// <summary>Players whose Balance / BonusPoints differ from the sum of their ledger rows (empty = all fine).</summary>
    [HttpGet("reconcile")]
    public async Task<ActionResult<ReconcileView>> Reconcile()
    {
        var mismatches = await Ledger.ReconcileAsync(_db);
        var players = await _db.Users.CountAsync();
        var rows = await _db.Ledger.CountAsync();
        return Ok(new ReconcileView(players, rows, mismatches.Count == 0, mismatches));
    }
}

public record ReconcileView(int Players, int LedgerRows, bool Ok, IReadOnlyList<Ledger.Mismatch> Mismatches);
