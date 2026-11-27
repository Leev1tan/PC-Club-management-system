using System.Globalization;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Reservations by staff (spec area 7): the day timeline, create, move, confirm or decline a request, cancel,
/// check in; the settings (hold, grace period, deposit of a no-show). Front desk (Owner, Manager, Cashier);
/// the settings and the override of a hold need Owner or Manager. Times in and out are club time.
/// </summary>
[ApiController]
[Route("api/reservations")]
[Authorize(Policy = Policies.FrontDesk)]
public class ReservationsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public ReservationsController(CmsDbContext db, IDeviceRepository devices, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _devices = devices;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private bool IsManagement => User.IsInRole(StaffRoles.Owner) || User.IsInRole(StaffRoles.Manager);

    private ReservationService Service => new(_db, _devices, _clock, _audit, StaffActor.From(HttpContext?.User)) { CanGiveFreeTime = IsManagement };

    /// <summary>The day (club date, default today): zones with their PCs, the reservations that touch the day, and the open requests.</summary>
    [HttpGet]
    public async Task<ActionResult<ReservationDayView>> Day([FromQuery] string? date)
    {
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(_clock.ToLocal(now).DateTime);
        DateOnly day = today;
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            return ReservationErrors.BadTime.ToActionResult();
        var settings = await ReservationSettings.LoadAsync(_db);
        var from = ClubTimeZone.LocalToUtc(day.ToDateTime(TimeOnly.MinValue), _clock.Zone);
        var to = ClubTimeZone.LocalToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), _clock.Zone);
        var rows = (await _db.Reservations.AsNoTracking().Where(r => r.StartDay >= day.DayNumber - 1 && r.StartDay <= day.DayNumber).ToListAsync())
            .Where(r => r.StartUtc < to && from < r.EndUtc).OrderBy(r => r.StartUtc).ToList();
        var requests = (await _db.Reservations.AsNoTracking().Where(r => r.Status == ReservationStatuses.Requested).ToListAsync())
            .Where(r => r.EndUtc > now).OrderBy(r => r.StartUtc).ToList();

        var zones = await _db.Zones.AsNoTracking().OrderBy(z => z.SortOrder).ThenBy(z => z.Name).ToListAsync();
        var pcs = await _db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatuses.Approved).ToListAsync();
        var lanes = zones.Select(z => new ZoneLane(z.Id, z.Name, z.Color,
                pcs.Where(p => p.ZoneId == z.Id).OrderBy(p => p.Hostname, StringComparer.OrdinalIgnoreCase).Select(p => new PcLane(p.Id, p.Hostname)).ToList()))
            .Where(z => z.Pcs.Count > 0).ToList();
        var unzoned = pcs.Where(p => p.ZoneId == null || zones.All(z => z.Id != p.ZoneId)).OrderBy(p => p.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
        if (unzoned.Count > 0) lanes.Add(new ZoneLane(null, "", "#8b8fa3", unzoned.Select(p => new PcLane(p.Id, p.Hostname)).ToList()));

        var views = await ReservationViews.ManyAsync(_db, _clock, settings, rows.Concat(requests).DistinctBy(r => r.Id).ToList(), day);
        var nowMinute = day == today ? (int)(_clock.ToLocal(now).DateTime - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes : (int?)null;
        return Ok(new ReservationDayView(day.ToString("yyyy-MM-dd"), ClubTimeZone.IanaId(_clock.Zone), settings.HoldMinutes, settings.GraceMinutes,
            settings.NoShowKeepsDeposit, lanes, views.Where(v => rows.Any(r => r.Id == v.Id)).ToList(),
            views.Where(v => v.Status == ReservationStatuses.Requested && requests.Any(r => r.Id == v.Id)).ToList(), nowMinute));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ReservationView>> Get(long id)
    {
        var r = await _db.Reservations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();
        return Ok(await OneAsync(r));
    }

    private async Task<ReservationView> OneAsync(ReservationEntity r)
    {
        var settings = await ReservationSettings.LoadAsync(_db);
        var day = DateOnly.FromDateTime(_clock.ToLocal(r.StartUtc).DateTime);
        return (await ReservationViews.ManyAsync(_db, _clock, settings, new[] { r }, day)).Single();
    }

    private async Task<ActionResult<ReservationView>> ResultAsync(BillingResult<ReservationEntity> result) =>
        result.Ok ? Ok(await OneAsync(result.Value!)) : result.Error!.ToActionResult();

    [HttpPost]
    public async Task<ActionResult<ReservationView>> Create([FromBody] CreateReservationRequest req) => await ResultAsync(await Service.CreateAsync(req));

    [HttpPut("{id:long}")]
    public async Task<ActionResult<ReservationView>> Update(long id, [FromBody] UpdateReservationRequest req) => await ResultAsync(await Service.UpdateAsync(id, req));

    [HttpPost("{id:long}/confirm")]
    public async Task<ActionResult<ReservationView>> Confirm(long id, [FromBody] ConfirmReservationRequest? req) =>
        await ResultAsync(await Service.ConfirmAsync(id, req ?? new ConfirmReservationRequest()));

    [HttpPost("{id:long}/decline")]
    public async Task<ActionResult<ReservationView>> Decline(long id, [FromBody] ReasonRequest? req) => await ResultAsync(await Service.DeclineAsync(id, req?.Reason));

    [HttpPost("{id:long}/cancel")]
    public async Task<ActionResult<ReservationView>> Cancel(long id, [FromBody] ReasonRequest? req) => await ResultAsync(await Service.CancelAsync(id, req?.Reason));

    /// <summary>Starts the session of the reservation (see ReservationService.CheckInAsync).</summary>
    [HttpPost("{id:long}/check-in")]
    public async Task<ActionResult<ReservationView>> CheckIn(long id, [FromBody] CheckInRequest? req)
    {
        var result = await Service.CheckInAsync(id, req ?? new CheckInRequest());
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok(await OneAsync(result.Value.Reservation));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ReservationSettings>> GetSettings() => Ok(await ReservationSettings.LoadAsync(_db));

    /// <summary>Hold minutes, grace period, deposit of a no-show (Owner, Manager; audited).</summary>
    [HttpPut("settings")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<ReservationSettings>> PutSettings([FromBody] ReservationSettings settings)
    {
        if (settings.Validate() is { } error) return ReservationErrors.BadSettings.With(error).ToActionResult();
        var before = await ReservationSettings.SaveAsync(_db, settings, User.Identity?.Name, _clock.UtcNow);
        if (before != settings)
            await _audit.WriteAsync(AuditActions.ReservationSettingsChanged, "setting", ClubSettingKeys.Reservations,
                $"Reservation settings changed: hold {before.HoldMinutes} -> {settings.HoldMinutes} min, grace {before.GraceMinutes} -> {settings.GraceMinutes} min, " +
                $"no-show deposit {(before.NoShowKeepsDeposit ? "kept" : "returned")} -> {(settings.NoShowKeepsDeposit ? "kept" : "returned")}",
                new { before, after = settings });
        return Ok(settings);
    }
}

public record PcLane(Guid Id, string Hostname);
public record ZoneLane(Guid? Id, string Name, string Color, IReadOnlyList<PcLane> Pcs);
/// <summary>NowMinute: minutes since the club midnight when the day is today (the "now" line), else null.</summary>
public record ReservationDayView(string Date, string TimeZone, int HoldMinutes, int GraceMinutes, bool NoShowKeepsDeposit,
    IReadOnlyList<ZoneLane> Zones, IReadOnlyList<ReservationView> Reservations, IReadOnlyList<ReservationView> Requests, int? NowMinute);

/// <summary>
/// A reservation for staff (the name and the phone are personal data: staff only). Times as club time: Date,
/// StartLocal, EndLocal; StartMinute = minutes from the club midnight of the day that was asked for (the timeline).
/// Held = the PC is kept for it now; CanCheckIn = from the hold until the grace period ends.
/// </summary>
public record ReservationView(long Id, string Status, string Source, Guid? DeviceId, string? DeviceHostname, Guid? ZoneId, string? ZoneName,
    Guid? AssignedDeviceId, string? AssignedHostname, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Date, string StartLocal, string EndLocal,
    int StartMinute, int DurationMinutes, Guid? UserId, string? Username, string? GuestName, string? Phone, decimal Deposit, string DepositStatus,
    Guid? SessionId, string? Note, string? CreatedByUsername, DateTimeOffset CreatedUtc, string? CancelReason, bool Held, bool CanCheckIn,
    string HoldFromLocal, string NoShowAtLocal, bool Erased);

public static class ReservationViews
{
    public static async Task<List<ReservationView>> ManyAsync(CmsDbContext db, ClubClock clock, ReservationSettings settings, IReadOnlyList<ReservationEntity> rows, DateOnly day)
    {
        var deviceIds = rows.SelectMany(r => new[] { r.DeviceId, r.AssignedDeviceId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var hosts = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Hostname);
        var zoneIds = rows.Where(r => r.ZoneId.HasValue).Select(r => r.ZoneId!.Value).Distinct().ToList();
        var zones = await db.Zones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => z.Name);
        var userIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username);
        var now = clock.UtcNow;
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        string L(DateTimeOffset utc, string f = "HH:mm") => clock.ToLocal(utc).ToString(f, CultureInfo.InvariantCulture);
        return rows.Select(r =>
        {
            var held = ReservationHolds.IsHeld(r, now, settings);
            return new ReservationView(r.Id, r.Status, r.Source, r.DeviceId, r.DeviceId is { } d && hosts.TryGetValue(d, out var h) ? h : null,
                r.ZoneId, r.ZoneId is { } z && zones.TryGetValue(z, out var zn) ? zn : null,
                r.AssignedDeviceId, r.AssignedDeviceId is { } a && hosts.TryGetValue(a, out var ah) ? ah : null,
                r.StartUtc, r.EndUtc, L(r.StartUtc, "yyyy-MM-dd"), L(r.StartUtc), L(r.EndUtc),
                (int)Math.Round((clock.ToLocal(r.StartUtc).DateTime - midnight).TotalMinutes), r.DurationMinutes,
                r.UserId, r.UserId is { } u && users.TryGetValue(u, out var un) ? un : null, r.GuestName, r.Phone, r.Deposit, r.DepositStatus,
                r.SessionId, r.Note, r.CreatedByUsername, r.CreatedUtc, r.CancelReason, held, held,
                L(r.StartUtc.AddMinutes(-settings.HoldMinutes)), L(r.StartUtc.AddMinutes(settings.GraceMinutes)), r.ErasedUtc.HasValue);
        }).ToList();
    }
}
