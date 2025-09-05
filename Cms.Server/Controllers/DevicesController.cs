using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Cms.Server.Repositories;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceRepository _devices;

    public DevicesController(IDeviceRepository devices)
    {
        _devices = devices;
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
    public ActionResult<IEnumerable<DeviceView>> List()
    {
        return Ok(_devices.List());
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
public record EnqueueCommandRequest(string Type, object? Payload);
public record CommandView(Guid Id, string Type, object? Payload);
public record AckCommandRequest(string Status, string? Result);

