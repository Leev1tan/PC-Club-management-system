using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Tariffs per zone with time-of-day / weekday rules (club local time), a rounding step for
/// post-paid time and a minimum charge. Front desk reads them; Owner and Manager change them.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.FrontDesk)]
public class TariffsController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public TariffsController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TariffView>>> List()
    {
        var tariffs = (await _db.Tariffs.AsNoTracking().ToListAsync()).OrderBy(t => t.ZoneId.HasValue ? 0 : 1).ThenBy(t => t.Name).ToList();
        var rules = await _db.TariffRules.AsNoTracking().ToListAsync();
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name);
        return Ok(tariffs.Select(t => View(t, rules, zones)));
    }

    [HttpPost]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<TariffView>> Create([FromBody] CreateTariffRequest req)
    {
        var (rules, error) = Validate(req);
        if (error != null) return BadRequest(new { error, code = "bad_tariff" });
        var tariff = new TariffEntity { Id = Guid.NewGuid() };
        Apply(tariff, req);
        _db.Tariffs.Add(tariff);
        AddRules(tariff.Id, rules ?? new());
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.TariffCreated, "tariff", tariff.Id,
            $"Tariff '{tariff.Name}' created: {tariff.PricePerHour} ₴/h, {rules?.Count ?? 0} rule(s)",
            new { tariff.Name, tariff.PricePerHour, tariff.IsDefault, tariff.ZoneId, tariff.BillingStepMinutes, tariff.MinimumCharge, rules = RulesText(rules) });
        return Ok(await ViewAsync(tariff.Id));
    }

    /// <summary>Changes the tariff; a rules list in the request replaces all rules (null keeps them).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<TariffView>> Update(Guid id, [FromBody] CreateTariffRequest req)
    {
        var tariff = await _db.Tariffs.FindAsync(id);
        if (tariff == null) return NotFound();
        var (rules, error) = Validate(req);
        if (error != null) return BadRequest(new { error, code = "bad_tariff" });

        var oldRules = await _db.TariffRules.Where(r => r.TariffId == id).ToListAsync();
        var before = new { tariff.Name, tariff.PricePerHour, tariff.IsDefault, tariff.ZoneId, tariff.BillingStepMinutes, tariff.MinimumCharge,
                           rules = RulesText(oldRules.OrderBy(r => r.SortOrder).Select(ToRequest).ToList()) };
        Apply(tariff, req);
        if (rules != null)
        {
            _db.TariffRules.RemoveRange(oldRules);
            AddRules(id, rules);
        }
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.TariffUpdated, "tariff", tariff.Id,
            $"Tariff '{tariff.Name}' changed: {before.PricePerHour} -> {tariff.PricePerHour} ₴/h" + (rules != null ? $", {rules.Count} rule(s)" : ""),
            new { before, after = new { tariff.Name, tariff.PricePerHour, tariff.IsDefault, tariff.ZoneId, tariff.BillingStepMinutes, tariff.MinimumCharge,
                                        rules = rules != null ? RulesText(rules) : before.rules } });
        return Ok(await ViewAsync(id));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tariff = await _db.Tariffs.FindAsync(id);
        if (tariff == null) return NotFound();
        _db.Tariffs.Remove(tariff);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.TariffDeleted, "tariff", id, $"Tariff '{tariff.Name}' deleted ({tariff.PricePerHour} ₴/h)",
            new { tariff.Name, tariff.PricePerHour });
        return NoContent();
    }

    /// <summary>
    /// Price of prepaid minutes starting now (or at StartUtc) with the tariff of the request, or the
    /// tariff of the PC's zone: total and the price slices per rule.
    /// </summary>
    [HttpPost("quote")]
    public async Task<ActionResult<QuoteView>> Quote([FromBody] QuoteRequest req)
    {
        if (req.Minutes is <= 0 or > 24 * 60) return BillingErrors.BadMinutes.ToActionResult();
        TariffEntity? tariff;
        if (req.TariffId.HasValue)
        {
            tariff = await _db.Tariffs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == req.TariffId.Value);
        }
        else
        {
            var zoneId = req.DeviceId.HasValue
                ? await _db.Devices.AsNoTracking().Where(d => d.Id == req.DeviceId.Value).Select(d => d.ZoneId).FirstOrDefaultAsync()
                : null;
            tariff = await SessionService.ZoneTariffAsync(_db, zoneId);
        }
        if (tariff == null) return BillingErrors.TariffNotFound.ToActionResult();
        var rules = await _db.TariffRules.AsNoTracking().Where(r => r.TariffId == tariff.Id).ToListAsync();
        var start = req.StartUtc ?? _clock.UtcNow;
        var quote = TariffCalculator.QuoteFixed(TariffPricing.From(tariff, rules), start, req.Minutes, _clock.Zone);
        return Ok(new QuoteView(tariff.Id, tariff.Name, quote.Total, req.Minutes, quote.MinimumApplied,
            quote.Slices.Select(s => new QuoteSliceView(s.StartUtc, s.EndUtc, s.PricePerHour, s.RuleName,
                (int)Math.Round((s.EndUtc - s.StartUtc).TotalMinutes), TariffCalculator.Round(s.Amount))).ToList()));
    }

    private async Task<TariffView> ViewAsync(Guid id)
    {
        var tariff = await _db.Tariffs.AsNoTracking().FirstAsync(t => t.Id == id);
        var rules = await _db.TariffRules.AsNoTracking().Where(r => r.TariffId == id).ToListAsync();
        var zones = await _db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name);
        return View(tariff, rules, zones);
    }

    private TariffView View(TariffEntity t, IReadOnlyList<TariffRuleEntity> allRules, IReadOnlyDictionary<Guid, string> zones)
    {
        var rules = allRules.Where(r => r.TariffId == t.Id).OrderBy(r => r.SortOrder).ToList();
        var (priceNow, ruleNow) = TariffCalculator.PriceAt(TariffPricing.From(t, rules), _clock.ToLocal(_clock.UtcNow));
        return new TariffView(t.Id, t.Name, t.PricePerHour, t.IsDefault, t.ZoneId,
            t.ZoneId.HasValue && zones.TryGetValue(t.ZoneId.Value, out var zone) ? zone : null)
        {
            BillingStepMinutes = t.BillingStepMinutes > 0 ? t.BillingStepMinutes : 1,
            MinimumCharge = t.MinimumCharge,
            Rules = rules.Select(ToRequest).ToList(),
            PriceNow = priceNow,
            RuleNow = ruleNow
        };
    }

    private static TariffRuleRequest ToRequest(TariffRuleEntity r) => new(r.Name, TariffCalculator.MaskToDays(r.DaysMask),
        TariffCalculator.MinuteText(r.StartMinute), TariffCalculator.MinuteText(r.EndMinute), r.PricePerHour);

    private static string RulesText(IReadOnlyList<TariffRuleRequest>? rules) => rules == null ? "" :
        string.Join("; ", rules.Select(r => $"{r.Name}: {string.Join(",", r.Days ?? Array.Empty<int>())} {r.Start}-{r.End} {r.PricePerHour} ₴/h"));

    private static void Apply(TariffEntity tariff, CreateTariffRequest req)
    {
        tariff.Name = req.Name.Trim();
        tariff.PricePerHour = req.PricePerHour;
        tariff.IsDefault = req.IsDefault;
        tariff.ZoneId = req.ZoneId;
        tariff.BillingStepMinutes = req.BillingStepMinutes ?? (tariff.BillingStepMinutes > 0 ? tariff.BillingStepMinutes : 1);
        tariff.MinimumCharge = req.MinimumCharge ?? tariff.MinimumCharge;
    }

    private void AddRules(Guid tariffId, IReadOnlyList<TariffRuleRequest> rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            TariffCalculator.TryParseMinute(r.Start, out var start);
            TariffCalculator.TryParseMinute(r.End, out var end);
            _db.TariffRules.Add(new TariffRuleEntity
            {
                Id = Guid.NewGuid(), TariffId = tariffId, Name = r.Name.Trim(), DaysMask = TariffCalculator.DaysToMask(r.Days)!.Value,
                StartMinute = start, EndMinute = end, PricePerHour = r.PricePerHour, SortOrder = i
            });
        }
    }

    private static (List<TariffRuleRequest>? Rules, string? Error) Validate(CreateTariffRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 100) return (null, "Name is required (at most 100 characters).");
        if (req.PricePerHour is < 0 or > 100_000) return (null, "Price per hour must be between 0 and 100000.");
        if (req.BillingStepMinutes is < 1 or > 60) return (null, "Rounding step must be between 1 and 60 minutes.");
        if (req.MinimumCharge is < 0 or > 100_000) return (null, "Minimum charge must be between 0 and 100000.");
        if (req.Rules == null) return (null, null);
        if (req.Rules.Count > 20) return (null, "At most 20 rules per tariff.");
        foreach (var r in req.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100) return (null, "Every rule needs a name (at most 100 characters).");
            if (TariffCalculator.DaysToMask(r.Days) == null) return (null, $"Rule '{r.Name}': choose at least one day (1 = Monday ... 7 = Sunday).");
            if (!TariffCalculator.TryParseMinute(r.Start, out var start) || start >= 24 * 60) return (null, $"Rule '{r.Name}': start must be HH:mm (00:00-23:59).");
            if (!TariffCalculator.TryParseMinute(r.End, out var end)) return (null, $"Rule '{r.Name}': end must be HH:mm (00:00-24:00).");
            if (start == end) return (null, $"Rule '{r.Name}': start and end are the same.");
            if (r.PricePerHour is < 0 or > 100_000) return (null, $"Rule '{r.Name}': price must be between 0 and 100000.");
        }
        return (req.Rules.ToList(), null);
    }
}

