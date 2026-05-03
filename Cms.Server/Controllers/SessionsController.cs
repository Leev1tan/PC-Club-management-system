using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;

    public SessionsController(CmsDbContext db, IDeviceRepository devices)
    {
        _db = db;
        _devices = devices;
    }

    /// <summary>
    /// Start a new session. Supports two modes:
    /// 1. Prepaid: specify durationMinutes — cost calculated upfront
    /// 2. Open-ended: no durationMinutes — runs until manually ended, cost calculated at end
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SessionDetailView>> Start([FromBody] StartSessionRequest req)
    {
        var device = await _db.Devices.FindAsync(req.DeviceId);
        if (device == null) return NotFound("Device not found");

        var now = DateTimeOffset.UtcNow;
        var activeForDevice = await _db.Sessions
            .Where(s => s.DeviceId == req.DeviceId && s.Status == "active")
            .ToListAsync();

        foreach (var expired in activeForDevice.Where(s =>
                     s.IsPrepaid && s.EndUtc.HasValue && s.EndUtc.Value <= now))
        {
            expired.Status = "ended";
        }

        var blockingSession = activeForDevice.FirstOrDefault(s =>
            s.Status == "active" &&
            (!s.IsPrepaid || !s.EndUtc.HasValue || s.EndUtc.Value > now));
        if (blockingSession != null)
            return Conflict("Device already has an active session.");

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

        bool isPrepaid = req.DurationMinutes.HasValue && req.DurationMinutes > 0;
        var endUtc = isPrepaid ? now.AddMinutes(req.DurationMinutes!.Value) : (DateTimeOffset?)null;
        decimal totalCost = isPrepaid ? Math.Round(tariff.PricePerHour * req.DurationMinutes!.Value / 60m, 2) : 0;

        // If user is specified and prepaid, check/deduct balance
        UserEntity? user = null;
        if (req.UserId.HasValue)
        {
            user = await _db.Users.FindAsync(req.UserId.Value);
            if (user == null) return NotFound("User not found");

            if (isPrepaid && user.Balance < totalCost)
                return BadRequest($"Insufficient balance. Need {totalCost} ₴, have {user.Balance} ₴");

            if (isPrepaid)
            {
                user.Balance -= totalCost;
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
        }

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

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        // Send commands to agent
        if (endUtc.HasValue)
            _devices.EnqueueCommand(req.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc }));
        _devices.EnqueueCommand(req.DeviceId, new EnqueueCommandRequest("unlock", null));

        return Ok(ToDetailView(session, tariff.Name, tariff.PricePerHour, user?.Username, device.Hostname));
    }

    /// <summary>
    /// End a session. Calculates final cost for open-ended sessions.
    /// </summary>
    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<SessionDetailView>> End(Guid id)
    {
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return NotFound();
        if (session.Status == "ended") return BadRequest("Session already ended");

        var now = DateTimeOffset.UtcNow;
        session.EndUtc = now;
        session.Status = "ended";

        // Calculate cost for open-ended sessions
        if (!session.IsPrepaid && session.TariffId.HasValue)
        {
            var tariff = await _db.Tariffs.FindAsync(session.TariffId.Value);
            if (tariff != null)
            {
                var minutes = (decimal)(now - session.StartUtc).TotalMinutes;
                session.TotalCost = Math.Round(tariff.PricePerHour * minutes / 60m, 2);

                // Deduct from user balance if applicable
                if (session.UserId.HasValue)
                {
                    var user = await _db.Users.FindAsync(session.UserId.Value);
                    if (user != null)
                    {
                        var device = await _db.Devices.FindAsync(session.DeviceId);
                        user.Balance -= session.TotalCost;
                        _db.Transactions.Add(new TransactionEntity
                        {
                            Id = Guid.NewGuid(),
                            UserId = user.Id,
                            Amount = -session.TotalCost,
                            Type = "session_charge",
                            Description = $"Сесія на {device?.Hostname ?? "?"} ({(int)minutes} хв) — {session.TotalCost} ₴",
                            CreatedUtc = now
                        });
                    }
                }
            }
        }

        await _db.SaveChangesAsync();

        // Lock the device + inform agent
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc = now }));
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("lock", null));

        return Ok(ToDetailView(session, null, null, null, null));
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

public record SessionDetailView(
    Guid Id, Guid DeviceId, string? DeviceHostname,
    Guid? TariffId, string? TariffName, decimal? PricePerHour,
    Guid? UserId, string? Username,
    DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string Status,
    decimal TotalCost, bool IsPrepaid);

public record RevenueSummary(decimal Today, decimal Week, decimal Month, string Currency);
