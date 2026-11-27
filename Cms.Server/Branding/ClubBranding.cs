using System.Security.Cryptography;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Branding;

/// <summary>What every page and every PC gets: the club theme, the owner accent (or null) and the SHA-256 of the logo (or null).</summary>
public sealed record ThemeView(string Theme, string? Accent, string? LogoSha256);

/// <summary>
/// Club theme, accent colour and logo (themes phase). The Owner changes them; the admin UI, the public booking page
/// and the lock screens follow. A stored value that this version does not know reads as the default (theme cyber,
/// no accent), so a manual database edit or a downgrade never breaks a page.
/// </summary>
public static class ClubBranding
{
    public static async Task<ThemeView> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var rows = await db.ClubSettings.AsNoTracking()
            .Where(s => s.Key == ClubSettingKeys.Theme || s.Key == ClubSettingKeys.Accent)
            .Select(s => new { s.Key, s.Value }).ToListAsync(ct);
        var theme = ClubThemes.Normalize(rows.FirstOrDefault(r => r.Key == ClubSettingKeys.Theme)?.Value);
        var accent = ColorContrast.TryParseHex(rows.FirstOrDefault(r => r.Key == ClubSettingKeys.Accent)?.Value, out var a) ? a : null;
        var logo = await db.ClubLogos.AsNoTracking().Select(l => l.Sha256).FirstOrDefaultAsync(ct);
        return new ThemeView(theme, accent, logo);
    }

    /// <summary>Stores the theme and the accent (both already checked; the caller audits). Returns the values before.</summary>
    public static async Task<ThemeView> SaveThemeAsync(CmsDbContext db, string theme, string? accent, string? by, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = await LoadAsync(db, ct);
        foreach (var (key, value) in new[] { (ClubSettingKeys.Theme, theme), (ClubSettingKeys.Accent, accent ?? string.Empty) })
        {
            var setting = await db.ClubSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (setting == null)
            {
                setting = new ClubSettingEntity { Key = key };
                db.ClubSettings.Add(setting);
            }
            setting.Value = value;
            setting.UpdatedUtc = now;
            setting.UpdatedBy = by;
        }
        await db.SaveChangesAsync(ct);
        return before;
    }

    /// <summary>Stores a logo that <see cref="LogoImage.Inspect"/> accepted; replaces the old one. Returns the SHA-256 (hex).</summary>
    public static async Task<string> SaveLogoAsync(CmsDbContext db, byte[] bytes, LogoCheck check, string? by, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!check.Ok || check.ContentType == null) throw new ArgumentException("The logo was not accepted by LogoImage.Inspect.", nameof(check));
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var logo = await db.ClubLogos.FirstOrDefaultAsync(l => l.Id == ClubLogoEntity.SingleId, ct);
        if (logo == null)
        {
            logo = new ClubLogoEntity();
            db.ClubLogos.Add(logo);
        }
        logo.Data = bytes;
        logo.ContentType = check.ContentType;
        logo.Sha256 = sha;
        logo.Width = check.Width;
        logo.Height = check.Height;
        logo.UpdatedUtc = now;
        logo.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        return sha;
    }

    /// <summary>Removes the logo; false when there was none.</summary>
    public static async Task<bool> RemoveLogoAsync(CmsDbContext db, CancellationToken ct = default) =>
        await db.ClubLogos.Where(l => l.Id == ClubLogoEntity.SingleId).ExecuteDeleteAsync(ct) > 0;
}