/// <summary>Days: ISO weekdays of the window start (1 = Monday ... 7 = Sunday). Start/End: "HH:mm"; End &lt;= Start = past midnight.</summary>
public record TariffRuleRequest(string Name, int[]? Days, string Start, string End, decimal PricePerHour);

public record CreateTariffRequest(string Name, decimal PricePerHour, bool IsDefault, Guid? ZoneId,
    int? BillingStepMinutes = null, decimal? MinimumCharge = null, IReadOnlyList<TariffRuleRequest>? Rules = null);

public record TariffView(Guid Id, string Name, decimal PricePerHour, bool IsDefault, Guid? ZoneId, string? ZoneName)
{
    public int BillingStepMinutes { get; init; } = 1;
    public decimal MinimumCharge { get; init; }
    public IReadOnlyList<TariffRuleRequest> Rules { get; init; } = Array.Empty<TariffRuleRequest>();
    /// <summary>Price that applies right now (club local time) and the rule that gives it.</summary>
    public decimal PriceNow { get; init; }
    public string? RuleNow { get; init; }
}

public record QuoteRequest(int Minutes, Guid? TariffId = null, Guid? DeviceId = null, DateTimeOffset? StartUtc = null);
public record QuoteSliceView(DateTimeOffset StartUtc, DateTimeOffset EndUtc, decimal PricePerHour, string? RuleName, int Minutes, decimal Amount);
public record QuoteView(Guid TariffId, string TariffName, decimal Total, int Minutes, bool MinimumApplied, IReadOnlyList<QuoteSliceView> Slices);
