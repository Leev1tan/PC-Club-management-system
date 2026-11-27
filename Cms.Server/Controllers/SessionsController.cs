using Cms.Server.Auth;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.FrontDesk)]
public class SessionsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly AuditLogger? _audit;

    public SessionsController(CmsDbContext db, IDeviceRepository devices, AuditLogger? audit = null)
    {
        _db = db;
        _devices = devices;
        _audit = audit;
    }

    private Task AuditAsync(string action, Guid sessionId, string summary, object details) =>
        _audit?.WriteAsync(action, "session", sessionId, summary, details) ?? Task.CompletedTask;

    /// <summary>
    /// Start a new session. Supports two modes:
    /// 1. Prepaid: specify durationMinutes — cost calculated upfront and taken from the player balance
    /// 2. Open-ended: no durationMinutes — runs until manually ended, cost calculated at end
    /// The balance debit, the ledger row and the session are one transaction. The database allows only
    /// one active session per PC (unique index), so two cashiers cannot start two sessions on one PC.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SessionDetailView>> Start([FromBody] StartSessionRequest req)
    {
        var device = await _db.Devices.FindAsync(req.DeviceId);
        if (device == null) return NotFound("Device not found");
        if (device.Status != Cms.Server.Auth.DeviceStatuses.Approved) return Conflict("The PC is not approved.");
        if (req.DurationMinutes is < 0 or > 24 * 60) return BadRequest("Duration must be between 1 and 1440 minutes");

        // Resolve tariff: explicit > zone default > system default
        TariffEntity? tariff = null;
        if (req.TariffId.HasValue)
            tariff = await _db.Tariffs.FindAsync(req.TariffId.Value);
        if (tariff == null && device.ZoneId.HasValue)
            tariff = await _db.Tariffs.FirstOrDefaultAsync(t => t.ZoneId == device.ZoneId);
        if (tariff == null)
            tariff = await _db.Tariffs.FirstOrDefaultAsync(t => t.IsDefault);
        if (tariff == null)
            return BadRequest("No tariff configured. Create a tariff first.");

        UserEntity? user = null;
        if (req.UserId.HasValue)
        {
            user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId.Value);
            if (user == null) return NotFound("User not found");
        }

        var now = DateTimeOffset.UtcNow;
        bool isPrepaid = req.DurationMinutes.HasValue && req.DurationMinutes > 0;
        var endUtc = isPrepaid ? now.AddMinutes(req.DurationMinutes!.Value) : (DateTimeOffset?)null;
        decimal totalCost = isPrepaid ? Math.Round(tariff.PricePerHour * req.DurationMinutes!.Value / 60m, 2) : 0;

        var session = new SessionEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = req.DeviceId,
            TariffId = tariff.Id,
            UserId = user?.Id,
            StartUtc = now,
            EndUtc = endUtc,
            Status = "active",
            TotalCost = totalCost,
            IsPrepaid = isPrepaid
        };

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            // A prepaid session that has run out but is not closed yet does not block the PC
            var activeForDevice = await _db.Sessions
                .Where(s => s.DeviceId == req.DeviceId && s.Status == "active")
                .ToListAsync();
            var expired = activeForDevice.Where(s => s.IsPrepaid && s.EndUtc.HasValue && s.EndUtc.Value <= now).ToList();
            if (activeForDevice.Count > expired.Count)
                return Conflict("Device already has an active session.");
            foreach (var old in expired) old.Status = "ended";

            if (user != null && isPrepaid)
            {
                if (!await Balances.TryDebitAsync(_db, user.Id, totalCost))
                    return BadRequest($"Insufficient balance. Need {totalCost} ₴, have {await Balances.GetAsync(_db, user.Id)} ₴");
                _db.Transactions.Add(new TransactionEntity
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Amount = -totalCost,
                    Type = "session_charge",
                    Description = $"Сесія на {device.Hostname} ({req.DurationMinutes} хв) — {totalCost} ₴",
                    CreatedUtc = now
                });
            }

            try
            {
                if (expired.Count > 0) await _db.SaveChangesAsync(); // free the unique index first
                _db.Sessions.Add(session);
                await _db.SaveChangesAsync();
                var paidFromBalance = user != null && isPrepaid;
                await AuditAsync(AuditActions.SessionStarted, session.Id,
                    $"Session on {device.Hostname} for {(user != null ? "@" + user.Username : "a guest")}: " +
                    (isPrepaid ? $"{req.DurationMinutes} min, {totalCost} ₴" : "open-ended") +
                    (paidFromBalance ? " from the balance" : isPrepaid ? " not paid from a balance (cash at the desk)" : ""),
                    new { device.Hostname, deviceId = device.Id, tariff = tariff.Name, minutes = req.DurationMinutes, cost = totalCost,
                          prepaid = isPrepaid, player = user?.Username, paidFromBalance });
                await tx.CommitAsync();
            }
            catch (DbUpdateException)
            {
                // Another session was started on this PC at the same moment; the debit is rolled back
                return Conflict("Device already has an active session.");
            }
        }

        // Send commands to agent
        if (endUtc.HasValue)
            _devices.EnqueueCommand(req.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc }));
        _devices.EnqueueCommand(req.DeviceId, new EnqueueCommandRequest("unlock", null));

        return Ok(ToDetailView(session, tariff.Name, tariff.PricePerHour, user?.Username, device.Hostname));
    }

    /// <summary>
    /// End a session. Calculates the final cost of open-ended sessions and takes it from the player
    /// balance, but never more than the balance: the rest is due at the desk (AmountDue).
    /// Ending the same session twice at the same moment charges once (concurrency token).
    /// </summary>
    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<SessionDetailView>> End(Guid id)
    {
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return NotFound();
        if (session.Status == "ended") return BadRequest("Session already ended");

        var now = DateTimeOffset.UtcNow;
        decimal amountDue = 0;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            session.EndUtc = now;
            session.Status = "ended";

            // Calculate cost for open-ended sessions
            TariffEntity? tariff = null;
            var minutes = 0m;
            if (!session.IsPrepaid && session.TariffId.HasValue)
            {
                tariff = await _db.Tariffs.FindAsync(session.TariffId.Value);
                if (tariff != null)
                {
                    minutes = (decimal)(now - session.StartUtc).TotalMinutes;
                    session.TotalCost = Math.Round(tariff.PricePerHour * minutes / 60m, 2);
                }
            }

            try
            {
                await _db.SaveChangesAsync(); // fails if someone else ended or changed the session
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("The session was changed at the same time. Reload and try again.");
            }

            if (tariff != null && session.UserId.HasValue && session.TotalCost > 0)
            {
                var charged = await Balances.DebitUpToAsync(_db, session.UserId.Value, session.TotalCost);
                amountDue = session.TotalCost - charged;
                var device = await _db.Devices.FindAsync(session.DeviceId);
                if (charged > 0)
                {
                    _db.Transactions.Add(new TransactionEntity
                    {
                        Id = Guid.NewGuid(),
                        UserId = session.UserId.Value,
                        Amount = -charged,
                        Type = "session_charge",
                        Description = $"Сесія на {device?.Hostname ?? "?"} ({(int)minutes} хв) — {charged} ₴" +
                                      (amountDue > 0 ? $" (до сплати на касі: {amountDue} ₴)" : ""),
                        CreatedUtc = now
                    });
                    await _db.SaveChangesAsync();
                }
            }
            await AuditAsync(AuditActions.SessionEnded, session.Id,
                $"Session ended: {session.TotalCost} ₴" + (amountDue > 0 ? $", {amountDue} ₴ due at the desk" : ""),
                new { session.DeviceId, cost = session.TotalCost, amountDue, prepaid = session.IsPrepaid, player = session.UserId });
            await tx.CommitAsync();
        }

        // Lock the device + inform agent
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc = now }));
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("lock", null));

        return Ok(ToDetailView(session, null, null, null, null) with { AmountDue = amountDue });
    }

    /// <summary>
    /// Extend a prepaid (fixed-duration) session by N minutes. The extra time is charged at the session
    /// tariff and taken from the player balance if a player is set (atomic, never below zero).
    /// </summary>
    [HttpPost("{id:guid}/extend")]
    public async Task<ActionResult<SessionDetailView>> Extend(Guid id, [FromBody] ExtendSessionRequest req)
    {
        if (req.Minutes <= 0 || req.Minutes > 24 * 60)
            return BadRequest("Minutes must be between 1 and 1440");

        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return NotFound();
        if (session.Status != "active") return BadRequest("Session is not active");
        if (!session.IsPrepaid || !session.EndUtc.HasValue)
            return BadRequest("Open-ended sessions have no end time to extend");

        var tariff = session.TariffId.HasValue ? await _db.Tariffs.FindAsync(session.TariffId.Value) : null;
        var extraCost = tariff != null ? Math.Round(tariff.PricePerHour * req.Minutes / 60m, 2) : 0m;
        var device = await _db.Devices.FindAsync(session.DeviceId);
        var user = session.UserId.HasValue ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId.Value) : null;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (user != null)
            {
                if (!await Balances.TryDebitAsync(_db, user.Id, extraCost))
                    return BadRequest($"Insufficient balance. Need {extraCost} ₴, have {await Balances.GetAsync(_db, user.Id)} ₴");
                _db.Transactions.Add(new TransactionEntity
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Amount = -extraCost,
                    Type = "session_charge",
                    Description = $"Продовження сесії на {device?.Hostname ?? "?"} (+{req.Minutes} хв) — {extraCost} ₴",
                    CreatedUtc = DateTimeOffset.UtcNow
                });
            }

            var now = DateTimeOffset.UtcNow;
            var baseEnd = session.EndUtc.Value > now ? session.EndUtc.Value : now;
            session.EndUtc = baseEnd.AddMinutes(req.Minutes);
            session.TotalCost += extraCost;
            try
            {
                await _db.SaveChangesAsync(); // fails if the session was ended or extended in between
                await AuditAsync(AuditActions.SessionExtended, session.Id,
                    $"Session on {device?.Hostname ?? "?"} extended by {req.Minutes} min, {extraCost} ₴ " +
                    (user != null ? "from the balance of @" + user.Username : "not paid from a balance (guest: cash at the desk)"),
                    new { device?.Hostname, minutes = req.Minutes, cost = extraCost, player = user?.Username, paidFromBalance = user != null, newEndUtc = session.EndUtc });
                await tx.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("The session was changed at the same time. Reload and try again.");
            }
        }

        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc = session.EndUtc }));

        return Ok(ToDetailView(session, tariff?.Name, tariff?.PricePerHour, user?.Username, device?.Hostname));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SessionDetailView>>> List([FromQuery] string? status)
    {
        var q = _db.Sessions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(s => s.Status == status);

        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var sessions = (await q.ToListAsync()).OrderByDescending(s => s.StartUtc).Take(200).ToList();

        // Resolve names
        var tariffIds = sessions.Where(s => s.TariffId.HasValue).Select(s => s.TariffId!.Value).Distinct();
        var tariffs = await _db.Tariffs.Where(t => tariffIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var userIds = sessions.Where(s => s.UserId.HasValue).Select(s => s.UserId!.Value).Distinct();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var deviceIds = sessions.Select(s => s.DeviceId).Distinct();
        var devices = await _db.Devices.Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id);

        return Ok(sessions.Select(s => ToDetailView(s,
            s.TariffId.HasValue && tariffs.ContainsKey(s.TariffId.Value) ? tariffs[s.TariffId.Value].Name : null,
            s.TariffId.HasValue && tariffs.ContainsKey(s.TariffId.Value) ? tariffs[s.TariffId.Value].PricePerHour : null,
            s.UserId.HasValue && users.ContainsKey(s.UserId.Value) ? users[s.UserId.Value].Username : null,
            devices.ContainsKey(s.DeviceId) ? devices[s.DeviceId].Hostname : null
        )));
    }

    [HttpGet("active")]
    public async Task<ActionResult<IEnumerable<SessionDetailView>>> Active()
    {
        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var sessions = (await _db.Sessions.AsNoTracking()
            .Where(s => s.Status == "active")
            .ToListAsync())
            .OrderByDescending(s => s.StartUtc).ToList();

        var tariffIds = sessions.Where(s => s.TariffId.HasValue).Select(s => s.TariffId!.Value).Distinct();
        var tariffs = await _db.Tariffs.Where(t => tariffIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var userIds = sessions.Where(s => s.UserId.HasValue).Select(s => s.UserId!.Value).Distinct();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var deviceIds = sessions.Select(s => s.DeviceId).Distinct();
        var devices = await _db.Devices.Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id);

        return Ok(sessions.Select(s => ToDetailView(s,
            s.TariffId.HasValue && tariffs.ContainsKey(s.TariffId.Value) ? tariffs[s.TariffId.Value].Name : null,
            s.TariffId.HasValue && tariffs.ContainsKey(s.TariffId.Value) ? tariffs[s.TariffId.Value].PricePerHour : null,
            s.UserId.HasValue && users.ContainsKey(s.UserId.Value) ? users[s.UserId.Value].Username : null,
            devices.ContainsKey(s.DeviceId) ? devices[s.DeviceId].Hostname : null
        )));
    }

    /// <summary>
    /// Revenue summary for the finance tab
    /// </summary>
    [HttpGet("revenue")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<RevenueSummary>> Revenue()
    {
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-(int)now.DayOfWeek);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        // SQLite cannot compare DateTimeOffset in WHERE — filter in memory
        var allEnded = await _db.Sessions.AsNoTracking()
            .Where(s => s.Status == "ended")
            .ToListAsync();
        var sessions = allEnded.Where(s => s.StartUtc >= monthStart).ToList();

        var today = sessions.Where(s => s.StartUtc >= todayStart).Sum(s => s.TotalCost);
        var week = sessions.Where(s => s.StartUtc >= weekStart).Sum(s => s.TotalCost);
        var month = sessions.Sum(s => s.TotalCost);

        return Ok(new RevenueSummary(today, week, month, "₴"));
    }

    private static SessionDetailView ToDetailView(SessionEntity s, string? tariffName, decimal? pricePerHour, string? username, string? hostname)
    {
        return new SessionDetailView(
            s.Id, s.DeviceId, hostname, s.TariffId, tariffName, pricePerHour,
            s.UserId, username, s.StartUtc, s.EndUtc, s.Status,
            s.TotalCost, s.IsPrepaid
        );
    }
}

public record StartSessionRequest(Guid DeviceId, Guid? TariffId, Guid? UserId, int? DurationMinutes);
public record ExtendSessionRequest(int Minutes);

public record SessionDetailView(
    Guid Id, Guid DeviceId, string? DeviceHostname,
    Guid? TariffId, string? TariffName, decimal? PricePerHour,
    Guid? UserId, string? Username,
    DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string Status,
    decimal TotalCost, bool IsPrepaid,
    decimal AmountDue = 0); // End: part of the cost that the balance did not cover (pay at the desk)

public record RevenueSummary(decimal Today, decimal Week, decimal Month, string Currency);
