using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Shifts: open with a float, cash payouts, close with the counted cash (blind for cashiers: the
/// expected cash is shown only after the count). Rules in Shifts/ShiftService.
/// </summary>
[ApiController]
[Route("api/shifts")]
[Authorize(Policy = Policies.FrontDesk)]
public class ShiftsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger? _audit;
    private readonly ClubClock _clock;

    public ShiftsController(CmsDbContext db, AuditLogger? audit = null, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private ShiftService Service => new(_db, _clock, _audit, StaffActor.From(HttpContext?.User));
    private bool IsManager => User.IsInRole(StaffRoles.Owner) || User.IsInRole(StaffRoles.Manager);
    private Guid? MyId => StaffActor.From(User)?.Id;

    /// <summary>The open shift (null when none), its expected cash (Owner, Manager only) and the last count.</summary>
    [HttpGet("current")]
    public async Task<ActionResult<CurrentShiftView>> Current()
    {
        var shift = await ShiftService.OpenShiftAsync(_db);
        var previous = (await _db.Shifts.AsNoTracking().Where(s => s.Status == ShiftStatuses.Closed)
            .OrderByDescending(s => s.Id).Take(1).ToListAsync()).FirstOrDefault();
        decimal? expected = shift != null && IsManager ? await ShiftService.ExpectedCashAsync(_db, shift) : null;
        return Ok(new CurrentShiftView(shift == null ? null : ShiftView.From(shift), expected, shift != null && !IsManager,
            previous?.CountedCash, previous?.Id, _db.RequireOpenShift));
    }

    [HttpPost]
    public async Task<ActionResult<ShiftView>> Open([FromBody] OpenShiftRequest req)
    {
        var result = await Service.OpenAsync(req.OpeningFloat, req.Note);
        return result.Ok ? Ok(ShiftView.From(result.Value!)) : result.Error!.ToActionResult();
    }

    /// <summary>Close with the counted cash. The answer shows the expected cash and the variance.</summary>
    [HttpPost("{id:long}/close")]
    public async Task<ActionResult<ShiftView>> Close(long id, [FromBody] CloseShiftRequest req)
    {
        var result = await Service.CloseAsync(id, req.CountedCash, req.Note, IsManager);
        return result.Ok ? Ok(ShiftView.From(result.Value!)) : result.Error!.ToActionResult();
    }

    /// <summary>Cash out of the drawer (collection, supplier) during the open shift, with a reason.</summary>
    [HttpPost("{id:long}/payout")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<LedgerEntryView>> Payout(long id, [FromBody] CashPayoutRequest req)
    {
        var result = await Service.PayoutAsync(id, req.Amount, req.Reason);
        return result.Ok ? Ok(LedgerEntryView.From(result.Value!)) : result.Error!.ToActionResult();
    }

    /// <summary>Shifts, newest first (Owner, Manager: all; Cashier: own shifts). Paging with beforeId.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ShiftView>>> List([FromQuery] int take = 50, [FromQuery] long? beforeId = null)
    {
        var q = _db.Shifts.AsNoTracking().AsQueryable();
        if (!IsManager)
        {
            var me = MyId;
            q = q.Where(s => s.OpenedById == me);
        }
        if (beforeId.HasValue) q = q.Where(s => s.Id < beforeId.Value);
        var shifts = await q.OrderByDescending(s => s.Id).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        return Ok(shifts.Select(s => ShiftView.From(s, hideExpected: !IsManager && s.Status == ShiftStatuses.Open)));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ShiftView>> Get(long id)
    {
        var shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (shift == null) return NotFound();
        if (!IsManager && shift.OpenedById != MyId) return Forbid();
        return Ok(ShiftView.From(shift, hideExpected: !IsManager && shift.Status == ShiftStatuses.Open));
    }

    /// <summary>
    /// Z-report of a shift from its ledger rows (an open shift: the report so far). Owner, Manager:
    /// any shift; Cashier: own shifts after the close.
    /// </summary>
    [HttpGet("{id:long}/report")]
    public async Task<ActionResult<ZReport>> Report(long id)
    {
        var (shift, error) = await ReportShiftAsync(id);
        if (shift == null) return error!;
        return Ok(await ZReport.BuildAsync(_db, shift, _clock));
    }

    /// <summary>The Z-report as CSV (UTF-8 with BOM). lang: uk (default) or en; sep: semicolon (default, decimal comma) or comma.</summary>
    [HttpGet("{id:long}/report.csv")]
    public async Task<IActionResult> ReportCsv(long id, [FromQuery] string? lang = null, [FromQuery] string? sep = null)
    {
        var (shift, error) = await ReportShiftAsync(id);
        if (shift == null) return error!;
        var report = await ZReport.BuildAsync(_db, shift, _clock);
        return File(CsvExport.ZReport(report, _clock, await _db.CurrencyAsync(), lang, sep), CsvExport.ContentType, $"z-report-shift-{id}.csv");
    }

    private async Task<(ShiftEntity? Shift, ActionResult? Error)> ReportShiftAsync(long id)
    {
        var shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (shift == null) return (null, ShiftErrors.NotFound.ToActionResult());
        if (IsManager) return (shift, null);
        if (shift.OpenedById != MyId) return (null, Forbid());
        if (shift.Status == ShiftStatuses.Open) return (null, ShiftErrors.ReportAfterClose.ToActionResult());
        return (shift, null);
    }
}

public record OpenShiftRequest(decimal OpeningFloat, string? Note = null);
public record CloseShiftRequest(decimal CountedCash, string? Note = null);
public record CashPayoutRequest(decimal Amount, string? Reason);

public record ShiftView(
    long Id, string Status, DateTimeOffset OpenedUtc, string? OpenedBy, decimal OpeningFloat, string? OpenNote,
    DateTimeOffset? ClosedUtc, string? ClosedBy, decimal? CountedCash, decimal? ExpectedCash, decimal? Variance, string? CloseNote,
    long? AuditHeadId, string? AuditHeadHash)
{
    public static ShiftView From(ShiftEntity s, bool hideExpected = false) => new(
        s.Id, s.Status, s.OpenedUtc, s.OpenedByUsername, s.OpeningFloat, s.OpenNote,
        s.ClosedUtc, s.ClosedByUsername, s.CountedCash, hideExpected ? null : s.ExpectedCash, hideExpected ? null : s.Variance, s.CloseNote,
        s.AuditHeadId, s.AuditHeadHash);
}

/// <summary>
/// Blind: the caller is a cashier and the shift is open, so the expected cash is not shown before the
/// count. RequireOpenShift: cash and card payments need an open shift (server setting).
/// </summary>
public record CurrentShiftView(ShiftView? Shift, decimal? ExpectedCash, bool Blind, decimal? PreviousCountedCash, long? PreviousShiftId, bool RequireOpenShift);
