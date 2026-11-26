using System.Security.Claims;
using Cms.Server.Auth;
using Cms.Server.Data;
using Cms.Server.Demo;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>Staff login, logout, first-start setup and own password change.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly SetupState _setup;
    private readonly LoginThrottle _throttle;
    private readonly DemoOptions _demo;

    public AuthController(CmsDbContext db, SetupState setup, LoginThrottle throttle, DemoOptions demo)
    {
        _db = db;
        _setup = setup;
        _throttle = throttle;
        _demo = demo;
    }

    /// <summary>Public: what the login page should show (setup screen, demo accounts).</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthStatusView>> Status()
    {
        var setupRequired = _setup.IsOpen && !await _db.StaffUsers.AnyAsync();
        return Ok(new AuthStatusView(
            setupRequired,
            _demo.Enabled,
            _demo.Enabled ? DemoOptions.StaffAccounts.Select(a => new DemoAccountView(a.Username, DemoOptions.StaffPassword, a.Role)).ToList() : null));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthConstants.AuthRateLimitPolicy)]
    public async Task<ActionResult<StaffMeView>> Login([FromBody] StaffLoginRequest req)
    {
        var username = StaffNames.Normalize(req.Username);
        var key = LoginThrottle.Key("staff", username, HttpContext.ClientIp());
        var now = DateTimeOffset.UtcNow;
        if (_throttle.IsLockedOut(key, now))
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed attempts. Try again in 15 minutes." });

        var staff = username.Length == 0 ? null : await _db.StaffUsers.FirstOrDefaultAsync(s => s.Username == username);
        var check = PasswordCheck.Failed;
        if (staff is { IsActive: true })
            check = Passwords.Verify(staff.PasswordHash, req.Password);
        else
            Passwords.VerifyDummy(req.Password);

        if (check == PasswordCheck.Failed)
        {
            _throttle.RecordFailure(key, now);
            return Unauthorized(new { error = "Wrong user name or password." });
        }

        _throttle.Reset(key);
        if (check == PasswordCheck.SuccessRehashNeeded)
            staff!.PasswordHash = Passwords.Hash(req.Password!);
        staff!.LastLoginUtc = now;
        await _db.SaveChangesAsync();

        await HttpContext.SignInStaffAsync(staff);
        return Ok(StaffMeView.From(staff));
    }

    [HttpPost("logout")]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = Policies.AnyStaff)]
    public ActionResult<StaffMeView> Me()
    {
        return Ok(new StaffMeView(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            User.Identity!.Name!,
            User.FindFirstValue(AuthConstants.DisplayNameClaim) ?? User.Identity.Name!,
            User.FindFirstValue(ClaimTypes.Role)!));
    }

    /// <summary>
    /// One-time setup: creates the first Owner account. Works only while no staff account exists,
    /// and only with the setup code from &lt;data directory&gt;/setup-code.txt on the server.
    /// </summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthConstants.AuthRateLimitPolicy)]
    public async Task<ActionResult<StaffMeView>> Setup([FromBody] SetupRequest req)
    {
        if (!_setup.IsOpen || await _db.StaffUsers.AnyAsync())
            return Conflict(new { error = "Setup is already complete. Sign in with a staff account." });

        var key = LoginThrottle.Key("setup", null, HttpContext.ClientIp());
        var now = DateTimeOffset.UtcNow;
        if (_throttle.IsLockedOut(key, now))
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed attempts. Try again in 15 minutes." });
        if (!_setup.Check(req.SetupCode))
        {
            _throttle.RecordFailure(key, now);
            return Unauthorized(new { error = "Wrong setup code. It is in setup-code.txt in the server data directory." });
        }

        var username = StaffNames.Normalize(req.Username);
        if (!StaffNames.IsValid(username))
            return BadRequest(new { error = "User name: 2-64 characters, a-z, 0-9, '.', '_' or '-'." });
        var passwordError = Passwords.ValidateStaffPassword(req.Password);
        if (passwordError != null) return BadRequest(new { error = passwordError });

        StaffUserEntity owner;
        try
        {
            owner = await StaffBootstrapper.CreateOwnerAsync(_db, username, req.DisplayName ?? username, req.Password!);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "Setup is already complete. Sign in with a staff account." });
        }
        _setup.Close();
        _throttle.Reset(key);

        await HttpContext.SignInStaffAsync(owner);
        return Ok(StaffMeView.From(owner));
    }

    /// <summary>Change the own password. Ends the other logins of this account.</summary>
    [HttpPost("password")]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var staff = await _db.StaffUsers.FindAsync(id);
        if (staff == null) return Unauthorized();
        if (Passwords.Verify(staff.PasswordHash, req.CurrentPassword) == PasswordCheck.Failed)
            return BadRequest(new { error = "The current password is wrong." });
        var error = Passwords.ValidateStaffPassword(req.NewPassword);
        if (error != null) return BadRequest(new { error });

        staff.PasswordHash = Passwords.Hash(req.NewPassword!);
        staff.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();
        await HttpContext.SignInStaffAsync(staff);
        return NoContent();
    }
}

public record StaffLoginRequest(string? Username, string? Password);
public record SetupRequest(string? SetupCode, string? Username, string? DisplayName, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public record DemoAccountView(string Username, string Password, string Role);
public record AuthStatusView(bool SetupRequired, bool DemoMode, IReadOnlyList<DemoAccountView>? DemoAccounts);

public record StaffMeView(Guid Id, string Username, string DisplayName, string Role)
{
    public static StaffMeView From(StaffUserEntity s) => new(s.Id, s.Username, s.DisplayName, s.Role);
}
