using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Sessions: start (tariff or package), end, extend, pause / resume, transfer to another PC.
/// The rules and the money handling are in Billing/SessionService.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.FrontDesk)]
public class SessionsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly AuditLogger? _audit;
    private readonly ClubClock _clock;

    public SessionsController(CmsDbContext db, IDeviceRepository devices, AuditLogger? audit = null, ClubClock? clock = null)
    {
        _db = db;
        _devices = devices;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private SessionService Service => new(_db, _devices, _clock, _audit, StaffActor.From(HttpContext?.User))
    {
        CanGiveFreeTime = HttpContext?.User is { } u && (u.IsInRole(StaffRoles.Owner) || u.IsInRole(StaffRoles.Manager)),
        CanOverrideReservation = HttpContext?.User is { } m && (m.IsInRole(StaffRoles.Owner) || m.IsInRole(StaffRoles.Manager))
    };

    /// <summary>
    /// Start a session:
    ///  - packageId: a package of the PC's zone (fixed price; ends after its duration or at its cut-off);
    ///  - durationMinutes: prepaid tariff time (price per time-of-day rule, taken now);
    ///  - neither: open-ended, billed at the end.
    /// paymentMethod: balance (default for players; bonus first unless useBonus = false), cash or card
    /// (at the desk; walk-in guests always pay at the desk, default cash).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SessionDetailView>> Start([FromBody] StartSessionRequest req)
    {
        var result = await Service.StartAsync(req);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok((await ViewsAsync(new[] { result.Value! })).Single());
    }

    /// <summary>
    /// End a session. Open-ended sessions are billed now per segment and time-of-day rule, taken from
    /// the player balances (never below zero); the rest is due at the desk (AmountDue), paid with
    /// deskMethod cash (default) or card, or recorded as the player's debt with deskMethod debt
    /// (DebtRecorded); paymentMethod cash | card puts everything at the desk.
    /// Ending the same session twice at once charges once.
    /// </summary>
    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<SessionDetailView>> End(Guid id, [FromBody] EndSessionRequest? req = null)
    {
        var result = await Service.EndAsync(id, req);
        if (!result.Ok) return result.Error!.ToActionResult();
        var view = (await ViewsAsync(new[] { result.Value!.Session })).Single();
        return Ok(view with
        {
            AmountDue = result.Value.AmountDue,
            PaidFromBalance = result.Value.FromBalances.FromBalance,
            PaidFromBonus = result.Value.FromBalances.FromBonus,
            DebtRecorded = result.Value.Debt
        });
    }

    /// <summary>Extend a prepaid or package session by N minutes at its tariff (never past a package cut-off).</summary>
    [HttpPost("{id:guid}/extend")]
    public async Task<ActionResult<SessionDetailView>> Extend(Guid id, [FromBody] ExtendSessionRequest req)
    {
        var result = await Service.ExtendAsync(id, req);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok((await ViewsAsync(new[] { result.Value! })).Single());
    }

    /// <summary>Pause: the time stops and the PC locks.</summary>
    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<SessionDetailView>> Pause(Guid id)
    {
        var result = await Service.PauseAsync(id);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok((await ViewsAsync(new[] { result.Value! })).Single());
    }

    /// <summary>Resume: the time runs again; the end moves by the paused time (never past a cut-off).</summary>
    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<SessionDetailView>> Resume(Guid id)
    {
        var result = await Service.ResumeAsync(id);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok((await ViewsAsync(new[] { result.Value! })).Single());
    }

    /// <summary>Price difference of a move to another PC, without changing anything.</summary>
    [HttpGet("{id:guid}/transfer-quote")]
    public async Task<ActionResult<TransferQuote>> TransferQuote(Guid id, [FromQuery] Guid targetDeviceId)
    {
        var result = await Service.QuoteTransferAsync(id, targetDeviceId);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok(result.Value);
    }

    /// <summary>Move an active session to a free PC (same zone, or another zone with the price difference).</summary>
    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<SessionDetailView>> Transfer(Guid id, [FromBody] TransferSessionRequest req)
    {
        var result = await Service.TransferAsync(id, req);
        if (!result.Ok) return result.Error!.ToActionResult();
        var view = (await ViewsAsync(new[] { result.Value.Session })).Single();
        return Ok(view with { Surcharge = result.Value.Quote.Surcharge });
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SessionDetailView>>> List([FromQuery] string? status)
    {
        var q = _db.Sessions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(s => s.Status == status);

        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var sessions = (await q.ToListAsync()).OrderByDescending(s => s.StartUtc).Take(200).ToList();
        return Ok(await ViewsAsync(sessions));
    }

    [HttpGet("active")]
    public async Task<ActionResult<IEnumerable<SessionDetailView>>> Active()
    {
        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var sessions = (await _db.Sessions.AsNoTracking()
            .Where(s => s.Status == "active")
            .ToListAsync())
            .OrderByDescending(s => s.StartUtc).ToList();
        return Ok(await ViewsAsync(sessions));
    }

    /// <summary>
    /// Revenue (PC time + bar net, from the ledger) of today, the last 7 and the last 30 club days: the
    /// same numbers as the analytics (GET /api/analytics). Until phase 4 it was the cost of the ended
    /// sessions per UTC day, week and calendar month.
    /// </summary>
    [HttpGet("revenue")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<RevenueSummary>> Revenue()
    {
        var (today, week, month) = await Cms.Server.Analytics.AnalyticsService.RevenueAsync(_db, _clock, HttpContext?.RequestAborted ?? CancellationToken.None);
        return Ok(new RevenueSummary(today, week, month, await _db.CurrencySymbolAsync()));
    }

    /// <summary>Views with names; active open-ended sessions get their cost so far.</summary>
    private async Task<List<SessionDetailView>> ViewsAsync(IReadOnlyList<SessionEntity> sessions)
    {
        var tariffIds = sessions.Where(s => s.TariffId.HasValue).Select(s => s.TariffId!.Value).Distinct().ToList();
        var tariffs = await _db.Tariffs.AsNoTracking().Where(t => tariffIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var userIds = sessions.Where(s => s.UserId.HasValue).Select(s => s.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var deviceIds = sessions.Select(s => s.DeviceId).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id);
        var packageIds = sessions.Where(s => s.PackageId.HasValue).Select(s => s.PackageId!.Value).Distinct().ToList();
        var packages = await _db.Packages.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var now = _clock.UtcNow;
        var service = new SessionService(_db, _devices, _clock);
        var views = new List<SessionDetailView>(sessions.Count);
        foreach (var s in sessions)
        {
            var tariff = s.TariffId.HasValue && tariffs.TryGetValue(s.TariffId.Value, out var t) ? t : null;
            decimal? currentCost = null;
            if (s.Status == "active" && !s.IsPrepaid)
                currentCost = (await service.QuotePlayedAsync(s, null, s.PausedUtc ?? now))?.Total;
            views.Add(new SessionDetailView(
                s.Id, s.DeviceId, devices.TryGetValue(s.DeviceId, out var d) ? d.Hostname : null,
                s.TariffId, tariff?.Name, tariff?.PricePerHour,
                s.UserId, s.UserId.HasValue && users.TryGetValue(s.UserId.Value, out var u) ? u.Username : null,
                s.StartUtc, s.EndUtc, s.Status, s.TotalCost, s.IsPrepaid)
            {
                PackageId = s.PackageId,
                PackageName = s.PackageId.HasValue && packages.TryGetValue(s.PackageId.Value, out var p) ? p.Name : null,
                PausedUtc = s.PausedUtc,
                CutoffUtc = s.CutoffUtc,
                PaymentMethod = s.PaymentMethod,
                PaidMinutes = s.PaidMinutes,
                CurrentCost = currentCost,
                LimitToBalance = s.LimitToBalance,
                BalanceEndUtc = s.BalanceEndUtc
            });
        }
        return views;
    }
}

/// <summary>
/// DurationMinutes: prepaid tariff minutes (null/0 = open-ended); PackageId: a package instead of a tariff.
/// PaymentMethod: balance | cash | card (default: balance for players, cash for guests), or free
/// (Owner, Manager; prepaid only; Reason required; audited as session.free_time).
/// UseBonus: spend the bonus balance first (PC time only).
/// </summary>
/// LimitToBalance: an open-ended session of a player that ends when the balances are used up (player login).
/// OverrideReservation: start on a PC held for a reservation of someone else (Owner, Manager; audited).
public record StartSessionRequest(Guid DeviceId, Guid? TariffId, Guid? UserId, int? DurationMinutes,
    Guid? PackageId = null, string? PaymentMethod = null, bool UseBonus = true, string? Reason = null, bool LimitToBalance = false,
    bool OverrideReservation = false);
/// <summary>PaymentMethod as for the start; free needs Owner or Manager and a Reason.</summary>
public record ExtendSessionRequest(int Minutes, string? PaymentMethod = null, bool UseBonus = true, string? Reason = null);
/// <summary>
/// Open-ended sessions: PaymentMethod balance (default for players: bonus first unless UseBonus = false,
/// then the real balance; the rest at the desk with DeskMethod cash | card, default cash, or with
/// DeskMethod debt on the player account) or cash | card (all at the desk; guests always).
/// </summary>
public record EndSessionRequest(string? PaymentMethod = null, bool UseBonus = true, string? DeskMethod = null);
public record TransferSessionRequest(Guid TargetDeviceId, string? PaymentMethod = null, bool UseBonus = true, bool OverrideReservation = false);

public record SessionDetailView(
    Guid Id, Guid DeviceId, string? DeviceHostname,
    Guid? TariffId, string? TariffName, decimal? PricePerHour,
    Guid? UserId, string? Username,
    DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string Status,
    decimal TotalCost, bool IsPrepaid,
    decimal AmountDue = 0) // End: part of the cost that the balance did not cover (pay at the desk)
{
    public Guid? PackageId { get; init; }
    public string? PackageName { get; init; }
    /// <summary>Set while paused (the time stands still; remaining = EndUtc - PausedUtc).</summary>
    public DateTimeOffset? PausedUtc { get; init; }
    /// <summary>Hard end of a package window (night pack).</summary>
    public DateTimeOffset? CutoffUtc { get; init; }
    public string? PaymentMethod { get; init; }
    public int? PaidMinutes { get; init; }
    /// <summary>Active open-ended sessions: price of the time played so far.</summary>
    public decimal? CurrentCost { get; init; }
    /// <summary>Open-ended session that ends when the balances are used up, and when that is at the current prices.</summary>
    public bool LimitToBalance { get; init; }
    public DateTimeOffset? BalanceEndUtc { get; init; }
    public decimal PaidFromBalance { get; init; }
    public decimal PaidFromBonus { get; init; }
    /// <summary>Transfer: price difference paid for the move.</summary>
    public decimal Surcharge { get; init; }
    /// <summary>End with deskMethod debt: the part recorded on the player account.</summary>
    public decimal DebtRecorded { get; init; }
}

/// <summary>Today, Week = the last 7 club days, Month = the last 30 club days; Currency = the symbol.</summary>
public record RevenueSummary(decimal Today, decimal Week, decimal Month, string Currency);
