using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Lockouts of the player login on the lock screens (PlayerLoginLimiter, kept in memory): a manager or the owner
/// sees them and clears one, for a user name or for a PC; audited. A name that is no account is listed without
/// the text (players type their password into the name field), only with an opaque id that clears it.
/// </summary>
[ApiController]
[Route("api/player-lockouts")]
[Authorize(Policy = Policies.Management)]
public class PlayerLockoutsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly PlayerLoginLimiter _limiter;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public PlayerLockoutsController(CmsDbContext db, PlayerLoginLimiter limiter, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _limiter = limiter;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    [HttpGet]
    public async Task<ActionResult<PlayerLockoutsView>> List()
    {
        var now = _clock.UtcNow;
        var accounts = _limiter.LockedAccounts(now);
        var names = accounts.Select(a => a.Name).ToList();
        var known = (await _db.Users.AsNoTracking().Where(u => names.Contains(u.Username)).Select(u => u.Username).ToListAsync()).ToHashSet();
        var devices = _limiter.LockedDevices(now);
        var ids = devices.Select(d => d.DeviceId).ToList();
        var hosts = await _db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Hostname);
        return Ok(new PlayerLockoutsView(
            accounts.Select(a => new LockedAccountView(a.Id, known.Contains(a.Name) ? a.Name : null, a.LockedUntilUtc)).ToList(),
            devices.Select(d => new LockedDeviceView(d.DeviceId, hosts.GetValueOrDefault(d.DeviceId) ?? "?", d.LockedUntilUtc)).ToList()));
    }

    /// <summary>Clears one lockout: Username (as typed) or Id (from the list) of a name, or DeviceId of a PC.</summary>
    [HttpPost("clear")]
    public async Task<IActionResult> Clear([FromBody] ClearLockoutRequest req)
    {
        var now = _clock.UtcNow;
        if (req.DeviceId is { } deviceId)
        {
            var host = await _db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.Hostname).FirstOrDefaultAsync();
            if (!_limiter.ClearDevice(deviceId, now)) return NotLocked();
            await _audit.WriteAsync(AuditActions.PlayerLoginLockoutCleared, "device", deviceId,
                $"Player login lockout cleared for PC {host ?? "?"}", new { device = host });
            return Ok(new { cleared = "device", deviceId });
        }
        if (string.IsNullOrWhiteSpace(req.Username) && string.IsNullOrWhiteSpace(req.Id))
            return BadRequest(new { error = "Give a user name, the id of a locked name or a PC.", code = "bad_lockout" });
        var name = _limiter.ClearAccount(string.IsNullOrWhiteSpace(req.Username) ? null : req.Username, req.Id, now);
        if (name == null) return NotLocked();
        var account = await _db.Users.AsNoTracking().Where(u => u.Username == name).Select(u => new { u.Id, u.Username }).FirstOrDefaultAsync();
        // A name that is no account is not written down (it can be a typed password)
        await _audit.WriteAsync(AuditActions.PlayerLoginLockoutCleared, account != null ? "player" : "lockout", account?.Id.ToString() ?? PlayerLoginLimiter.AccountId(name),
            account != null ? $"Player login lockout cleared for account @{account.Username}" : "Player login lockout cleared for an unknown user name",
            new { player = account?.Username });
        return Ok(new { cleared = "account", id = PlayerLoginLimiter.AccountId(name), username = account?.Username });
    }

    private ConflictObjectResult NotLocked() => Conflict(new { error = "This login is not locked (any more).", code = "lockout_not_found" });
}

public record ClearLockoutRequest(string? Username = null, string? Id = null, Guid? DeviceId = null);
/// <summary>Username: only when the locked name is an account; otherwise null (Id clears it).</summary>
public record LockedAccountView(string Id, string? Username, DateTimeOffset LockedUntilUtc);
public record LockedDeviceView(Guid DeviceId, string Hostname, DateTimeOffset LockedUntilUtc);
public record PlayerLockoutsView(IReadOnlyList<LockedAccountView> Accounts, IReadOnlyList<LockedDeviceView> Devices);
