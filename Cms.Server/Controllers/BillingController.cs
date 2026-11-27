using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cms.Server.Controllers;

/// <summary>Billing settings: club time zone (read-only, from configuration) and cashback on top-ups.</summary>
[ApiController]
[Route("api/billing")]
[Authorize(Policy = Policies.FrontDesk)]
public class BillingController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public BillingController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    [HttpGet("settings")]
    public async Task<ActionResult<BillingSettingsView>> Get()
    {
        var now = _clock.UtcNow;
        var currency = await _db.CurrencyAsync();
        return Ok(new BillingSettingsView(_clock.Zone.Id, _clock.ToLocal(now), currency.Symbol, await BonusRules.LoadAsync(_db)) { CurrencyCode = currency.Code });
    }

    /// <summary>Cashback tiers (Owner, Manager). An empty list turns cashback off.</summary>
    [HttpPut("settings")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<BillingSettingsView>> Put([FromBody] BillingSettingsRequest req)
    {
        var error = BonusRules.Validate(req.BonusTopUpRules);
        if (error != null) return BadRequest(new { error, code = "bad_bonus_rules" });
        var before = await BonusRules.LoadAsync(_db);
        var after = BonusRules.Normalize(req.BonusTopUpRules!);
        await BonusRules.SaveAsync(_db, after, User.Identity?.Name, _clock.UtcNow);
        var cur = await _db.CurrencySymbolAsync();
        string Text(IEnumerable<BonusTier> tiers) => string.Join(", ", tiers.Select(t => $"from {t.MinAmount} {cur}: {t.Percent}%"));
        await _audit.WriteAsync(AuditActions.BillingSettingsChanged, "settings", ClubSettingKeys.BonusTopUpRules,
            $"Cashback on top-ups: {(after.Count == 0 ? "off" : Text(after))}", new { before, after });
        return await Get();
    }
}

public record BillingSettingsRequest(IReadOnlyList<BonusTier>? BonusTopUpRules);
/// <summary>Currency: the symbol of the club currency; CurrencyCode: its ISO code.</summary>
public record BillingSettingsView(string TimeZone, DateTimeOffset ClubTimeNow, string Currency, IReadOnlyList<BonusTier> BonusTopUpRules)
{
    public string CurrencyCode { get; init; } = ClubCurrency.Default.Code;
}
