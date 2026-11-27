using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.FrontDesk)]
public class UsersController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly LoginThrottle _throttle;

    public UsersController(CmsDbContext db, LoginThrottle throttle)
    {
        _db = db;
        _throttle = throttle;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserView>>> List()
    {
        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var users = (await _db.Users.AsNoTracking().ToListAsync()).OrderByDescending(u => u.CreatedUtc).ToList();
        return Ok(users.Select(u => new UserView(u.Id, u.Username, u.DisplayName, u.Balance, u.BonusPoints, u.CreatedUtc)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserView>> Get(Guid id)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null) return NotFound();
        return Ok(new UserView(u.Id, u.Username, u.DisplayName, u.Balance, u.BonusPoints, u.CreatedUtc));
    }

    /// <summary>Staff create a player account. A password or PIN is required for the lock-screen login.</summary>
    [HttpPost("register")]
    public async Task<ActionResult<UserView>> Register([FromBody] RegisterUserRequest req)
    {
        var username = (req.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (username.Length is 0 or > 100)
            return BadRequest("Username is required (at most 100 characters)");
        var passwordError = Passwords.ValidatePlayerPassword(req.Password);
        if (passwordError != null) return BadRequest(passwordError);

        if (await _db.Users.AnyAsync(u => u.Username == username))
            return Conflict("Username already exists");

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim(),
            PasswordHash = Passwords.Hash(req.Password!),
            Balance = 0,
            BonusPoints = 0,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new UserView(user.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc));
    }

    /// <summary>
    /// Player login on the lock screen. A password or PIN is always required: an account without one
    /// cannot log in until staff set it (POST /api/users/{id}/password). Old unsalted SHA-256 hashes
    /// are replaced by a salted hash at the first successful login.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous] // player login on the lock screen (Cms.Launcher)
    [EnableRateLimiting(AuthConstants.AuthRateLimitPolicy)]
    public async Task<ActionResult<UserView>> Login([FromBody] LoginRequest req)
    {
        var username = (req.Username ?? string.Empty).Trim().ToLowerInvariant();
        var key = LoginThrottle.Key("player", username, HttpContext.ClientIp());
        var now = DateTimeOffset.UtcNow;
        if (_throttle.IsLockedOut(key, now))
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed attempts. Try again in 15 minutes.");

        var user = username.Length == 0 ? null : await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        var check = PasswordCheck.Failed;
        if (user?.PasswordHash != null)
            check = Passwords.Verify(user.PasswordHash, req.Password);
        else
            Passwords.VerifyDummy(req.Password); // same time for unknown users and users without a password

        if (check == PasswordCheck.Failed)
        {
            _throttle.RecordFailure(key, now);
            return Unauthorized("Invalid credentials");
        }

        _throttle.Reset(key);
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user!.PasswordHash = Passwords.Hash(req.Password!);
            await _db.SaveChangesAsync();
        }
        return Ok(new UserView(user!.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc));
    }

    /// <summary>Set or reset the password / PIN of a player (front desk).</summary>
    [HttpPost("{id:guid}/password")]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] SetPlayerPasswordRequest req)
    {
        var error = Passwords.ValidatePlayerPassword(req.Password);
        if (error != null) return BadRequest(error);
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.PasswordHash = Passwords.Hash(req.Password!);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/topup")]
    public async Task<ActionResult<UserView>> TopUp(Guid id, [FromBody] TopUpRequest req)
    {
        if (req.Amount <= 0) return BadRequest("Amount must be positive");

        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        user.Balance += req.Amount;

        _db.Transactions.Add(new TransactionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Amount = req.Amount,
            Type = "topup",
            Description = req.Description ?? $"Поповнення балансу +{req.Amount} ₴",
            CreatedUtc = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync();
        return Ok(new UserView(user.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc));
    }

    [HttpGet("{id:guid}/transactions")]
    public async Task<ActionResult<IEnumerable<TransactionView>>> Transactions(Guid id)
    {
        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var txs = (await _db.Transactions
            .Where(t => t.UserId == id)
            .AsNoTracking()
            .ToListAsync())
            .OrderByDescending(t => t.CreatedUtc)
            .Take(100)
            .ToList();
        return Ok(txs.Select(t => new TransactionView(t.Id, t.UserId, t.Amount, t.Type, t.Description, t.CreatedUtc)));
    }

}

public record RegisterUserRequest(string? Username, string? Password, string? DisplayName);
public record LoginRequest(string? Username, string? Password);
public record SetPlayerPasswordRequest(string? Password);
public record TopUpRequest(decimal Amount, string? Description);
public record UserView(Guid Id, string Username, string DisplayName, decimal Balance, decimal BonusPoints, DateTimeOffset CreatedUtc);
public record TransactionView(Guid Id, Guid UserId, decimal Amount, string Type, string? Description, DateTimeOffset CreatedUtc);
