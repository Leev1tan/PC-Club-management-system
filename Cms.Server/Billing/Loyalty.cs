using System.Text.Json;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>A loyalty tier: from MinSpend of real money spent in the period, a bonus on top-ups and a discount on PC time.</summary>
public sealed record LoyaltyTier(string Name, decimal MinSpend, decimal TopUpBonusPercent, decimal PcDiscountPercent);

/// <summary>The loyalty tiers of the club (club setting loyalty.tiers); no tiers = loyalty off.</summary>
public sealed record LoyaltySettings(int PeriodDays, IReadOnlyList<LoyaltyTier> Tiers)
{
    public static readonly LoyaltySettings Off = new(90, Array.Empty<LoyaltyTier>());
    public bool Enabled => Tiers.Count > 0;
}

/// <summary>The tier of a player now: the spend of the period, the tier (null below the lowest one) and the next tier.</summary>
public sealed record PlayerTier(decimal Spend, int PeriodDays, LoyaltyTier? Tier, LoyaltyTier? Next)
{
    /// <summary>Real money still to spend in the period for the next tier (0 when there is none).</summary>
    public decimal ToNext => Next == null ? 0m : Math.Max(0m, Next.MinSpend - Spend);
    public decimal DiscountPercent => Tier?.PcDiscountPercent ?? 0m;
    public decimal BonusPercent => Tier?.TopUpBonusPercent ?? 0m;
}

/// <summary>
/// Loyalty tiers (spec area 3): a player is in the tier with the highest MinSpend that the real money spent at the club in
/// the last PeriodDays reaches. Spent = PC time, packages and bar paid from the real balance or at the desk, debt paid,
/// kept deposits and entry fees, minus bar refunds; never top-ups, bonus or unpaid debt (Spend). The tier gives
///  - a bonus on top-ups: a bonus_accrual ledger row next to the cashback of the amount (BonusRules), taken back with
///    the cashback when the top-up is refunded;
///  - a discount on PC time (tariff time, packages, extensions, post-paid time; not event prices): the session keeps the
///    percent of its start (SessionEntity.DiscountPercent) and its ledger rows hold the discounted price, so balances and
///    reports reconcile exactly as before (the description of the row names the discount).
/// Guests have no tier. Settings: Owner only, audited.
/// </summary>
public static class Loyalty
{
    public const int MaxTiers = 6;
    public const int NameLength = 20;
    public const decimal MaxPercent = 50m;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ledger types that count as money spent at the club.</summary>
    public static readonly string[] SpendTypes =
    {
        LedgerTypes.SessionCharge, LedgerTypes.PackagePurchase, LedgerTypes.BarSale, LedgerTypes.BarRefund, LedgerTypes.DebtPayment,
        LedgerTypes.DepositKept, LedgerTypes.EventFeeKept
    };

    /// <summary>Real money of one ledger row spent at the club (negative for a bar refund; 0 for bonus and unpaid debt).</summary>
    public static decimal Spend(string type, string method, decimal amount, decimal balanceDelta, decimal deskAmount) => type switch
    {
        LedgerTypes.DepositKept or LedgerTypes.EventFeeKept => amount,                  // held from the real balance before
        LedgerTypes.DebtPayment => amount,
        LedgerTypes.SessionCharge or LedgerTypes.PackagePurchase or LedgerTypes.BarSale or LedgerTypes.BarRefund =>
            method == PaymentMethods.Balance ? -balanceDelta : PaymentMethods.IsDesk(method) ? deskAmount : 0m,
        _ => 0m
    };

