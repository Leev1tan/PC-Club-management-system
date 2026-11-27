using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>
/// Club currency (spec area 12): UAH (default), EUR, USD or PLN, a club setting that the owner changes.
/// It changes only how money is shown: amounts stay decimals in the database and are never converted.
/// Texts that the server stores (ledger descriptions, audit summaries) take the symbol that is set when
/// they are written; older rows keep their symbol.
/// </summary>
public sealed record ClubCurrency(string Code, string Symbol, string NameUk, string NameEn)
{
    public static readonly ClubCurrency Uah = new("UAH", "₴", "Гривня", "Ukrainian hryvnia");
    public static readonly ClubCurrency Eur = new("EUR", "€", "Євро", "Euro");
    public static readonly ClubCurrency Usd = new("USD", "$", "Долар США", "US dollar");
    public static readonly ClubCurrency Pln = new("PLN", "zł", "Злотий", "Polish złoty");

    public static readonly IReadOnlyList<ClubCurrency> All = new[] { Uah, Eur, Usd, Pln };
    public static ClubCurrency Default => Uah;

    /// <summary>The currency with this ISO code (any case), or null.</summary>
    public static ClubCurrency? Find(string? code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Amount and symbol, as in the stored texts: "150.00 ₴", "12.50 €".</summary>
    public string Text(decimal amount) => $"{MoneyText.Of(amount)} {Symbol}";

    /// <summary>The club setting club.currency; UAH when it is missing or unknown.</summary>
    public static async Task<ClubCurrency> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var code = await db.ClubSettings.AsNoTracking().Where(s => s.Key == ClubSettingKeys.Currency).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return Find(code) ?? Default;
    }

    /// <summary>Stores the currency (the caller audits it). Returns the currency that was set before.</summary>
    public static async Task<ClubCurrency> SaveAsync(CmsDbContext db, ClubCurrency currency, string? by, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = await LoadAsync(db, ct);
        var setting = await db.ClubSettings.FirstOrDefaultAsync(s => s.Key == ClubSettingKeys.Currency, ct);
        if (setting == null)
        {
            setting = new ClubSettingEntity { Key = ClubSettingKeys.Currency };
            db.ClubSettings.Add(setting);
        }
        setting.Value = currency.Code;
        setting.UpdatedUtc = now;
        setting.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        db.SetCurrency(currency);
        return before;
    }
}

public record CurrencyView(string Code, string Symbol, string NameUk, string NameEn)
{
    public static CurrencyView From(ClubCurrency c) => new(c.Code, c.Symbol, c.NameUk, c.NameEn);
}
