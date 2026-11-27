using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>Read-only audit log (Owner, Manager). There is no endpoint that changes or deletes entries.</summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = Policies.Management)]
public class AuditController : ControllerBase
{
    private readonly CmsDbContext _db;

    public AuditController(CmsDbContext db) => _db = db;

    /// <summary>Newest first. Paging: pass the smallest id of the last page as beforeId.</summary>
    /// <param name="action">Action name or prefix, for example "session." or "device.unlock_without_session"</param>
    /// <param name="actor">Exact staff user name</param>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditEntryView>>> List(
        [FromQuery] long? beforeId, [FromQuery] int take = 100, [FromQuery] string? action = null, [FromQuery] string? actor = null)
    {
        var q = _db.AuditLog.AsNoTracking().AsQueryable();
        if (beforeId.HasValue) q = q.Where(e => e.Id < beforeId.Value);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var prefix = action.Trim();
            q = q.Where(e => e.Action.StartsWith(prefix));
        }
        if (!string.IsNullOrWhiteSpace(actor))
        {
            var name = actor.Trim().ToLowerInvariant();
            q = q.Where(e => e.ActorUsername == name);
        }
        var entries = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return Ok(entries.Select(e => new AuditEntryView(e.Id, e.CreatedUtc, e.ActorUsername, e.ActorRole, e.Action,
            e.TargetType, e.TargetId, e.Summary, e.DetailsJson, e.Ip) { Hash = e.Hash, PrevHash = e.PrevHash }));
    }

    /// <summary>
    /// Owner: recomputes the hash chain of the whole log and reports the first broken row (changed,
    /// removed or inserted rows) and the head hash. Also checks the head hashes stored by closed shifts.
    /// </summary>
    [HttpGet("verify")]
    [Authorize(Policy = Policies.OwnerOnly)]
    public async Task<ActionResult<AuditChain.VerifyResult>> Verify() => Ok(await AuditChain.VerifyAsync(_db, HttpContext.RequestAborted));
}

public record AuditEntryView(long Id, DateTimeOffset CreatedUtc, string? ActorUsername, string? ActorRole, string Action,
    string? TargetType, string? TargetId, string? Summary, string? DetailsJson, string? Ip)
{
    /// <summary>SHA-256 link of the hash chain (see Auth/AuditChain).</summary>
    public string? Hash { get; init; }
    public string? PrevHash { get; init; }
}