    public static async Task<LoyaltySettings> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var value = await db.ClubSettings.AsNoTracking().Where(s => s.Key == ClubSettingKeys.Loyalty).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(value)) return LoyaltySettings.Off;
        try
        {
            var s = JsonSerializer.Deserialize<LoyaltySettings>(value, Json);
            return s == null || Validate(s) != null ? LoyaltySettings.Off : Normalize(s);
        }
        catch (JsonException)
        {
            return LoyaltySettings.Off;
        }
    }

    /// <summary>Error text for invalid settings, or null.</summary>
    public static string? Validate(LoyaltySettings? s)
    {
        if (s?.Tiers == null) return "Tiers are required (an empty list turns loyalty off).";
        if (s.PeriodDays is < 7 or > 365) return "The period must be 7 to 365 days.";
        if (s.Tiers.Count > MaxTiers) return $"At most {MaxTiers} tiers.";
        foreach (var t in s.Tiers)
        {
            var name = t.Name?.Trim() ?? "";
            if (name.Length is 0 or > NameLength || name.Any(char.IsControl)) return $"A tier name has 1 to {NameLength} characters.";
            if (t.MinSpend is < 0 or > 1_000_000) return "The spend of a tier must be 0 to 1000000.";
            if (t.TopUpBonusPercent is < 0 or > MaxPercent || t.PcDiscountPercent is < 0 or > MaxPercent) return $"Percents must be 0 to {MaxPercent}.";
        }
        if (s.Tiers.GroupBy(t => t.MinSpend).Any(g => g.Count() > 1)) return "Two tiers have the same spend.";
        if (s.Tiers.GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return "Two tiers have the same name.";
        return null;
    }

    public static LoyaltySettings Normalize(LoyaltySettings s) => new(s.PeriodDays,
        s.Tiers.Select(t => new LoyaltyTier(t.Name.Trim(), TariffCalculator.Round(t.MinSpend), Math.Round(t.TopUpBonusPercent, 2), Math.Round(t.PcDiscountPercent, 2)))
            .OrderBy(t => t.MinSpend).ToList());

    public static async Task SaveAsync(CmsDbContext db, LoyaltySettings settings, string? actor, DateTimeOffset now, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(Normalize(settings), Json);
        var row = await db.ClubSettings.FindAsync(new object[] { ClubSettingKeys.Loyalty }, ct);
        if (row == null) db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.Loyalty, Value = json, UpdatedUtc = now, UpdatedBy = actor });
        else
        {
            row.Value = json;
            row.UpdatedUtc = now;
            row.UpdatedBy = actor;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The tier of a spend: the one with the highest MinSpend that the spend reaches (null below the lowest).</summary>
    public static PlayerTier TierFor(LoyaltySettings s, decimal spend)
    {
        var tier = s.Tiers.Where(t => spend >= t.MinSpend).OrderByDescending(t => t.MinSpend).FirstOrDefault();
        var next = s.Tiers.Where(t => t.MinSpend > spend).OrderBy(t => t.MinSpend).FirstOrDefault();
        return new PlayerTier(spend, s.PeriodDays, tier, next);
    }

    /// <summary>Real money spent per player in [now - PeriodDays, now] (players without rows are missing).</summary>
    public static async Task<Dictionary<Guid, decimal>> SpendByPlayerAsync(CmsDbContext db, DateTimeOffset now, int periodDays, Guid? userId = null,
        CancellationToken ct = default)
    {
        var from = now.AddDays(-periodDays);
        var query = db.Ledger.AsNoTracking().Where(l => l.UserId != null && SpendTypes.Contains(l.Type) && (userId == null || l.UserId == userId));
        // SQLite cannot compare DateTimeOffset in SQL: the rows are filtered in memory there
        if (db.Database.IsNpgsql()) query = query.Where(l => l.CreatedUtc >= from && l.CreatedUtc <= now);
        var rows = await query.Select(l => new { l.UserId, l.CreatedUtc, l.Type, l.Method, l.Amount, l.BalanceDelta, l.DeskAmount }).ToListAsync(ct);
        return rows.Where(r => r.CreatedUtc >= from && r.CreatedUtc <= now)
            .GroupBy(r => r.UserId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(r => Spend(r.Type, r.Method, r.Amount, r.BalanceDelta, r.DeskAmount)));
    }

    /// <summary>The tier of one player now; null when loyalty is off.</summary>
    public static async Task<PlayerTier?> OfPlayerAsync(CmsDbContext db, Guid userId, DateTimeOffset now, LoyaltySettings? settings = null, CancellationToken ct = default)
    {
        settings ??= await LoadAsync(db, ct);
        if (!settings.Enabled) return null;
        var spend = (await SpendByPlayerAsync(db, now, settings.PeriodDays, userId, ct)).GetValueOrDefault(userId);
        return TierFor(settings, spend);
    }

    /// <summary>The discount of a price (rounded to cents); the price minus it is what the player pays.</summary>
    public static decimal Discount(decimal price, decimal percent) =>
        percent <= 0 || price <= 0 ? 0m : TariffCalculator.Round(price * percent / 100m);

    /// <summary>The price after the discount of the percent.</summary>
    public static decimal Apply(decimal price, decimal percent) => price - Discount(price, percent);

    /// <summary>The tier bonus of a top-up (rounded to cents).</summary>
    public static decimal BonusFor(decimal amount, decimal percent) =>
        percent <= 0 || amount <= 0 ? 0m : TariffCalculator.Round(amount * percent / 100m);
}
