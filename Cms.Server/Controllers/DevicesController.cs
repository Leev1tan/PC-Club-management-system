using Cms.Server.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Cms.Server.Data;
using Cms.Shared;
using Cms.Server.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceRepository _devices;
    private readonly CmsDbContext _db;
    private readonly DeviceRuntimeState? _runtime;
    private readonly AuditLogger? _audit;
    private readonly Cms.Server.Billing.ClubClock? _clock;

    public DevicesController(IDeviceRepository devices, CmsDbContext db, DeviceRuntimeState? runtime = null, AuditLogger? audit = null,
        Cms.Server.Billing.ClubClock? clock = null)
    {
        _devices = devices;
        _db = db;
        _runtime = runtime;
        _audit = audit;
        _clock = clock;
    }

    private Task AuditAsync(string action, Guid deviceId, string summary, object? details = null) =>
        _audit?.WriteAsync(action, "device", deviceId, summary, details) ?? Task.CompletedTask;

    [HttpPost("register")]
    [AllowAnonymous] // agent; authenticated by the enrollment token or its current device key
    [EnableRateLimiting(AuthConstants.AuthRateLimitPolicy)]
    public async Task<ActionResult<DeviceRegistrationResponse>> Register([FromBody] DeviceRegistrationRequest request)
    {
        var result = _devices.Register(request);
        if (result.Response != null && result.Kind != DeviceRegistrationKind.ReRegistered)
        {
            var how = result.Kind switch
            {
                DeviceRegistrationKind.Enrolled => "with the enrollment token",
                DeviceRegistrationKind.ReEnrolled => "again with the enrollment token (new key, old key invalid)",
                _ => "without token; waits for approval"
            };
            await AuditAsync(AuditActions.DeviceRegistered, result.Response.DeviceId, $"PC {request.Hostname} registered {how}",
                new { request.Hostname, kind = result.Kind.ToString(), result.Response.Status });
        }
        return result.Kind switch
        {
            DeviceRegistrationKind.Invalid => BadRequest(new { error = result.Error }),
            DeviceRegistrationKind.Unauthorized => Unauthorized(new { error = result.Error }),
            _ => Ok(result.Response)
        };
    }

    /// <summary>
    /// Agent heartbeat (X-Device-Key). Pending PCs may send heartbeats, so staff see them online
    /// in the approval list; the response tells the agent its status.
    /// </summary>
    [HttpPost("heartbeat")]
    [Authorize(Policy = Policies.Device)]
    public async Task<ActionResult<DeviceHeartbeatResponse>> Heartbeat([FromBody] DeviceHeartbeatRequest request)
    {
        var key = Request.Headers[DeviceKeyAuthenticationHandler.HeaderName].ToString();
        var status = _devices.Heartbeat(key, request);
        if (status == null) return Unauthorized();
        // Approved PCs get the hash of the staff PIN for the offline emergency unlock (null = no PIN set)
        var pinHash = status == DeviceStatuses.Approved ? await KioskSettings.GetStaffPinHashAsync(_db) : null;
        return Ok(new DeviceHeartbeatResponse(status, pinHash));
    }

    [HttpGet]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<ActionResult<IEnumerable<DeviceDetailView>>> List()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = await _db.Devices.AsNoTracking().ToListAsync();
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id);

        // Get active sessions for each device
        // The database allows one active session per PC; GroupBy keeps the list working on any data
        var activeSessions = (await _db.Sessions
                .Where(s => s.Status == "active")
                .AsNoTracking()
                .ToListAsync())
            .GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.StartUtc).First());

        var packageIds = activeSessions.Values.Where(s => s.PackageId.HasValue).Select(s => s.PackageId!.Value).Distinct().ToList();
        var packageNames = await _db.Packages.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        var tariffIds = activeSessions.Values.Where(s => s.TariffId.HasValue).Select(s => s.TariffId!.Value).Distinct().ToList();
        var tariffNames = await _db.Tariffs.AsNoTracking().Where(t => tariffIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name);

        // Commands queued for a device but not yet acknowledged by its agent
        var pendingCommands = await _db.Commands.AsNoTracking()
            .Where(c => c.Status == "pending" || c.Status == "delivered")
            .GroupBy(c => c.DeviceId)
            .Select(g => new { DeviceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DeviceId, x => x.Count);

        return Ok(devices.Select(d =>
        {
            var status = (d.LastSeenUtc.HasValue && (now - d.LastSeenUtc.Value) < TimeSpan.FromSeconds(20)) ? "online" : "offline";
            var zoneName = d.ZoneId.HasValue && zones.ContainsKey(d.ZoneId.Value) ? zones[d.ZoneId.Value].Name : null;
            var zoneColor = d.ZoneId.HasValue && zones.ContainsKey(d.ZoneId.Value) ? zones[d.ZoneId.Value].Color : null;
            var hasSession = activeSessions.ContainsKey(d.Id);
            var session = hasSession ? activeSessions[d.Id] : null;

            return new DeviceDetailView(
                d.Id, d.Hostname, d.OsVersion, d.AgentVersion,
                d.LastSeenUtc, d.LastIp, status,
                d.ZoneId, zoneName, zoneColor,
                d.PositionX, d.PositionY,
                hasSession ? "occupied" : (status == "offline" ? "offline" : "free"),
                session?.Id, session?.UserId, session?.EndUtc,
                _runtime?.IsLocked(d.Id),
                pendingCommands.TryGetValue(d.Id, out var pending) ? pending : 0,
                d.Status
            )
            {
                SessionPausedUtc = session?.PausedUtc,
                SessionCutoffUtc = session?.CutoffUtc,
                SessionLabel = session == null ? null
                    : session.PackageId.HasValue && packageNames.TryGetValue(session.PackageId.Value, out var package) ? package
                    : session.TariffId.HasValue && tariffNames.TryGetValue(session.TariffId.Value, out var tariff) ? tariff : null,
                SessionIsPackage = session?.PackageId != null
            };
        }));
    }

    [HttpPut("{deviceId:guid}/zone")]
    [Authorize(Policy = Policies.DeviceAdmin)]
    public async Task<IActionResult> AssignZone(Guid deviceId, [FromBody] AssignZoneRequest req)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        var before = device.ZoneId;
        device.ZoneId = req.ZoneId;
        await _db.SaveChangesAsync();
        if (before != req.ZoneId)
            await AuditAsync(AuditActions.DeviceZoneChanged, deviceId, $"Zone of {device.Hostname} changed", new { from = before, to = req.ZoneId });
        return Ok();
    }

    [HttpPut("{deviceId:guid}/position")]
    [Authorize(Policy = Policies.DeviceAdmin)]
    public async Task<IActionResult> SetPosition(Guid deviceId, [FromBody] SetPositionRequest req)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        device.PositionX = req.X;
        device.PositionY = req.Y;
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static readonly HashSet<string> StaffCommandTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        CommandTypes.Lock, CommandTypes.Unlock, CommandTypes.Restart, CommandTypes.Logoff, CommandTypes.Message
    };

    [HttpPost("{deviceId:guid}/commands")]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<ActionResult<CommandView>> Enqueue(Guid deviceId, [FromBody] EnqueueCommandRequest request)
    {
        // session_set is sent only by the server (it would give free time); unknown types are refused
        if (!StaffCommandTypes.Contains(request.Type ?? string.Empty))
            return BadRequest(new { error = $"Unknown command. Allowed: {string.Join(", ", StaffCommandTypes)}." });
        var type = request.Type!.ToLowerInvariant();
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device == null) return NotFound();

        var cmd = _devices.EnqueueCommand(deviceId, request with { Type = type });
        if (cmd is null) return NotFound();

        // An unlock of a PC without an active session gives time without payment
        var withoutSession = type == CommandTypes.Unlock &&
                             !await _db.Sessions.AnyAsync(s => s.DeviceId == deviceId && s.Status == "active");
        var text = type == CommandTypes.Message ? MessageText(request.Payload) : null;
        await AuditAsync(withoutSession ? AuditActions.UnlockWithoutSession : AuditActions.DeviceCommand, deviceId,
            withoutSession ? $"Unlocked {device.Hostname} without an active session (no payment)" : $"Command {type} sent to {device.Hostname}",
            new { type, message = text is { Length: > 200 } ? text[..200] : text });
        return Ok(cmd);
    }

    [HttpGet("{deviceId:guid}/commands")]
    [Authorize(Policy = Policies.ApprovedDevice)]
    public ActionResult<IEnumerable<CommandView>> Poll(Guid deviceId, [FromQuery] int max = 10)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        return Ok(_devices.PollCommands(deviceId, Math.Clamp(max, 1, 50)));
    }

    [HttpPost("{deviceId:guid}/commands/{commandId:guid}/ack")]
    [Authorize(Policy = Policies.ApprovedDevice)]
    public IActionResult Ack(Guid deviceId, Guid commandId, [FromBody] AckCommandRequest request)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        var ok = _devices.AckCommand(deviceId, commandId, request);
        if (!ok) return NotFound();
        return Ok();
    }

    /// <summary>
    /// Agent: reports what happened on the PC while the server could not see it. Today only
    /// "staff_unlock" (the staff PIN on the lock screen, checked offline), written to the audit log
    /// as device.emergency_unlock. The same event id is recorded once; at most 30 per PC and hour.
    /// </summary>
    [HttpPost("{deviceId:guid}/events")]
    [Authorize(Policy = Policies.ApprovedDevice)]
    public async Task<IActionResult> Event(Guid deviceId, [FromBody] DeviceEventRequest request)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device == null) return NotFound();
        var result = await DeviceEvents.RecordAsync(_db, _audit, device, request, _clock?.UtcNow ?? DateTimeOffset.UtcNow, _clock);
        return result switch
        {
            DeviceEvents.Result.Recorded or DeviceEvents.Result.Duplicate => Ok(new { recorded = result == DeviceEvents.Result.Recorded }),
            DeviceEvents.Result.TooMany => StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many events from this PC; try again later." }),
            _ => BadRequest(new { error = "Unknown event type or time." })
        };
    }

    private static string? MessageText(object? payload) => payload switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } o when o.TryGetProperty("text", out var t) => t.ToString(),
        System.Text.Json.JsonElement e => e.ToString(),
        _ => payload?.ToString()
    };

    /// <summary>Approve a PC that registered without the enrollment token.</summary>
    [HttpPost("{deviceId:guid}/approve")]
    [Authorize(Policy = Policies.DeviceAdmin)]
    public async Task<IActionResult> Approve(Guid deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        if (device.Status != DeviceStatuses.Pending) return Conflict(new { error = "Only a pending PC can be approved." });
        device.Status = DeviceStatuses.Approved;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.DeviceApproved, deviceId, $"PC {device.Hostname} approved", new { device.Hostname, device.LastIp });
        return Ok();
    }

    /// <summary>Reject a pending PC: its record is removed (a pending PC has no sessions).</summary>
    [HttpPost("{deviceId:guid}/reject")]
    [Authorize(Policy = Policies.DeviceAdmin)]
    public async Task<IActionResult> Reject(Guid deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        if (device.Status != DeviceStatuses.Pending) return Conflict(new { error = "Only a pending PC can be rejected. Revoke an approved PC instead." });
        _db.Devices.Remove(device);
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.DeviceRejected, deviceId, $"Pending PC {device.Hostname} rejected", new { device.Hostname, device.LastIp });
        return NoContent();
    }

    /// <summary>
    /// Revoke an approved PC (stolen key, PC removed from the club). The key stops working at once;
    /// sessions and history stay. The agent can then only register again as a new pending PC.
    /// </summary>
    [HttpPost("{deviceId:guid}/revoke")]
    [Authorize(Policy = Policies.DeviceAdmin)]
    public async Task<IActionResult> Revoke(Guid deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        if (device.Status == DeviceStatuses.Revoked) return Conflict(new { error = "The PC is already revoked." });
        device.Status = DeviceStatuses.Revoked;
        device.DeviceKey = DeviceKeys.Hash(DeviceKeys.Generate()); // nobody knows this key
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.DeviceRevoked, deviceId, $"PC {device.Hostname} revoked", new { device.Hostname });
        return Ok();
    }
}

