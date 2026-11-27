using System.Globalization;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Events;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Events and tournaments (spec area 9). Front desk (Owner, Manager, Cashier) reads them, registers and withdraws
/// teams, enters match results and starts the session of a player again; Owner and Manager create, move, start,
/// finish and cancel events and create the bracket. Times in and out are club time.
/// </summary>
[ApiController]
[Route("api/events")]
[Authorize(Policy = Policies.FrontDesk)]
public class EventsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public EventsController(CmsDbContext db, IDeviceRepository devices, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _devices = devices;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private EventService Service => new(_db, _devices, _clock, _audit, StaffActor.From(HttpContext?.User));

    /// <summary>The events of the last 60 days and all later ones, the newest start first.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<EventSummaryView>>> List()
    {
        var from = Cms.Server.Reservations.ReservationService.DayOf(_clock.UtcNow, _clock) - 60;
        var rows = (await _db.Events.AsNoTracking().Where(e => e.StartDay >= from).ToListAsync()).OrderByDescending(e => e.StartUtc).ToList();
        return Ok(await EventViews.SummariesAsync(_db, _clock, rows));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<EventDetailView>> Get(long id)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return NotFound();
        return Ok(await EventViews.DetailAsync(_db, _clock, e));
    }

    private async Task<ActionResult> DetailOrError<T>(long id, BillingResult<T> result) =>
        result.Ok ? Ok(await EventViews.DetailAsync(_db, _clock, await _db.Events.AsNoTracking().FirstAsync(x => x.Id == id))) : result.Error!.ToActionResult();

    [HttpPost]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Create([FromBody] CreateEventRequest req)
    {
        var result = await Service.CreateAsync(req);
        _db.ChangeTracker.Clear();
        return result.Ok ? await DetailOrError(result.Value!.Id, result) : result.Error!.ToActionResult();
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Update(long id, [FromBody] UpdateEventRequest req) => await DetailOrError(id, await Service.UpdateAsync(id, req));

    [HttpPost("{id:long}/start")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Start(long id) => await DetailOrError(id, await Service.StartAsync(id));

    [HttpPost("{id:long}/finish")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Finish(long id) => await DetailOrError(id, await Service.FinishAsync(id, early: true));

    [HttpPost("{id:long}/cancel")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Cancel(long id, [FromBody] EventReasonRequest? req) => await DetailOrError(id, await Service.CancelAsync(id, req?.Reason));

    [HttpPost("{id:long}/bracket")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> CreateBracket(long id, [FromBody] BracketRequest? req) => await DetailOrError(id, await Service.CreateBracketAsync(id, req?.Shuffle ?? false));

    [HttpPost("{id:long}/teams")]
    public async Task<ActionResult> Register(long id, [FromBody] RegisterTeamRequest req) => await DetailOrError(id, await Service.RegisterAsync(id, req));

    [HttpPost("{id:long}/teams/{teamId:long}/withdraw")]
    public async Task<ActionResult> Withdraw(long id, long teamId) => await DetailOrError(id, await Service.WithdrawAsync(id, teamId));

    [HttpPost("{id:long}/players/{playerId:long}/start")]
    public async Task<ActionResult> StartPlayer(long id, long playerId) => await DetailOrError(id, await Service.StartPlayerAsync(id, playerId));

    [HttpPut("{id:long}/matches/{matchId:long}")]
    public async Task<ActionResult> Result(long id, long matchId, [FromBody] MatchResultRequest req) => await DetailOrError(id, await Service.SetResultAsync(id, matchId, req));
}

public record EventSummaryView(long Id, string Name, string Status, Guid? ZoneId, string? ZoneName, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string Date, string StartLocal, string EndLocal, int DurationMinutes, string PriceMode, decimal Price, decimal EntryFee, int TeamSize,
    int Pcs, int Teams, int Players, int MaxTeams, long? WinnerTeamId, string? WinnerName, string? CancelReason, DateTimeOffset? StartedUtc, DateTimeOffset? EndedUtc);

public record EventPcView(Guid Id, string Hostname, long? PlayerId, string? Username, bool SessionActive);
public record EventPlayerView(long Id, Guid UserId, string Username, string DisplayName, Guid? DeviceId, string? Hostname, Guid? SessionId, bool SessionActive,
    decimal Fee, string FeeStatus, string? StartError, bool Captain);
public record EventTeamView(long Id, string Name, int Seed, string Status, IReadOnlyList<EventPlayerView> Players);
public record EventMatchView(long Id, int Round, int Slot, long? TeamAId, long? TeamBId, long? WinnerTeamId, int? ScoreA, int? ScoreB, bool Bye);
public record EventDetailView(EventSummaryView Event, IReadOnlyList<EventPcView> Pcs, IReadOnlyList<EventTeamView> Teams, IReadOnlyList<EventMatchView> Matches,
    int Rounds, decimal FeesHeld);

public static class EventViews
{
    private static string Local(ClubClock clock, DateTimeOffset utc, string format) => clock.ToLocal(utc).ToString(format, CultureInfo.InvariantCulture);

    public static async Task<List<EventSummaryView>> SummariesAsync(CmsDbContext db, ClubClock clock, IReadOnlyList<EventEntity> rows)
    {
        var ids = rows.Select(e => e.Id).ToList();
        var pcs = (await db.EventDevices.AsNoTracking().Where(d => ids.Contains(d.EventId)).ToListAsync()).GroupBy(d => d.EventId).ToDictionary(g => g.Key, g => g.Count());
        var teams = await db.EventTeams.AsNoTracking().Where(t => ids.Contains(t.EventId)).ToListAsync();
        var players = (await db.EventPlayers.AsNoTracking().Where(p => ids.Contains(p.EventId) && p.Status == EventMemberStatuses.Active).ToListAsync())
            .GroupBy(p => p.EventId).ToDictionary(g => g.Key, g => g.Count());
        var zoneIds = rows.Where(e => e.ZoneId.HasValue).Select(e => e.ZoneId!.Value).Distinct().ToList();
        var zones = await db.Zones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => z.Name);
        return rows.Select(e =>
        {
            var count = pcs.GetValueOrDefault(e.Id);
            return new EventSummaryView(e.Id, e.Name, e.Status, e.ZoneId, e.ZoneId is { } z && zones.TryGetValue(z, out var zn) ? zn : null,
                e.StartUtc, e.EndUtc, Local(clock, e.StartUtc, "yyyy-MM-dd"), Local(clock, e.StartUtc, "HH:mm"), Local(clock, e.EndUtc, "HH:mm"),
                e.DurationMinutes, e.PriceMode, e.Price, e.EntryFee, e.TeamSize, count,
                teams.Count(t => t.EventId == e.Id && t.Status == EventMemberStatuses.Active), players.GetValueOrDefault(e.Id),
                e.TeamSize > 0 ? count / e.TeamSize : 0, e.WinnerTeamId, teams.FirstOrDefault(t => t.Id == e.WinnerTeamId)?.Name, e.CancelReason, e.StartedUtc, e.EndedUtc);
        }).ToList();
    }

    public static async Task<EventDetailView> DetailAsync(CmsDbContext db, ClubClock clock, EventEntity e)
    {
        var summary = (await SummariesAsync(db, clock, new[] { e })).Single();
        var pcs = (await db.Devices.AsNoTracking().Where(d => db.EventDevices.Any(x => x.EventId == e.Id && x.DeviceId == d.Id)).ToListAsync())
            .OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
        var teams = (await db.EventTeams.AsNoTracking().Where(t => t.EventId == e.Id).ToListAsync()).OrderBy(t => t.Status != EventMemberStatuses.Active).ThenBy(t => t.Seed).ToList();
        var players = await db.EventPlayers.AsNoTracking().Where(p => p.EventId == e.Id).OrderBy(p => p.Id).ToListAsync();
        var userIds = players.Select(p => p.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var sessionIds = players.Where(p => p.SessionId.HasValue).Select(p => p.SessionId!.Value).ToList();
        var active = (await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id) && s.Status == "active").Select(s => s.Id).ToListAsync()).ToHashSet();
        var hosts = pcs.ToDictionary(p => p.Id, p => p.Hostname);
        EventPlayerView View(EventPlayerEntity p, EventTeamEntity team) => new(p.Id, p.UserId,
            users.TryGetValue(p.UserId, out var u) ? u.Username : "?", u?.DisplayName ?? "", p.DeviceId,
            p.DeviceId is { } d && hosts.TryGetValue(d, out var h) ? h : null, p.SessionId, p.SessionId is { } s && active.Contains(s),
            p.Fee, p.FeeStatus, p.StartError, team.CaptainUserId == p.UserId);
        var teamViews = teams.Select(t => new EventTeamView(t.Id, t.Name, t.Seed, t.Status,
            players.Where(p => p.TeamId == t.Id).Select(p => View(p, t)).ToList())).ToList();
        var seated = players.Where(p => p.Status == EventMemberStatuses.Active && p.DeviceId.HasValue).ToDictionary(p => p.DeviceId!.Value);
        var pcViews = pcs.Select(pc => seated.TryGetValue(pc.Id, out var p)
            ? new EventPcView(pc.Id, pc.Hostname, p.Id, users.TryGetValue(p.UserId, out var u) ? u.Username : "?", p.SessionId is { } s && active.Contains(s))
            : new EventPcView(pc.Id, pc.Hostname, null, null, false)).ToList();
        var matches = (await db.EventMatches.AsNoTracking().Where(m => m.EventId == e.Id).ToListAsync()).OrderBy(m => m.Round).ThenBy(m => m.Slot)
            .Select(m => new EventMatchView(m.Id, m.Round, m.Slot, m.TeamAId, m.TeamBId, m.WinnerTeamId, m.ScoreA, m.ScoreB, EventService.ToBracket(m).IsBye)).ToList();
        var held = players.Where(p => p.FeeStatus == EventFeeStatuses.Held).Sum(p => p.Fee);
        return new EventDetailView(summary, pcViews, teamViews, matches, matches.Count == 0 ? 0 : matches.Max(m => m.Round), held);
    }
}
