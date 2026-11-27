using System.Globalization;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Events;

public static class EventErrors
{
    private const int NotFound = StatusCodes.Status404NotFound;
    private const int Conflict = StatusCodes.Status409Conflict;

    public static readonly BillingError NotFoundError = new("event_not_found", "Event not found", NotFound);
    public static readonly BillingError BadName = new("event_bad_name", "Enter a name: 1 to 80 characters.");
    public static readonly BillingError BadTime = new("event_bad_time", "Choose a start in club time (date YYYY-MM-DD, time HH:mm), from now up to 90 days ahead.");
    public static readonly BillingError BadDuration = new("event_bad_duration", "The duration must be 30 minutes to 12 hours, in steps of 15 minutes.");
    public static readonly BillingError BadPrice = new("event_bad_price", "The PC time is free, or a fixed price from 0.01 to 10000 per player.");
    public static readonly BillingError BadFee = new("event_bad_fee", "The entry fee must be 0 to 10000.");
    public static readonly BillingError BadTeamSize = new("event_bad_team_size", "A team has 1 to 10 players.");
    public static readonly BillingError NeedsPcs = new("event_needs_pcs", "Choose a zone or PCs: at least as many approved PCs as one team has players (at most 100).");
    public static readonly BillingError NotOpen = new("event_not_open", "The event is {0}; this is not possible now.", Conflict);
    public static readonly BillingError EventOver = new("event_over", "The event is over.", Conflict);
    public static readonly BillingError Full = new("event_full", "The event has no free PC for {0} more player(s).", Conflict);
    public static readonly BillingError BadTeam = new("event_bad_team", "A team needs exactly {0} different registered player(s) and a name (1 to 40 characters).");
    public static readonly BillingError PlayerRegistered = new("event_player_registered", "@{0} is already registered for this event.", Conflict);
    public static readonly BillingError InsufficientFee = new("insufficient_balance_fee", "@{0} has not enough real balance for the entry fee: need {1}, have {2}.", Conflict);
    public static readonly BillingError TeamNotFound = new("event_team_not_found", "Team not found", NotFound);
    public static readonly BillingError PlayerNotFound = new("event_player_not_found", "Player of the event not found", NotFound);
    public static readonly BillingError BracketNeedsTeams = new("bracket_needs_teams", "A bracket needs at least two teams.", Conflict);
    public static readonly BillingError BracketHasResults = new("bracket_has_results", "The bracket has results. Clear them first.", Conflict);
    public static readonly BillingError MatchNotFound = new("match_not_found", "Match not found", NotFound);
    public static readonly BillingError MatchRule = new("bracket_rule", "{0}", Conflict);
    public static readonly BillingError SeatRunning = new("event_seat_running", "The session of this player runs already.", Conflict);
    public static readonly BillingError SeatFailed = new("event_seat_failed", "The session of @{0} did not start: {1}.", Conflict);
    public static readonly BillingError PcEvent = new("pc_event", "This PC is booked for the event «{0}» at {1}. A manager can override it.", Conflict);
    public static readonly BillingError Changed = new("event_changed", "The event was changed at the same time. Reload and try again.", Conflict);
}

public sealed record CreateEventRequest(string? Name, Guid? ZoneId, IReadOnlyList<Guid>? DeviceIds, string? Date, string? Time, int DurationMinutes,
    string? PriceMode = null, decimal Price = 0m, decimal EntryFee = 0m, int TeamSize = 1);
public sealed record UpdateEventRequest(string? Name, string? Date, string? Time, int DurationMinutes);
/// <summary>PlayerIds: exactly TeamSize registered players; CaptainId: one of them (default the first); Name: optional for TeamSize 1.</summary>
public sealed record RegisterTeamRequest(string? Name, IReadOnlyList<Guid>? PlayerIds, Guid? CaptainId = null);
/// <summary>WinnerTeamId null clears the result.</summary>
public sealed record MatchResultRequest(long? WinnerTeamId, int? ScoreA = null, int? ScoreB = null);
public sealed record BracketRequest(bool Shuffle = false);
public sealed record EventReasonRequest(string? Reason = null);
public sealed record EventJobResult(int Started, int Finished, int Cancelled, int Seats);
/// <summary>The start of an event: how many sessions started now and which players have none (error codes).</summary>
public sealed record EventStartResult(int Started, int Failed);

