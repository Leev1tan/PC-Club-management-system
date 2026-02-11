using System.Security.Cryptography;
using System.Text;
using Cms.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly CmsDbContext _db;

    public UsersController(CmsDbContext db) => _db = db;

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

    [HttpPost("register")]
    public async Task<ActionResult<UserView>> Register([FromBody] RegisterUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest("Username is required");

        if (await _db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict("Username already exists");

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = req.Username.Trim().ToLower(),
            DisplayName = req.DisplayName ?? req.Username,
            PasswordHash = !string.IsNullOrWhiteSpace(req.Password) ? HashPassword(req.Password) : null,
            Balance = 0,
            BonusPoints = 0,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new UserView(user.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc));
    }

    [HttpPost("login")]
    public async Task<ActionResult<UserView>> Login([FromBody] LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username.Trim().ToLower());
        if (user == null) return Unauthorized("Invalid credentials");

        if (user.PasswordHash != null)
        {
            if (string.IsNullOrWhiteSpace(req.Password) || HashPassword(req.Password) != user.PasswordHash)
                return Unauthorized("Invalid credentials");
        }

        return Ok(new UserView(user.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc));
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

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(bytes);
    }
}

public record RegisterUserRequest(string Username, string? Password, string? DisplayName);
public record LoginRequest(string Username, string? Password);
public record TopUpRequest(decimal Amount, string? Description);
public record UserView(Guid Id, string Username, string DisplayName, decimal Balance, decimal BonusPoints, DateTimeOffset CreatedUtc);
public record TransactionView(Guid Id, Guid UserId, decimal Amount, string Type, string? Description, DateTimeOffset CreatedUtc);
