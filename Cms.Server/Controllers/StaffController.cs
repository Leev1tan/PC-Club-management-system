using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>Staff accounts (Owner only). Accounts are deactivated, never deleted, so the audit log keeps its references.</summary>
[ApiController]
[Route("api/staff")]
[Authorize(Policy = Policies.OwnerOnly)]
public class StaffController : ControllerBase
{
    private readonly CmsDbContext _db;

    public StaffController(CmsDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StaffView>>> List()
    {
        var staff = await _db.StaffUsers.AsNoTracking().ToListAsync();
        return Ok(staff.OrderBy(s => s.Username).Select(StaffView.From));
    }

    [HttpPost]
    public async Task<ActionResult<StaffView>> Create([FromBody] CreateStaffRequest req)
    {
        var username = StaffNames.Normalize(req.Username);
        if (!StaffNames.IsValid(username))
            return BadRequest(new { error = "User name: 2-64 characters, a-z, 0-9, '.', '_' or '-'." });
        var role = StaffRoles.Normalize(req.Role);
        if (role == null) return BadRequest(new { error = "Role must be Owner, Manager, Cashier or Tech." });
        var passwordError = Passwords.ValidateStaffPassword(req.Password);
        if (passwordError != null) return BadRequest(new { error = passwordError });
        if (await _db.StaffUsers.AnyAsync(s => s.Username == username))
            return Conflict(new { error = "This user name already exists." });

        var staff = new StaffUserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim(),
            PasswordHash = Passwords.Hash(req.Password!),
            Role = role,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        _db.StaffUsers.Add(staff);
        await _db.SaveChangesAsync();
        return Ok(StaffView.From(staff));
    }

    /// <summary>Change display name, role or active flag. At least one active Owner always remains.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<StaffView>> Update(Guid id, [FromBody] UpdateStaffRequest req)
    {
        var staff = await _db.StaffUsers.FindAsync(id);
        if (staff == null) return NotFound();

        var role = req.Role == null ? staff.Role : StaffRoles.Normalize(req.Role);
        if (role == null) return BadRequest(new { error = "Role must be Owner, Manager, Cashier or Tech." });
        var active = req.IsActive ?? staff.IsActive;

        var removesOwner = staff.Role == StaffRoles.Owner && staff.IsActive && (role != StaffRoles.Owner || !active);
        if (removesOwner && !await _db.StaffUsers.AnyAsync(s => s.Id != id && s.Role == StaffRoles.Owner && s.IsActive))
            return Conflict(new { error = "At least one active Owner account must remain." });

        if (role != staff.Role || active != staff.IsActive)
            staff.SecurityStamp = Guid.NewGuid().ToString("N"); // ends the current logins of this account
        staff.Role = role;
        staff.IsActive = active;
        if (!string.IsNullOrWhiteSpace(req.DisplayName)) staff.DisplayName = req.DisplayName.Trim();
        await _db.SaveChangesAsync();
        return Ok(StaffView.From(staff));
    }

    /// <summary>Set a new password for a staff account (for example a forgotten one).</summary>
    [HttpPost("{id:guid}/password")]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] SetStaffPasswordRequest req)
    {
        var staff = await _db.StaffUsers.FindAsync(id);
        if (staff == null) return NotFound();
        var error = Passwords.ValidateStaffPassword(req.Password);
        if (error != null) return BadRequest(new { error });
        staff.PasswordHash = Passwords.Hash(req.Password!);
        staff.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateStaffRequest(string? Username, string? DisplayName, string? Password, string? Role);
public record UpdateStaffRequest(string? DisplayName, string? Role, bool? IsActive);
public record SetStaffPasswordRequest(string? Password);

public record StaffView(Guid Id, string Username, string DisplayName, string Role, bool IsActive, DateTimeOffset CreatedUtc, DateTimeOffset? LastLoginUtc)
{
    public static StaffView From(StaffUserEntity s) => new(s.Id, s.Username, s.DisplayName, s.Role, s.IsActive, s.CreatedUtc, s.LastLoginUtc);
}
