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

    public LedgerController(CmsDbContext db) => _db = db;

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