/// <summary>Token = enrollment token (new PC); DeviceId + DeviceKey = current credentials (re-registration).</summary>
public record DeviceRegistrationRequest(string Hostname, string OsVersion, string AgentVersion, string? Token, Guid? DeviceId = null, string? DeviceKey = null);
public record DeviceRegistrationResponse(Guid DeviceId, string DeviceKey, string Status);
/// <summary>StaffPinHash: see Cms.Shared.StaffPinHash; only for approved PCs, null when no PIN is set.</summary>
public record DeviceHeartbeatResponse(string Status, string? StaffPinHash);

public enum DeviceRegistrationKind { ReRegistered, Enrolled, ReEnrolled, Pending, Unauthorized, Invalid }

public sealed record DeviceRegistrationResult(DeviceRegistrationKind Kind, DeviceRegistrationResponse? Response, string? Error)
{
    public static DeviceRegistrationResult Ok(DeviceRegistrationResponse response, DeviceRegistrationKind kind) => new(kind, response, null);
    public static DeviceRegistrationResult Unauthorized(string error) => new(DeviceRegistrationKind.Unauthorized, null, error);
    public static DeviceRegistrationResult Invalid(string error) => new(DeviceRegistrationKind.Invalid, null, error);
}
public record DeviceHeartbeatRequest(double CpuPercent, double MemPercent, string? ActiveUser, string Ip, TimeSpan Uptime);
public record DeviceView(Guid Id, string Hostname, string OsVersion, string AgentVersion, DateTimeOffset? LastSeenUtc, string? LastIp, string Status);
public record DeviceDetailView(
    Guid Id, string Hostname, string OsVersion, string AgentVersion,
    DateTimeOffset? LastSeenUtc, string? LastIp, string Status,
    Guid? ZoneId, string? ZoneName, string? ZoneColor,
    int? PositionX, int? PositionY,
    string OccupancyStatus, // "free", "occupied", "offline"
    Guid? ActiveSessionId, Guid? ActiveUserId, DateTimeOffset? SessionEndUtc,
    bool? IsLocked = null,  // last lock state confirmed by the agent; null = unknown
    int PendingCommands = 0, // queued commands not yet acknowledged by the agent
    string Enrollment = "approved" // pending (waits for approval), approved, revoked
)
{
    /// <summary>Set while the active session is paused (remaining = SessionEndUtc - SessionPausedUtc).</summary>
    public DateTimeOffset? SessionPausedUtc { get; init; }
    public DateTimeOffset? SessionCutoffUtc { get; init; }
    /// <summary>Package or tariff name of the active session.</summary>
    public string? SessionLabel { get; init; }
    public bool SessionIsPackage { get; init; }
}
public record EnqueueCommandRequest(string Type, object? Payload);
/// <summary>Event of the PC (Cms.Shared.LauncherEvent): Id (for de-duplication), Type staff_unlock, when, why the PC was locked.</summary>
public record DeviceEventRequest(Guid Id, string? Type, DateTimeOffset OccurredUtc, string? LockReason = null, string? WindowsUser = null);
public record CommandView(Guid Id, string Type, object? Payload);
public record AckCommandRequest(string Status, string? Result);
public record AssignZoneRequest(Guid? ZoneId);
public record SetPositionRequest(int? X, int? Y);

