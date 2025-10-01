using Microsoft.AspNetCore.Mvc;
using Cms.Server.Repositories;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private readonly ISessionRepository _sessions;
    private readonly IDeviceRepository _devices;

    public SessionsController(ISessionRepository sessions, IDeviceRepository devices)
    {
        _sessions = sessions;
        _devices = devices;
    }

    [HttpPost]
    public ActionResult<SessionView> Start([FromBody] StartSessionRequest request)
    {
        if (request.DurationMinutes <= 0) return BadRequest("durationMinutes <= 0");
        var session = _sessions.Start(request.DeviceId, TimeSpan.FromMinutes(request.DurationMinutes));
        // Enqueue session_set command to agent with endUtc
        _devices.EnqueueCommand(request.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc = session.EndUtc }));
        // Ensure device is unlocked at session start
        _devices.EnqueueCommand(request.DeviceId, new EnqueueCommandRequest("unlock", null));
        return Ok(new SessionView(session.Id, session.DeviceId, session.StartUtc, session.EndUtc));
    }

    [HttpPost("{id:guid}/end")]
    public ActionResult<SessionView> End(Guid id)
    {
        var session = _sessions.End(id);
        if (session is null) return NotFound();
        // Send immediate end to agent
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc = DateTimeOffset.UtcNow }));
        return Ok(new SessionView(session.Id, session.DeviceId, session.StartUtc, session.EndUtc));
    }

    [HttpGet]
    public IEnumerable<SessionView> List() => _sessions.List().Select(s => new SessionView(s.Id, s.DeviceId, s.StartUtc, s.EndUtc));
}

public record StartSessionRequest(Guid DeviceId, int DurationMinutes);
public record SessionView(Guid Id, Guid DeviceId, DateTimeOffset StartUtc, DateTimeOffset? EndUtc);

