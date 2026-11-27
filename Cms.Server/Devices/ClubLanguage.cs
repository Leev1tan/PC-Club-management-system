using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Devices;

/// <summary>
/// Language of the lock screens (spec area 12): uk (default) or en, a club setting that the owner changes
/// (club.language). The admin UI keeps its own language per browser; this setting is for the club PCs.
/// </summary>
public static class ClubLanguage
{
    public static async Task<string> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var code = await db.ClubSettings.AsNoTracking().Where(s => s.Key == ClubSettingKeys.Language).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return ClientLanguages.Normalize(code);
    }

    /// <summary>Stores the language (the caller audits it). Returns the language that was set before.</summary>
    public static async Task<string> SaveAsync(CmsDbContext db, string language, string? by, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = await LoadAsync(db, ct);
        var setting = await db.ClubSettings.FirstOrDefaultAsync(s => s.Key == ClubSettingKeys.Language, ct);
        if (setting == null)
        {
            setting = new ClubSettingEntity { Key = ClubSettingKeys.Language };
            db.ClubSettings.Add(setting);
        }
        setting.Value = language;
        setting.UpdatedUtc = now;
        setting.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        return before;
    }

    /// <summary>What an approved PC gets with its heartbeat: lock screen language, club currency, theme, accent and logo hash.</summary>
    public static async Task<ClientSettings> ClientSettingsAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var branding = await Cms.Server.Branding.ClubBranding.LoadAsync(db, ct);
        return new ClientSettings
        {
            Language = await LoadAsync(db, ct),
            Currency = (await db.CurrencyAsync(ct)).Code,
            Theme = branding.Theme,
            Accent = branding.Accent,
            LogoSha256 = branding.LogoSha256
        };
    }
}
