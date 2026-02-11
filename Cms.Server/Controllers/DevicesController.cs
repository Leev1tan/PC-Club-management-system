using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceRepository _devices;
    private readonly CmsDbContext _db;

    public DevicesController(IDeviceRepository devices, CmsDbContext db)
    {
        _devices = devices;
        _db = db;
    }

    [HttpPost("register")]
    public ActionResult<DeviceRegistrationResponse> Register([FromBody] DeviceRegistrationRequest request)
    {
        var result = _devices.Register(request);
        return Ok(result);
    }

    [HttpPost("heartbeat")]
    public IActionResult Heartbeat([FromHeader(Name = "X-Device-Key")] string? deviceKey, [FromBody] DeviceHeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return Unauthorized();
        var ok = _devices.Heartbeat(deviceKey, request);
        if (!ok) return Unauthorized();
        return Ok();
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<DeviceDetailView>>> List()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = await _db.Devices.AsNoTracking().ToListAsync();
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id);

        // Get active sessions for each device
        var activeSessions = await _db.Sessions
            .Where(s => s.Status == "active")
            .AsNoTracking()
            .ToDictionaryAsync(s => s.DeviceId);

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
                session?.Id, session?.UserId, session?.EndUtc
            );
        }));
    }

    [HttpPut("{deviceId:guid}/zone")]
    public async Task<IActionResult> AssignZone(Guid deviceId, [FromBody] AssignZoneRequest req)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        device.ZoneId = req.ZoneId;
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPut("{deviceId:guid}/position")]
    public async Task<IActionResult> SetPosition(Guid deviceId, [FromBody] SetPositionRequest req)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return NotFound();
        device.PositionX = req.X;
        device.PositionY = req.Y;
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("{deviceId:guid}/commands")]
    public ActionResult<CommandView> Enqueue(Guid deviceId, [FromBody] EnqueueCommandRequest request)
    {
        var cmd = _devices.EnqueueCommand(deviceId, request);
        if (cmd is null) return NotFound();
        return Ok(cmd);
    }

    [HttpGet("{deviceId:guid}/commands")]
    public ActionResult<IEnumerable<CommandView>> Poll(Guid deviceId, [FromQuery] int max = 10)
    {
        return Ok(_devices.PollCommands(deviceId, max));
    }

    [HttpPost("{deviceId:guid}/commands/{commandId:guid}/ack")]
    public IActionResult Ack(Guid deviceId, Guid commandId, [FromBody] AckCommandRequest request)
    {
        var ok = _devices.AckCommand(deviceId, commandId, request);
        if (!ok) return NotFound();
        return Ok();
    }
}

public record DeviceRegistrationRequest(string Hostname, string OsVersion, string AgentVersion, string? Token);
public record DeviceRegistrationResponse(Guid DeviceId, string DeviceKey);
public record DeviceHeartbeatRequest(double CpuPercent, double MemPercent, string? ActiveUser, string Ip, TimeSpan Uptime);
public record DeviceView(Guid Id, string Hostname, string OsVersion, string AgentVersion, DateTimeOffset? LastSeenUtc, string? LastIp, string Status);
public record DeviceDetailView(
    Guid Id, string Hostname, string OsVersion, string AgentVersion,
    DateTimeOffset? LastSeenUtc, string? LastIp, string Status,
    Guid? ZoneId, string? ZoneName, string? ZoneColor,
    int? PositionX, int? PositionY,
    string OccupancyStatus, // "free", "occupied", "offline"
    Guid? ActiveSessionId, Guid? ActiveUserId, DateTimeOffset? SessionEndUtc
);
public record EnqueueCommandRequest(string Type, object? Payload);
public record CommandView(Guid Id, string Type, object? Payload);
public record AckCommandRequest(string Status, string? Result);
public record AssignZoneRequest(Guid? ZoneId);
public record SetPositionRequest(int? X, int? Y);