/// <summary>
/// Events and tournaments (spec area 9). Booking changes (create, move, register, withdraw, cancel) run under the
/// lock of the reservations, so an event never takes a PC that a reservation or another event has for that time,
/// and two registrations never overbook it. Money: an optional entry fee per player from the real balance only,
/// held in the ledger (event_fee_hold) until the event is over (event_fee_kept, revenue like PC time from the
/// balance) or returned (event_fee_release) when the team withdraws before the start or the event is cancelled.
/// The PC time of a player is a normal prepaid session (free with the event as the reason, or a fixed price from
/// the balances, bonus first) that ends at the common end of the event. Audit texts have ids, PCs, times and
/// amounts, never the name of an event or a team.
/// </summary>
public sealed class EventService
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;
    private readonly StaffUserEntity? _auditActor;

    public EventService(CmsDbContext db, IDeviceRepository devices, ClubClock? clock = null, AuditLogger? audit = null,
        StaffActor? actor = null, StaffUserEntity? auditActor = null)
    {
        _db = db;
        _devices = devices;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor ?? (auditActor == null ? null : new StaffActor(auditActor.Id, auditActor.Username));
        _auditActor = auditActor;
    }

    private Task AuditAsync(string action, long id, string summary, object details) =>
        _audit?.WriteAsync(action, "event", id, summary, details, _auditActor) ?? Task.CompletedTask;

    private string Local(DateTimeOffset utc, string format = "HH:mm") => _clock.ToLocal(utc).ToString(format, CultureInfo.InvariantCulture);

    private string Span(EventEntity e) => $"{Local(e.StartUtc, "dd.MM HH:mm")}-{Local(e.EndUtc)}";

    public static bool ValidDuration(int minutes) =>
        minutes is >= EventLimits.MinMinutes and <= EventLimits.MaxMinutes && minutes % EventLimits.StepMinutes == 0;

    /// <summary>One line without control characters and double spaces; null when empty or too long.</summary>
    public static string? CleanName(string? name, int max)
    {
        var visible = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray());
        var n = string.Join(' ', visible.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return n.Length == 0 || n.Length > max ? null : n;
    }

    private (DateTimeOffset Utc, int Day)? ParseStart(string? date, string? time)
    {
        if (!DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            !TimeOnly.TryParseExact(time?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        var utc = ClubTimeZone.LocalToUtc(d.ToDateTime(t), _clock.Zone);
        return (utc, ReservationService.DayOf(utc, _clock));
    }

    private BillingError? CheckTime((DateTimeOffset Utc, int Day)? start)
    {
        var now = _clock.UtcNow;
        return start == null || start.Value.Utc < now - ReservationService.PastTolerance || start.Value.Utc > now.AddDays(EventLimits.MaxDaysAhead)
            ? EventErrors.BadTime : null;
    }

    private ReservationService Bookings => new(_db, _devices, _clock, _audit, _actor, _auditActor);

    private async Task<List<DeviceEntity>> PcsAsync(long eventId) =>
        (await _db.Devices.AsNoTracking().Where(d => _db.EventDevices.Any(x => x.EventId == eventId && x.DeviceId == d.Id)).ToListAsync())
        .OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).ToList();

    // ===== Create, move =====

    public async Task<BillingResult<EventEntity>> CreateAsync(CreateEventRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        var name = CleanName(req.Name, EventLimits.NameLength);
        if (name == null) return EventErrors.BadName;
        if (!ValidDuration(req.DurationMinutes)) return EventErrors.BadDuration;
        var start = ParseStart(req.Date, req.Time);
        if (CheckTime(start) is { } timeError) return timeError;
        var mode = (req.PriceMode ?? EventPriceModes.Free).Trim().ToLowerInvariant();
        if (mode is not (EventPriceModes.Free or EventPriceModes.Fixed)) return EventErrors.BadPrice;
        var price = mode == EventPriceModes.Fixed ? TariffCalculator.Round(req.Price) : 0m;
        if (mode == EventPriceModes.Fixed && (price <= 0 || price > EventLimits.MaxPrice)) return EventErrors.BadPrice;
        if (req.EntryFee < 0 || req.EntryFee > EventLimits.MaxFee) return EventErrors.BadFee;
        var fee = TariffCalculator.Round(req.EntryFee);
        if (req.TeamSize is < 1 or > EventLimits.MaxTeamSize) return EventErrors.BadTeamSize;

        List<DeviceEntity> pcs;
        if (req.DeviceIds is { Count: > 0 } ids)
        {
            var wanted = ids.Distinct().ToList();
            pcs = await _db.Devices.AsNoTracking().Where(d => wanted.Contains(d.Id) && d.Status == DeviceStatuses.Approved).ToListAsync();
            if (pcs.Count != wanted.Count) return EventErrors.NeedsPcs;
        }
        else if (req.ZoneId.HasValue)
        {
            pcs = await _db.Devices.AsNoTracking().Where(d => d.ZoneId == req.ZoneId && d.Status == DeviceStatuses.Approved).ToListAsync();
        }
        else return EventErrors.NeedsPcs;
        if (pcs.Count < req.TeamSize || pcs.Count == 0 || pcs.Count > EventLimits.MaxPcs) return EventErrors.NeedsPcs;
        pcs = pcs.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
        var zoneIds = pcs.Select(p => p.ZoneId).Distinct().ToList();
        var zoneId = req.ZoneId ?? (zoneIds.Count == 1 ? zoneIds[0] : null);
        var zoneName = zoneId.HasValue ? await _db.Zones.AsNoTracking().Where(z => z.Id == zoneId).Select(z => z.Name).FirstOrDefaultAsync() : null;
        var now = _clock.UtcNow;
        var end = start!.Value.Utc.AddMinutes(req.DurationMinutes);

        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReservationService.BookingLockAsync(_db);
        var fit = await Bookings.CheckEventFitAsync(0, pcs.Select(p => p.Id).ToList(), start.Value.Utc, end);
        if (fit != null) return fit;
        var e = new EventEntity
        {
            Name = name, Status = EventStatuses.Scheduled, ZoneId = zoneId, StartUtc = start.Value.Utc, EndUtc = end, DurationMinutes = req.DurationMinutes,
            StartDay = start.Value.Day, PriceMode = mode, Price = price, EntryFee = fee, TeamSize = req.TeamSize,
            CreatedUtc = now, UpdatedUtc = now, CreatedById = _actor?.Id, CreatedByUsername = _actor?.Username
        };
        _db.Events.Add(e);
        await _db.SaveChangesAsync();
        _db.EventDevices.AddRange(pcs.Select(p => new EventDeviceEntity { EventId = e.Id, DeviceId = p.Id }));
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.EventCreated, e.Id,
            $"Event #{e.Id}: {pcs.Count} PC(s) ({(zoneName != null ? "zone " + zoneName : "chosen PCs")}), {Span(e)}, " +
            $"PC time {(mode == EventPriceModes.Free ? "free" : $"{price} {cur} per player")}, entry fee {(fee > 0 ? $"{fee} {cur}" : "none")}, " +
            (req.TeamSize == 1 ? "single players" : $"teams of {req.TeamSize}"),
            new { id = e.Id, pcs = pcs.Select(p => p.Hostname), zone = zoneName, startUtc = e.StartUtc, minutes = e.DurationMinutes, priceMode = mode, price, entryFee = fee, teamSize = e.TeamSize });
        await tx.CommitAsync();
        return BillingResult<EventEntity>.Success(e);
    }

    /// <summary>A scheduled event gets another name, start or duration (its PCs must be free for the new time).</summary>
    public async Task<BillingResult<EventEntity>> UpdateAsync(long id, UpdateEventRequest req)
    {
        var name = CleanName(req.Name, EventLimits.NameLength);
        if (name == null) return EventErrors.BadName;
        if (!ValidDuration(req.DurationMinutes)) return EventErrors.BadDuration;
        var start = ParseStart(req.Date, req.Time);
        if (CheckTime(start) is { } timeError) return timeError;
        var end = start!.Value.Utc.AddMinutes(req.DurationMinutes);

        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReservationService.BookingLockAsync(_db);
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status != EventStatuses.Scheduled) return EventErrors.NotOpen.With(e.Status);
        var fit = await Bookings.CheckEventFitAsync(e.Id, (await PcsAsync(e.Id)).Select(p => p.Id).ToList(), start.Value.Utc, end);
        if (fit != null) return fit;
        var before = Span(e);
        var renamed = e.Name != name;
        e.Name = name;
        e.StartUtc = start.Value.Utc;
        e.EndUtc = end;
        e.DurationMinutes = req.DurationMinutes;
        e.StartDay = start.Value.Day;
        e.UpdatedUtc = _clock.UtcNow;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return EventErrors.Changed;
        }
        await AuditAsync(AuditActions.EventUpdated, e.Id,
            $"Event #{e.Id} changed: {before} -> {Span(e)}" + (renamed ? ", new name" : ""),
            new { id = e.Id, startUtc = e.StartUtc, minutes = e.DurationMinutes, renamed });
        await tx.CommitAsync();
        return BillingResult<EventEntity>.Success(e);
    }

    // ===== Teams =====

    /// <summary>
    /// Registers a team (or one player) before the start: exactly TeamSize registered players, each on a free PC of the
    /// event (neighbours by PC name), each paying the entry fee from the real balance (all or nothing).
    /// </summary>
    public async Task<BillingResult<EventTeamEntity>> RegisterAsync(long id, RegisterTeamRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReservationService.BookingLockAsync(_db);
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status != EventStatuses.Scheduled) return EventErrors.NotOpen.With(e.Status);
        var ids = (req.PlayerIds ?? Array.Empty<Guid>()).ToList();
        if (ids.Count != e.TeamSize || ids.Distinct().Count() != ids.Count) return EventErrors.BadTeam.With(e.TeamSize);
        var found = await _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync();
        if (found.Count != ids.Count) return BillingErrors.UserNotFound;
        var users = ids.Select(i => found.Single(u => u.Id == i)).ToList();
        var captain = req.CaptainId ?? ids[0];
        if (!ids.Contains(captain)) return EventErrors.BadTeam.With(e.TeamSize);
        var name = CleanName(req.Name, EventLimits.TeamNameLength) ?? (e.TeamSize == 1 ? users[0].Username : null);
        if (name == null) return EventErrors.BadTeam.With(e.TeamSize);
        if (name.Length > EventLimits.TeamNameLength) name = name[..EventLimits.TeamNameLength];

        var active = await _db.EventPlayers.AsNoTracking().Where(p => p.EventId == id && p.Status == EventMemberStatuses.Active).ToListAsync();
        if (users.FirstOrDefault(u => active.Any(p => p.UserId == u.Id)) is { } twice) return EventErrors.PlayerRegistered.With(twice.Username);
        var taken = active.Where(p => p.DeviceId.HasValue).Select(p => p.DeviceId!.Value).ToHashSet();
        var seats = (await PcsAsync(id)).Where(p => !taken.Contains(p.Id)).Take(e.TeamSize).ToList();
        if (seats.Count < e.TeamSize) return EventErrors.Full.With(e.TeamSize);

        var resetBracket = await ResetBracketAsync(id);
        if (!resetBracket.Ok) return resetBracket.Error!;
        var seed = (await _db.EventTeams.AsNoTracking().Where(t => t.EventId == id).Select(t => (int?)t.Seed).MaxAsync() ?? 0) + 1;
        var team = new EventTeamEntity
        {
            EventId = id, Name = name, CaptainUserId = captain, Seed = seed, CreatedUtc = now, CreatedByUsername = _actor?.Username
        };
        _db.EventTeams.Add(team);
        await _db.SaveChangesAsync();
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            if (e.EntryFee > 0)
            {
                // From the real balance only (bonus is not money that can be held or returned)
                if (await Wallet.TryChargeAsync(_db, user.Id, e.EntryFee, SpendPurpose.Bar) == null)
                {
                    var have = await Wallet.GetAsync(_db, user.Id);
                    return EventErrors.InsufficientFee.With(user.Username, e.EntryFee, have?.Balance ?? 0m);
                }
                await Ledger.AppendAsync(_db, new LedgerEntryEntity
                {
                    UserId = user.Id, Type = LedgerTypes.EventFeeHold, Method = PaymentMethods.Balance, Amount = e.EntryFee, BalanceDelta = -e.EntryFee,
                    EventId = id, DeviceId = seats[i].Id,
                    Description = $"Внесок за участь: подія №{id} ({Local(e.StartUtc, "dd.MM HH:mm")}) — {e.EntryFee} {cur}"
                }, _actor, now);
            }
            _db.EventPlayers.Add(new EventPlayerEntity
            {
                EventId = id, TeamId = team.Id, UserId = user.Id, DeviceId = seats[i].Id, Fee = e.EntryFee,
                FeeStatus = e.EntryFee > 0 ? EventFeeStatuses.Held : EventFeeStatuses.None, CreatedUtc = now
            });
        }
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return EventErrors.Changed; // the same player registered at the same moment (unique index)
        }
        await AuditAsync(AuditActions.EventTeamRegistered, id,
            $"Event #{id}: team #{team.Id} registered: {string.Join(", ", users.Select(u => "@" + u.Username))} on {string.Join(", ", seats.Select(s => s.Hostname))}" +
            (e.EntryFee > 0 ? $", entry fee {e.EntryFee} {cur} x {users.Count} held" : "") + (resetBracket.Value ? ", bracket cleared" : ""),
            new { eventId = id, team = team.Id, players = users.Select(u => u.Username), pcs = seats.Select(s => s.Hostname), fee = e.EntryFee, bracketCleared = resetBracket.Value });
        await tx.CommitAsync();
        return BillingResult<EventTeamEntity>.Success(team);
    }

    /// <summary>A team leaves before the start: its PCs are free again and the held entry fees go back to the balances.</summary>
    public async Task<BillingResult<EventTeamEntity>> WithdrawAsync(long id, long teamId)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReservationService.BookingLockAsync(_db);
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status != EventStatuses.Scheduled) return EventErrors.NotOpen.With(e.Status);
        var team = await _db.EventTeams.FirstOrDefaultAsync(t => t.Id == teamId && t.EventId == id && t.Status == EventMemberStatuses.Active);
        if (team == null) return EventErrors.TeamNotFound;
        var reset = await ResetBracketAsync(id);
        if (!reset.Ok) return reset.Error!;
        var players = await _db.EventPlayers.Where(p => p.TeamId == teamId && p.Status == EventMemberStatuses.Active).ToListAsync();
        var returned = await ReleaseFeesAsync(e, players, $"повернено: команда знялася з події №{id}", now);
        foreach (var p in players)
        {
            p.Status = EventMemberStatuses.Withdrawn;
            p.DeviceId = null;
        }
        team.Status = EventMemberStatuses.Withdrawn;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.EventTeamWithdrawn, id,
            $"Event #{id}: team #{team.Id} withdrawn" + (returned > 0 ? $", entry fees {returned} {cur} returned to the balances" : "") + (reset.Value ? ", bracket cleared" : ""),
            new { eventId = id, team = team.Id, returned, bracketCleared = reset.Value });
        await tx.CommitAsync();
        return BillingResult<EventTeamEntity>.Success(team);
    }

    private async Task<decimal> ReleaseFeesAsync(EventEntity e, IEnumerable<EventPlayerEntity> players, string why, DateTimeOffset now)
    {
        var cur = await _db.CurrencySymbolAsync();
        decimal total = 0;
        foreach (var p in players.Where(p => p.FeeStatus == EventFeeStatuses.Held && p.Fee > 0))
        {
            await Wallet.CreditAsync(_db, p.UserId, p.Fee);
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = p.UserId, Type = LedgerTypes.EventFeeRelease, Method = PaymentMethods.Balance, Amount = p.Fee, BalanceDelta = p.Fee,
                EventId = e.Id, DeviceId = p.DeviceId, Description = $"Внесок за участь у події №{e.Id} {why} — {p.Fee} {cur}"
            }, _actor, now);
            p.FeeStatus = EventFeeStatuses.Released;
            total += p.Fee;
        }
        return total;
    }

    /// <summary>Deletes a bracket that has no results yet (teams changed); true when one was deleted.</summary>
    private async Task<BillingResult<bool>> ResetBracketAsync(long id)
    {
        var matches = await _db.EventMatches.Where(m => m.EventId == id).ToListAsync();
        if (matches.Count == 0) return BillingResult<bool>.Success(false);
        if (Bracket.HasResults(matches.Select(ToBracket))) return EventErrors.BracketHasResults;
        _db.EventMatches.RemoveRange(matches);
        await _db.SaveChangesAsync();
        return BillingResult<bool>.Success(true);
    }

    // ===== Start, finish, cancel =====

    /// <summary>
    /// Starts the event (staff early, or the job at the start time): the PC of every registered player gets a session
    /// that ends at the common end of the event (free, or the fixed price from the player's balances). A player whose
    /// session cannot start (balance too low, PC busy, the player plays elsewhere) keeps the error; staff retry it.
    /// </summary>
    public async Task<BillingResult<EventStartResult>> StartAsync(long id)
    {
        var now = _clock.UtcNow;
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status != EventStatuses.Scheduled) return EventErrors.NotOpen.With(e.Status);
        if (e.EndUtc <= now) return EventErrors.EventOver;
        e.Status = EventStatuses.Running;
        e.StartedUtc = now;
        e.UpdatedUtc = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return EventErrors.Changed; // started or cancelled at the same moment
        }
        var (started, failed) = await StartSeatsAsync(e);
        await AuditAsync(AuditActions.EventStarted, e.Id,
            $"Event #{e.Id} started: {started.Count} PC(s) unlocked until {Local(e.EndUtc)}" + (failed.Count > 0 ? $", {failed.Count} player(s) without a session" : ""),
            new { id = e.Id, endUtc = e.EndUtc, started = started.Select(s => s.Host), failed = failed.Select(f => new { player = f.Player, f.Code }), early = now < e.StartUtc });
        return BillingResult<EventStartResult>.Success(new EventStartResult(started.Count, failed.Count));
    }

    private sealed record SeatResult(string Host, string Player, string? Code);

    /// <summary>Starts the sessions of the players that have none (and no error) yet.</summary>
    private async Task<(List<SeatResult> Started, List<SeatResult> Failed)> StartSeatsAsync(EventEntity e)
    {
        var players = await _db.EventPlayers.AsNoTracking()
            .Where(p => p.EventId == e.Id && p.Status == EventMemberStatuses.Active && p.SessionId == null && p.StartError == null).ToListAsync();
        var started = new List<SeatResult>();
        var failed = new List<SeatResult>();
        foreach (var p in players.OrderBy(p => p.Id))
        {
            var r = await StartSeatAsync(e, p.Id);
            (r.Code == null ? started : failed).Add(r);
        }
        return (started, failed);
    }

    /// <summary>The session of one player: in one transaction with the player row (the session id or the error code).</summary>
    private async Task<SeatResult> StartSeatAsync(EventEntity e, long playerId)
    {
        _db.ChangeTracker.Clear();
        var p = await _db.EventPlayers.FirstAsync(x => x.Id == playerId);
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == p.UserId).Select(u => u.Username).FirstOrDefaultAsync() ?? "?";
        var host = p.DeviceId.HasValue ? await _db.Devices.AsNoTracking().Where(d => d.Id == p.DeviceId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?" : "—";
        string? code;
        if (p.DeviceId == null) code = "event_no_seat";
        else
        {
            // A player plays on one PC at a time
            var elsewhere = await _db.Sessions.AsNoTracking().AnyAsync(s => s.UserId == p.UserId && s.Status == "active" && s.DeviceId != p.DeviceId);
            code = elsewhere ? BillingErrors.PlayerBusy.Code : null;
        }
        if (code == null)
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            var sessions = new SessionService(_db, _devices, _clock, _audit, _actor)
            {
                Event = new EventTerms(e.Id, e.EndUtc, e.PriceMode == EventPriceModes.Free, e.Price), CanGiveFreeTime = true
            };
            var result = await sessions.StartAsync(new StartSessionRequest(p.DeviceId!.Value, null, p.UserId, null, null,
                e.PriceMode == EventPriceModes.Free ? PaymentMethods.Free : PaymentMethods.Balance, UseBonus: true, Reason: $"event #{e.Id}"));
            if (result.Ok)
            {
                p.SessionId = result.Value!.Id;
                p.StartError = null;
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                return new SeatResult(host, user, null);
            }
            code = result.Error!.Code;
            await tx.RollbackAsync();
        }
        _db.ChangeTracker.Clear();
        var row = await _db.EventPlayers.FirstAsync(x => x.Id == playerId);
        row.StartError = code;
        await _db.SaveChangesAsync();
        return new SeatResult(host, user, code);
    }

    /// <summary>Staff start the session of one player of a running event again (after a top-up, a PC that became free).</summary>
    public async Task<BillingResult<EventPlayerEntity>> StartPlayerAsync(long id, long playerId)
    {
        var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status != EventStatuses.Running) return EventErrors.NotOpen.With(e.Status);
        if (e.EndUtc <= _clock.UtcNow) return EventErrors.EventOver;
        var p = await _db.EventPlayers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == playerId && x.EventId == id && x.Status == EventMemberStatuses.Active);
        if (p == null) return EventErrors.PlayerNotFound;
        if (p.SessionId is { } sid && await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sid && s.Status == "active")) return EventErrors.SeatRunning;
        // A player whose session ended early (logout) starts again with the rest of the event time
        _db.ChangeTracker.Clear();
        var row = await _db.EventPlayers.FirstAsync(x => x.Id == playerId);
        row.SessionId = null;
        row.StartError = null;
        await _db.SaveChangesAsync();
        var result = await StartSeatAsync(e, playerId);
        _db.ChangeTracker.Clear();
        var after = await _db.EventPlayers.AsNoTracking().FirstAsync(x => x.Id == playerId);
        if (result.Code != null) return EventErrors.SeatFailed.With(result.Player, result.Code);
        await AuditAsync(AuditActions.EventSeatStarted, id, $"Event #{id}: the session of @{result.Player} on {result.Host} started until {Local(e.EndUtc)}",
            new { eventId = id, player = result.Player, device = result.Host, sessionId = after.SessionId });
        return BillingResult<EventPlayerEntity>.Success(after);
    }

    /// <summary>
    /// The event is over (at its end by the job, or early by staff): the held entry fees are kept by the club. Early: the
    /// running sessions of the event end now (prepaid time is not refunded) and their PCs lock.
    /// </summary>
    public async Task<BillingResult<EventEntity>> FinishAsync(long id, bool early)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
            if (e == null) return EventErrors.NotFoundError;
            if (e.Status != EventStatuses.Running) return EventErrors.NotOpen.With(e.Status);
            if (!early && now < e.EndUtc) return EventErrors.NotOpen.With(e.Status);
            var players = await _db.EventPlayers.Where(p => p.EventId == id && p.Status == EventMemberStatuses.Active).ToListAsync();
            decimal kept = 0;
            foreach (var p in players.Where(p => p.FeeStatus == EventFeeStatuses.Held && p.Fee > 0))
            {
                await Ledger.AppendAsync(_db, new LedgerEntryEntity
                {
                    UserId = p.UserId, Type = LedgerTypes.EventFeeKept, Method = PaymentMethods.Balance, Amount = p.Fee,
                    EventId = id, DeviceId = p.DeviceId, Description = $"Внесок за участь у події №{id} — подія відбулася, {p.Fee} {cur}"
                }, _actor, now);
                p.FeeStatus = EventFeeStatuses.Kept;
                kept += p.Fee;
            }
            e.Status = EventStatuses.Finished;
            e.EndedUtc = early || now < e.EndUtc ? now : e.EndUtc;
            e.UpdatedUtc = now;
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return EventErrors.Changed;
            }
            await AuditAsync(AuditActions.EventFinished, id,
                $"Event #{id} finished{(early ? " early by staff" : " at its end")}" + (kept > 0 ? $", entry fees {kept} {cur} kept" : ""),
                new { id, early, kept, winnerTeam = e.WinnerTeamId });
            await tx.CommitAsync();
        }
        if (early) await EndSessionsAsync(id, "event finished");
        _db.ChangeTracker.Clear();
        return BillingResult<EventEntity>.Success(await _db.Events.AsNoTracking().FirstAsync(x => x.Id == id));
    }

    /// <summary>Cancels a scheduled or running event: the held entry fees go back to the balances; running sessions end now.</summary>
    public async Task<BillingResult<EventEntity>> CancelAsync(long id, string? reason)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) why = null;
        else if (why.Length > 200) why = why[..200];
        bool wasRunning;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await ReservationService.BookingLockAsync(_db);
            var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
            if (e == null) return EventErrors.NotFoundError;
            if (e.Status is not (EventStatuses.Scheduled or EventStatuses.Running)) return EventErrors.NotOpen.With(e.Status);
            wasRunning = e.Status == EventStatuses.Running;
            var players = await _db.EventPlayers.Where(p => p.EventId == id && p.Status == EventMemberStatuses.Active).ToListAsync();
            var returned = await ReleaseFeesAsync(e, players, "повернено: подію скасовано", now);
            e.Status = EventStatuses.Cancelled;
            e.EndedUtc = now;
            e.CancelReason = why;
            e.UpdatedUtc = now;
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return EventErrors.Changed;
            }
            await AuditAsync(AuditActions.EventCancelled, id,
                $"Event #{id} cancelled" + (wasRunning ? " while running" : "") + (returned > 0 ? $", entry fees {returned} {cur} returned to the balances" : "") +
                (why != null ? $": {why}" : ""),
                new { id, returned, running = wasRunning, reason = why });
            await tx.CommitAsync();
        }
        if (wasRunning) await EndSessionsAsync(id, "event cancelled");
        _db.ChangeTracker.Clear();
        return BillingResult<EventEntity>.Success(await _db.Events.AsNoTracking().FirstAsync(x => x.Id == id));
    }

    /// <summary>Ends the running sessions of the players of an event now (each in its own transaction).</summary>
    private async Task EndSessionsAsync(long id, string why)
    {
        _db.ChangeTracker.Clear();
        var ids = await _db.EventPlayers.AsNoTracking().Where(p => p.EventId == id && p.SessionId != null).Select(p => p.SessionId!.Value).ToListAsync();
        var running = await _db.Sessions.AsNoTracking().Where(s => ids.Contains(s.Id) && s.Status == "active").Select(s => s.Id).ToListAsync();
        foreach (var sid in running)
        {
            _db.ChangeTracker.Clear();
            await new SessionService(_db, _devices, _clock, _audit, _actor).EndAsync(sid, null, why);
        }
    }

    // ===== Bracket =====

    public static BracketMatch ToBracket(EventMatchEntity m) => new(m.Round, m.Slot, m.TeamAId, m.TeamBId, m.WinnerTeamId, m.ScoreA, m.ScoreB);

    /// <summary>A new single-elimination bracket of the active teams (by registration order, or shuffled), while no result is entered.</summary>
    public async Task<BillingResult<List<EventMatchEntity>>> CreateBracketAsync(long id, bool shuffle)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReservationService.BookingLockAsync(_db);
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status is not (EventStatuses.Scheduled or EventStatuses.Running)) return EventErrors.NotOpen.With(e.Status);
        var teams = await _db.EventTeams.AsNoTracking().Where(t => t.EventId == id && t.Status == EventMemberStatuses.Active).OrderBy(t => t.Seed).Select(t => t.Id).ToListAsync();
        if (teams.Count < 2) return EventErrors.BracketNeedsTeams;
        var reset = await ResetBracketAsync(id);
        if (!reset.Ok) return reset.Error!;
        if (shuffle)
            teams = teams.Select(t => (t, k: System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue))).OrderBy(x => x.k).Select(x => x.t).ToList();
        var matches = Bracket.Create(teams).Select(m => new EventMatchEntity
        {
            EventId = id, Round = m.Round, Slot = m.Slot, TeamAId = m.TeamA, TeamBId = m.TeamB, WinnerTeamId = m.Winner,
            DecidedUtc = m.Winner != null ? now : null
        }).ToList();
        _db.EventMatches.AddRange(matches);
        e.WinnerTeamId = null;
        e.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.EventBracketCreated, id,
            $"Event #{id}: bracket of {teams.Count} teams, {Bracket.Rounds(teams.Count)} round(s){(shuffle ? ", shuffled" : ", in registration order")}",
            new { eventId = id, teams, shuffle, rounds = Bracket.Rounds(teams.Count) });
        await tx.CommitAsync();
        return BillingResult<List<EventMatchEntity>>.Success(matches);
    }

    /// <summary>Staff enter (or clear) the winner of a match of a running or finished event; the winner moves on.</summary>
    public async Task<BillingResult<EventMatchEntity>> SetResultAsync(long id, long matchId, MatchResultRequest req)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReservationService.BookingLockAsync(_db);
        var e = await _db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return EventErrors.NotFoundError;
        if (e.Status is not (EventStatuses.Running or EventStatuses.Finished)) return EventErrors.NotOpen.With(e.Status);
        var rows = await _db.EventMatches.Where(m => m.EventId == id).ToListAsync();
        var match = rows.FirstOrDefault(m => m.Id == matchId);
        if (match == null) return EventErrors.MatchNotFound;
        var bracket = rows.Select(ToBracket).ToList();
        var error = Bracket.SetResult(bracket, match.Round, match.Slot, req.WinnerTeamId, req.ScoreA, req.ScoreB);
        if (error != null) return EventErrors.MatchRule.With(error) with { Code = error };
        foreach (var row in rows)
        {
            var m = bracket.Single(b => b.Round == row.Round && b.Slot == row.Slot);
            if (row.TeamAId == m.TeamA && row.TeamBId == m.TeamB && row.WinnerTeamId == m.Winner && row.ScoreA == m.ScoreA && row.ScoreB == m.ScoreB) continue;
            row.TeamAId = m.TeamA;
            row.TeamBId = m.TeamB;
            if (row.Id == matchId)
            {
                row.DecidedUtc = m.Winner != null ? now : null;
                row.DecidedByUsername = m.Winner != null ? _actor?.Username : null;
            }
            row.WinnerTeamId = m.Winner;
            row.ScoreA = m.ScoreA;
            row.ScoreB = m.ScoreB;
        }
        e.WinnerTeamId = Bracket.Champion(bracket);
        e.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        var score = req.WinnerTeamId != null && req.ScoreA.HasValue && req.ScoreB.HasValue ? $" ({req.ScoreA}:{req.ScoreB})" : "";
        await AuditAsync(AuditActions.EventResult, id,
            req.WinnerTeamId != null
                ? $"Event #{id}: match {match.Round}.{match.Slot + 1}: team #{req.WinnerTeamId} won{score}" + (e.WinnerTeamId != null ? $"; team #{e.WinnerTeamId} won the event" : "")
                : $"Event #{id}: result of match {match.Round}.{match.Slot + 1} cleared",
            new { eventId = id, match = matchId, round = match.Round, slot = match.Slot, winner = req.WinnerTeamId, req.ScoreA, req.ScoreB, champion = e.WinnerTeamId });
        await tx.CommitAsync();
        return BillingResult<EventMatchEntity>.Success(match);
    }

    // ===== Background jobs =====

    /// <summary>
    /// Every 15 s: scheduled events start at their start time, running events finish at their end (fees kept), a
    /// scheduled event whose end passed without a start is cancelled (fees returned), and the players of a running
    /// event that have no session and no error yet (registered when the server was down) get it.
    /// </summary>
    public async Task<EventJobResult> RunJobsAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var today = ReservationService.DayOf(now, _clock);
        var open = (await _db.Events.AsNoTracking()
                .Where(e => (e.Status == EventStatuses.Scheduled || e.Status == EventStatuses.Running) && e.StartDay <= today + 1)
                .ToListAsync(ct))
            .OrderBy(e => e.StartUtc).ToList();
        int started = 0, finished = 0, cancelled = 0, seats = 0;
        foreach (var e in open)
        {
            _db.ChangeTracker.Clear();
            if (e.Status == EventStatuses.Scheduled && now >= e.EndUtc)
                cancelled += (await CancelAsync(e.Id, "not started before its end")).Ok ? 1 : 0;
            else if (e.Status == EventStatuses.Scheduled && now >= e.StartUtc)
                started += (await StartAsync(e.Id)).Ok ? 1 : 0;
            else if (e.Status == EventStatuses.Running && now >= e.EndUtc)
                finished += (await FinishAsync(e.Id, early: false)).Ok ? 1 : 0;
            else if (e.Status == EventStatuses.Running)
                seats += (await StartSeatsAsync(e)).Started.Count;
        }
        return new EventJobResult(started, finished, cancelled, seats);
    }
}
