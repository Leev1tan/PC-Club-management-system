using System.Security.Claims;
using Cms.Server.Auth;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cms.Server.Controllers;

/// <summary>
/// Club settings (Owner only). The staff / emergency PIN of the lock screen is stored only as a
/// PBKDF2 hash; agents receive the hash with their heartbeat and the launcher verifies it offline.
/// </summary>
[ApiController]
[Route("api/settings")]
[Authorize(Policy = Policies.OwnerOnly)]
public class SettingsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly DeviceEnrollmentOptions _enrollment;
    private readonly AuditLogger _audit;

    public SettingsController(CmsDbContext db, DeviceEnrollmentOptions enrollment, AuditLogger audit)
    {
        _db = db;
        _enrollment = enrollment;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<SettingsView>> Get()
    {
        var pin = await _db.ClubSettings.FindAsync(KioskSettings.StaffPinHashKey);
        return Ok(new SettingsView(
            pin?.Value != null,
            pin?.Value != null ? pin.UpdatedUtc : null,
            _enrollment.TokenConfigured,
            _enrollment.AllowPendingRegistration));
    }

    [HttpPut("staff-pin")]
    public async Task<IActionResult> SetStaffPin([FromBody] SetStaffPinRequest req)
    {
        var error = StaffPinHash.Validate(req.Pin);
        if (error != null) return BadRequest(new { error });

        var setting = await _db.ClubSettings.FindAsync(KioskSettings.StaffPinHashKey);
        if (setting == null)
        {
            setting = new ClubSettingEntity { Key = KioskSettings.StaffPinHashKey };
            _db.ClubSettings.Add(setting);
        }
        setting.Value = StaffPinHash.Create(req.Pin!);
        setting.UpdatedUtc = DateTimeOffset.UtcNow;
        setting.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.StaffPinChanged, "setting", KioskSettings.StaffPinHashKey, "Staff PIN of the lock screens changed");
        return NoContent();
    }

    /// <summary>Remove the PIN: the lock screens then have no emergency unlock.</summary>
    [HttpDelete("staff-pin")]
    public async Task<IActionResult> DeleteStaffPin()
    {
        var setting = await _db.ClubSettings.FindAsync(KioskSettings.StaffPinHashKey);
        if (setting != null)
        {
            _db.ClubSettings.Remove(setting);
            await _db.SaveChangesAsync();
            await _audit.WriteAsync(AuditActions.StaffPinRemoved, "setting", KioskSettings.StaffPinHashKey, "Staff PIN removed: no emergency unlock");
        }
        return NoContent();
    }
}

public static class KioskSettings
{
    public const string StaffPinHashKey = "kiosk.staff_pin_hash";

    public static async Task<string?> GetStaffPinHashAsync(CmsDbContext db) =>
        (await db.ClubSettings.FindAsync(StaffPinHashKey))?.Value;
}

public record SetStaffPinRequest(string? Pin);
public record SettingsView(bool StaffPinConfigured, DateTimeOffset? StaffPinUpdatedUtc, bool EnrollmentTokenConfigured, bool AllowPendingRegistration);
