using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Branding;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Cms.Server.Controllers;

/// <summary>
/// Club theme, accent colour and logo (themes phase). Everybody may read them (the login screen, the public booking
/// page /book and the lock screens need them); only the Owner changes them, and every change is audited. The logo is
/// an untrusted file: it is judged by its bytes (Branding/LogoImage) and served with the detected type and nosniff.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Policy = Policies.OwnerOnly)]
public class BrandingController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public BrandingController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    /// <summary>The club theme, the accent (null: the accent of the theme) and the SHA-256 of the logo (null: no logo).</summary>
    [HttpGet("public/theme")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthConstants.BookingReadPolicy)]
    public async Task<ActionResult<ThemeView>> Get()
    {
        Response.Headers.CacheControl = "no-cache";
        return Ok(await ClubBranding.LoadAsync(_db));
    }

    /// <summary>The logo bytes with the type found in them; 404 when the club has no logo.</summary>
    [HttpGet("public/theme/logo")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthConstants.BookingReadPolicy)]
    public async Task<IActionResult> Logo()
    {
        var logo = await _db.ClubLogos.AsNoTracking().FirstOrDefaultAsync();
        if (logo == null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "public, max-age=300";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return File(logo.Data, logo.ContentType, lastModified: null, entityTag: new EntityTagHeaderValue($"\"{logo.Sha256}\""));
    }

    /// <summary>
    /// Theme and accent together. The accent is "#RRGGBB" or empty (the accent of the theme); it must reach a contrast of
    /// 3:1 against the surface of BOTH modes of the theme, because every staff member may switch to the other mode.
    /// </summary>
    [HttpPut("settings/theme")]
    public async Task<ActionResult<ThemeView>> SetTheme([FromBody] SetThemeRequest req)
    {
        var theme = ClubThemes.Find(req.Theme);
        if (theme == null)
            return BadRequest(new { error = "Theme must be cyber, pro-dark, light or arcade.", code = "bad_theme" });
        string? accent = null;
        if (!string.IsNullOrWhiteSpace(req.Accent))
        {
            if (!ColorContrast.TryParseHex(req.Accent, out var hex))
                return BadRequest(new { error = "The accent must be a colour written as #RRGGBB.", code = "bad_accent" });
            var worst = ClubThemes.Modes.Select(m => (Mode: m, Ratio: ColorContrast.Ratio(hex, ClubThemes.SurfaceHex(theme, m)))).MinBy(x => x.Ratio);
            if (worst.Ratio < ClubThemes.MinAccentContrast)
                return BadRequest(new
                {
                    error = $"The accent {hex} has a contrast of {worst.Ratio:0.00}:1 on the {worst.Mode} surface of {theme}; at least {ClubThemes.MinAccentContrast:0.0}:1 is needed in both modes.",
                    code = "accent_low_contrast", mode = worst.Mode, ratio = Math.Round(worst.Ratio, 2), minimum = ClubThemes.MinAccentContrast
                });
            accent = hex;
        }
        var before = await ClubBranding.SaveThemeAsync(_db, theme, accent, User.Identity?.Name, _clock.UtcNow);
        if (before.Theme != theme || before.Accent != accent)
            await _audit.WriteAsync(AuditActions.ThemeChanged, "setting", ClubSettingKeys.Theme,
                $"Club theme changed: {before.Theme} -> {theme}; accent: {before.Accent ?? "theme default"} -> {accent ?? "theme default"}",
                new { before = before.Theme, after = theme, accentBefore = before.Accent, accentAfter = accent });
        return Ok(await ClubBranding.LoadAsync(_db));
    }

    /// <summary>
    /// Upload of the logo as the raw request body (any Content-Type; the type is read from the bytes). PNG or JPEG, at
    /// most 512 KB and 1024 x 1024 px. The audit row has the type, the size and the first 12 hex characters of the SHA-256.
    /// </summary>
    [HttpPut("settings/logo")]
    [RequestSizeLimit(2 * LogoImage.MaxBytes)]
    public async Task<ActionResult<ThemeView>> SetLogo()
    {
        if (Request.ContentLength > LogoImage.MaxBytes) return LogoError("size");
        // A body without a length (chunked) is read up to the limit only
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > LogoImage.MaxBytes) return LogoError("size");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var check = LogoImage.Inspect(bytes);
        if (!check.Ok) return LogoError(check.Error ?? "type");
        var sha = await ClubBranding.SaveLogoAsync(_db, bytes, check, User.Identity?.Name, _clock.UtcNow);
        await _audit.WriteAsync(AuditActions.LogoChanged, "setting", "club.logo",
            $"Club logo changed: {check.ContentType}, {check.Width} x {check.Height} px, {bytes.Length} bytes, SHA-256 {sha[..12]}",
            new { type = check.ContentType, width = check.Width, height = check.Height, bytes = bytes.Length, sha256 = sha[..12] });
        return Ok(await ClubBranding.LoadAsync(_db));
    }

    [HttpDelete("settings/logo")]
    public async Task<ActionResult<ThemeView>> RemoveLogo()
    {
        if (await ClubBranding.RemoveLogoAsync(_db))
            await _audit.WriteAsync(AuditActions.LogoRemoved, "setting", "club.logo", "Club logo removed: the lock screens show the club name");
        return Ok(await ClubBranding.LoadAsync(_db));
    }

    private BadRequestObjectResult LogoError(string error) => BadRequest(error switch
    {
        "size" => new { error = "The logo must be at most 512 KB.", code = "logo_size" },
        "dimensions" => new { error = $"The logo must be at most {LogoImage.MaxSide} x {LogoImage.MaxSide} pixels.", code = "logo_dimensions" },
        _ => new { error = "The logo must be a PNG or JPEG image (SVG, GIF and WebP are not accepted).", code = "logo_type" }
    });
}

public record SetThemeRequest(string? Theme, string? Accent);
