using Cms.Server.Accounts;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cms.Server.Controllers;

/// <summary>
/// The club game-account pool for staff (spec area 8). The front desk sees which PC uses which account (masked login)
/// and frees a stuck one; Owner and Manager add accounts, rotate the secret, switch them on or off and remove them.
/// No answer ever contains a login or a secret.
/// </summary>
[ApiController]
[Route("api/game-accounts")]
[Authorize(Policy = Policies.FrontDesk)]
public class GameAccountsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly GameAccountVault _vault;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public GameAccountsController(CmsDbContext db, GameAccountVault vault, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _vault = vault;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private GameAccountService Service => new(_db, _vault, _clock, _audit, StaffActor.From(HttpContext?.User));

    private static ActionResult Result(BillingResult<GameAccountView> r) => r.Ok ? new OkObjectResult(r.Value) : r.Error!.ToActionResult();

    [HttpGet]
    public async Task<ActionResult<IEnumerable<GameAccountView>>> List() => Ok(await GameAccountService.ViewsAsync(_db));

    [HttpPost]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Add([FromBody] AddGameAccountRequest req) => Result(await Service.AddAsync(req));

    /// <summary>A new secret (write-only). The answer has only HasSecret.</summary>
    [HttpPut("{id:guid}/secret")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Rotate(Guid id, [FromBody] RotateSecretRequest req) => Result(await Service.RotateAsync(id, req));

    [HttpPut("{id:guid}/active")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Active(Guid id, [FromBody] AccountActiveRequest req) => Result(await Service.SetActiveAsync(id, req.IsActive));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult> Remove(Guid id) => Result(await Service.RemoveAsync(id));

    /// <summary>Frees an account that a PC keeps (front desk): the lease ends now.</summary>
    [HttpPost("{id:guid}/release")]
    public async Task<ActionResult> Release(Guid id) => Result(await Service.ReleaseAsync(id));
}

/// <summary>
/// The agent of an approved PC (device key) asks for a club account when the player opens a game that needs one, and
/// fetches its credentials once. The lock screen asks the agent through the local pipe; it never talks to the server.
/// </summary>
[ApiController]
[Route("api/devices/{deviceId:guid}/game-account")]
[Authorize(Policy = Policies.ApprovedDevice)]
public class DeviceGameAccountController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly GameAccountVault _vault;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public DeviceGameAccountController(CmsDbContext db, GameAccountVault vault, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _vault = vault;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private GameAccountService Service => new(_db, _vault, _clock, _audit);

    [HttpPost]
    public async Task<ActionResult<DeviceLeaseView>> Lease(Guid deviceId, [FromBody] LeaseRequest req)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        var r = await Service.LeaseAsync(deviceId, req.GameId);
        return r.Ok ? Ok(r.Value) : r.Error!.ToActionResult();
    }

    /// <summary>The credentials of a lease of this PC, once (no-store); 409 credential_delivered afterwards.</summary>
    [HttpPost("{leaseId:long}/credential")]
    public async Task<ActionResult<DeviceCredentialView>> Credential(Guid deviceId, long leaseId)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        Response.Headers.CacheControl = "no-store";
        var r = await Service.DeliverAsync(deviceId, leaseId);
        return r.Ok ? Ok(r.Value) : r.Error!.ToActionResult();
    }
}
