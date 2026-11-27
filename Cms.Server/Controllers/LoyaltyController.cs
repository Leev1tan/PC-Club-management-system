using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Loyalty tiers (spec area 3): the front desk reads them (the player card shows the tier); only the Owner changes them
/// (audited). An empty list of tiers turns loyalty off.
/// </summary>
[ApiController]
[Route("api/loyalty")]
[Authorize(Policy = Policies.FrontDesk)]
public class LoyaltyController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public LoyaltyController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    [HttpGet]
    public async Task<ActionResult<LoyaltyView>> Get()
    {
        var settings = await Loyalty.LoadAsync(_db);
        var counts = new Dictionary<string, int>();
        if (settings.Enabled)
        {
            var spend = await Loyalty.SpendByPlayerAsync(_db, _clock.UtcNow, settings.PeriodDays);
            var players = await _db.Users.Select(u => u.Id).ToListAsync();
            foreach (var id in players)
                if (Loyalty.TierFor(settings, spend.GetValueOrDefault(id)).Tier is { } tier)
                    counts[tier.Name] = counts.GetValueOrDefault(tier.Name) + 1;
        }
        return Ok(new LoyaltyView(settings.PeriodDays, settings.Tiers, settings.Tiers.Select(t => counts.GetValueOrDefault(t.Name)).ToList()));
    }

    [HttpPut]
    [Authorize(Policy = Policies.OwnerOnly)]
    public async Task<ActionResult<LoyaltyView>> Put([FromBody] LoyaltySettings req)
    {
        var error = Loyalty.Validate(req);
        if (error != null) return BadRequest(new { error, code = "bad_loyalty_settings" });
        var before = await Loyalty.LoadAsync(_db);
        var after = Loyalty.Normalize(req);
        await Loyalty.SaveAsync(_db, after, User.Identity?.Name, _clock.UtcNow);
        var cur = await _db.CurrencySymbolAsync();
        string Text(LoyaltySettings s) => s.Enabled
            ? $"{s.PeriodDays} days: " + string.Join(", ", s.Tiers.Select(t => $"{t.Name} from {MoneyText.Of(t.MinSpend)} {cur} (+{t.TopUpBonusPercent}% bonus, -{t.PcDiscountPercent}% PC time)"))
            : "off";
        await _audit.WriteAsync(AuditActions.LoyaltySettingsChanged, "settings", ClubSettingKeys.Loyalty,
            $"Loyalty tiers: {Text(after)}", new { before, after });
        return await Get();
    }
}

/// <summary>Players: how many players are in each tier now (same order as Tiers).</summary>
public record LoyaltyView(int PeriodDays, IReadOnlyList<LoyaltyTier> Tiers, IReadOnlyList<int> Players);
