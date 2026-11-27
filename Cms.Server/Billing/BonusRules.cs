using System.Text.Json;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>Cashback tier: a top-up of at least MinAmount gets Percent of it as bonus.</summary>
public sealed record BonusTier(decimal MinAmount, decimal Percent);

/// <summary>
/// Cashback on top-ups (club setting billing.bonus_topup_rules). The tier with the highest
/// MinAmount that the top-up reaches applies; no tiers = no cashback. Only real money paid at the
/// desk (top-up in cash or by card) earns bonus; corrections and refunds never do. A refund takes
/// back the cashback of the refunded amount (at most the bonus the player still has).
/// </summary>
public static class BonusRules
{
    public const int MaxTiers = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<BonusTier>> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var value = await db.ClubSettings.AsNoTracking()
            .Where(s => s.Key == ClubSettingKeys.BonusTopUpRules).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<BonusTier>();
        try
        {
            return Normalize(JsonSerializer.Deserialize<List<BonusTier>>(value, Json) ?? new());
        }
        catch (JsonException)
        {
            return Array.Empty<BonusTier>();
        }
    }

    /// <summary>Error text for invalid tiers, or null.</summary>
    public static string? Validate(IReadOnlyList<BonusTier>? tiers)
    {
        if (tiers == null) return "Tiers are required (an empty list turns cashback off).";
        if (tiers.Count > MaxTiers) return $"At most {MaxTiers} tiers.";
        foreach (var t in tiers)
        {
            if (t.MinAmount is < 0 or > 100_000) return "Minimum top-up must be between 0 and 100000.";
            if (t.Percent is < 0 or > 100) return "Percent must be between 0 and 100.";
        }
        if (tiers.GroupBy(t => t.MinAmount).Any(g => g.Count() > 1)) return "Two tiers have the same minimum top-up.";
        return null;
    }

    public static IReadOnlyList<BonusTier> Normalize(IEnumerable<BonusTier> tiers) =>
        tiers.Select(t => new BonusTier(TariffCalculator.Round(t.MinAmount), Math.Round(t.Percent, 2)))
            .OrderBy(t => t.MinAmount).ToList();

    public static async Task SaveAsync(CmsDbContext db, IReadOnlyList<BonusTier> tiers, string? actor, DateTimeOffset now, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(Normalize(tiers), Json);
        var setting = await db.ClubSettings.FindAsync(new object[] { ClubSettingKeys.BonusTopUpRules }, ct);
        if (setting == null)
        {
            db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.BonusTopUpRules, Value = json, UpdatedUtc = now, UpdatedBy = actor });
        }
        else
        {
            setting.Value = json;
            setting.UpdatedUtc = now;
            setting.UpdatedBy = actor;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Tier that applies to a top-up of this amount, or null.</summary>
    public static BonusTier? TierFor(IReadOnlyList<BonusTier> tiers, decimal amount) =>
        tiers.Where(t => t.Percent > 0 && amount >= t.MinAmount).OrderByDescending(t => t.MinAmount).FirstOrDefault();

    public static decimal CashbackFor(IReadOnlyList<BonusTier> tiers, decimal amount)
    {
        var tier = TierFor(tiers, amount);
        return tier == null || amount <= 0 ? 0m : TariffCalculator.Round(amount * tier.Percent / 100m);
    }
}
