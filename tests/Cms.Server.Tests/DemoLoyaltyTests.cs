using Cms.Server.Billing;
using Cms.Server.Demo;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The demo players spread over the loyalty tiers (Bronze, Silver, Gold) at any club time.</summary>
public sealed class DemoLoyaltyTests
{
    [Theory]
    [InlineData("09:01")]
    [InlineData("21:01")]
    [InlineData("00:01")]
    [InlineData("now")]
    public async Task The_demo_players_are_in_every_tier(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        using var testDb = new TestDb();
        await using (var db = testDb.NewShiftContext()) await DemoSeeder.ResetAsync(db, now, new Random(42));
        await using var check = testDb.NewContext();
        var settings = await Loyalty.LoadAsync(check);
        Assert.True(settings.Enabled);
        var spend = await Loyalty.SpendByPlayerAsync(check, now, settings.PeriodDays);
        var players = await check.Users.AsNoTracking().Select(u => new { u.Id, u.Username }).ToListAsync();
        var tiers = players.Select(p => (p.Username, Spend: spend.GetValueOrDefault(p.Id), Tier: Loyalty.TierFor(settings, spend.GetValueOrDefault(p.Id)).Tier?.Name)).ToList();
        var text = string.Join("; ", tiers.OrderBy(t => t.Spend).Select(t => $"{t.Username}={t.Spend}"));
        foreach (var name in new[] { "Bronze", "Silver", "Gold" })
            Assert.True(tiers.Count(t => t.Tier == name) >= 2, $"{name}: {tiers.Count(t => t.Tier == name)} — {text}");
    }
}
