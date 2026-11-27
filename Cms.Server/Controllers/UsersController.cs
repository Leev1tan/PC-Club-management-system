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
    private readonly AuditLogger _audit;

    public UsersController(CmsDbContext db, LoginThrottle throttle, AuditLogger audit)
    {
        _db = db;
        _throttle = throttle;
        _audit = audit;
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
        await _audit.WriteAsync(AuditActions.PlayerRegistered, "player", user.Id, $"Player @{user.Username} registered", new { user.Username });
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
        await _audit.WriteAsync(AuditActions.PlayerPasswordSet, "player", user.Id, $"Password / PIN of @{user.Username} set by staff");
        return NoContent();
    }

    /// <summary>Cash top-up at the desk (atomic; ledger row in the same transaction).</summary>
    [HttpPost("{id:guid}/topup")]
    public async Task<ActionResult<UserView>> TopUp(Guid id, [FromBody] TopUpRequest req)
    {
        if (req.Amount <= 0 || req.Amount > 100_000) return BadRequest("Amount must be positive (at most 100000)");

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (!await Balances.CreditAsync(_db, id, req.Amount)) return NotFound();
            _db.Transactions.Add(new TransactionEntity
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Amount = req.Amount,
                Type = "topup",
                Description = string.IsNullOrWhiteSpace(req.Description) ? $"Поповнення балансу +{req.Amount} ₴" : req.Description.Trim(),
                CreatedUtc = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync();
            await _audit.WriteAsync(AuditActions.PlayerTopUp, "player", id, $"Balance top-up +{req.Amount} ₴", new { amount = req.Amount, req.Description });
            await tx.CommitAsync();
        }

        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
        return Ok(new UserView(user.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc));
    }

    /// <summary>
    /// Balance correction with a mandatory reason (Owner, Manager): positive or negative. A negative
    /// correction cannot take the balance below zero.
    /// </summary>
    [HttpPost("{id:guid}/adjust")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<UserView>> Adjust(Guid id, [FromBody] AdjustBalanceRequest req)
    {
        if (req.Amount == 0 || Math.Abs(req.Amount) > 100_000) return BadRequest("Amount must not be 0 (at most 100000)");
        var reason = req.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length < 3) return BadRequest("A reason is required");
        if (reason.Length > 300) reason = reason[..300];

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var ok = req.Amount > 0
                ? await Balances.CreditAsync(_db, id, req.Amount)
                : await Balances.TryDebitAsync(_db, id, -req.Amount);
            if (!ok)
            {
                if (!await _db.Users.AnyAsync(u => u.Id == id)) return NotFound();
                return BadRequest("The balance is too low for this correction");
            }
            _db.Transactions.Add(new TransactionEntity
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Amount = req.Amount,
                Type = "adjustment",
                Description = $"Коригування {req.Amount:+0.##;-0.##} ₴: {reason}",
                CreatedUtc = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync();
            await _audit.WriteAsync(AuditActions.PlayerBalanceAdjusted, "player", id, $"Balance correction {req.Amount:+0.##;-0.##} ₴: {reason}", new { amount = req.Amount, reason });
            await tx.CommitAsync();
        }

        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
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
public record AdjustBalanceRequest(decimal Amount, string? Reason);
public record UserView(Guid Id, string Username, string DisplayName, decimal Balance, decimal BonusPoints, DateTimeOffset CreatedUtc);
public record TransactionView(Guid Id, Guid UserId, decimal Amount, string Type, string? Description, DateTimeOffset CreatedUtc);
