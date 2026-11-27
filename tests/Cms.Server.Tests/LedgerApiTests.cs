using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Ledger, bonus balance and cashback through the HTTP API, with the role checks.</summary>
public sealed class LedgerApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public LedgerApiTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task Top_ups_earn_cashback_and_refunds_take_it_back_but_never_pay_out_bonus()
    {
        var owner = await _factory.LoginOwnerAsync();
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var manager = await StaffAsync(StaffRoles.Manager);

        // Cashier may read the cashback rules but not change them
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/billing/settings")).StatusCode);
        var tiers = new { bonusTopUpRules = new[] { new { minAmount = 200, percent = 5 }, new { minAmount = 500, percent = 10 } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/billing/settings", tiers)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync("/api/billing/settings",
            new { bonusTopUpRules = new[] { new { minAmount = 100, percent = 150 } } })).StatusCode);
        var settings = await JsonAsync(await manager.PutAsJsonAsync("/api/billing/settings", tiers));
        Assert.Equal(2, settings.GetProperty("bonusTopUpRules").GetArrayLength());

        var id = await PlayerAsync(cashier);
        var after500 = await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{id}/topup", new { amount = 500, method = "card" }));
        Assert.Equal((500m, 50m), (Dec(after500, "balance"), Dec(after500, "bonusPoints")));
        var after100 = await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{id}/topup", new { amount = 100 }));
        Assert.Equal((600m, 50m), (Dec(after100, "balance"), Dec(after100, "bonusPoints"))); // below the first tier
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/users/{id}/topup", new { amount = 100, method = "balance" })).StatusCode);

        // Refunds need Manager or Owner and a reason; they never exceed the real balance
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/users/{id}/refund", new { amount = 50, reason = "test" })).StatusCode);
        var noReason = await manager.PostAsJsonAsync($"/api/users/{id}/refund", new { amount = 50 });
        Assert.Equal("reason_required", await CodeAsync(noReason));
        var tooMuch = await manager.PostAsJsonAsync($"/api/users/{id}/refund", new { amount = 650, reason = "all of it" });
        Assert.Equal("balance_too_low", await CodeAsync(tooMuch)); // 650 = 600 real + 50 bonus: bonus is not money
        var refunded = await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{id}/refund", new { amount = 200, method = "cash", reason = "Гравець їде" }));
        Assert.Equal((400m, 40m), (Dec(refunded, "balance"), Dec(refunded, "bonusPoints"))); // cashback of 200 ₴ (5%) taken back

        var ledger = (await owner.GetFromJsonAsync<JsonElement>($"/api/users/{id}/ledger")).EnumerateArray().Reverse().ToList();
        Assert.Equal(new[] { "topup", "bonus_accrual", "topup", "refund", "bonus_reversal" }, ledger.Select(l => l.GetProperty("type").GetString()));
        Assert.Equal(new[] { 500m, 0m, 100m, -200m, 0m }, ledger.Select(l => Dec(l, "deskAmount")));
        Assert.Equal("card", ledger[0].GetProperty("method").GetString());
        Assert.All(ledger, l => Assert.False(string.IsNullOrEmpty(l.GetProperty("actorUsername").GetString())));

        var check = await owner.GetFromJsonAsync<JsonElement>($"/api/users/{id}/ledger/check");
        Assert.True(check.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Corrections_need_a_reason_and_never_make_a_balance_negative()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var id = await PlayerAsync(cashier);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/users/{id}/adjust", new { amount = 20, reason = "gift", target = "bonus" })).StatusCode);
        var bonus = await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{id}/adjust", new { amount = 30, reason = "Турнір: приз", target = "bonus" }));
        Assert.Equal((0m, 30m), (Dec(bonus, "balance"), Dec(bonus, "bonusPoints")));
        Assert.Equal("balance_too_low", await CodeAsync(await manager.PostAsJsonAsync($"/api/users/{id}/adjust", new { amount = -40, reason = "too much", target = "bonus" })));
        Assert.Equal("balance_too_low", await CodeAsync(await manager.PostAsJsonAsync($"/api/users/{id}/adjust", new { amount = -1, reason = "below zero" })));
        Assert.Equal("reason_required", await CodeAsync(await manager.PostAsJsonAsync($"/api/users/{id}/adjust", new { amount = 10, reason = "x" })));

        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=player.bonus_adjusted&take=50")).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("summary").GetString()!.Contains("Турнір: приз"));
    }

    [Fact]
    public async Task The_ledger_is_append_only_and_reconciles_with_the_balances()
    {
        var owner = await _factory.LoginOwnerAsync();
        var id = await PlayerAsync(owner);
        await owner.PostAsJsonAsync($"/api/users/{id}/topup", new { amount = 150 });

        var reconcile = await owner.GetFromJsonAsync<JsonElement>("/api/ledger/reconcile");
        Assert.True(reconcile.GetProperty("ok").GetBoolean(), reconcile.ToString());
        Assert.True(reconcile.GetProperty("ledgerRows").GetInt32() >= 1);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.DeleteAsync("/api/ledger")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var row = await db.Ledger.FirstAsync(l => l.UserId == Guid.Parse(id));
        row.Amount = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"LedgerEntries\" SET \"Amount\" = 1"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"LedgerEntries\""));
        // A player with money history cannot be deleted
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM \"Users\" WHERE \"Id\" = '{id.ToUpperInvariant()}'"));
    }

    [Fact]
    public async Task Bonus_pays_only_for_pc_time()
    {
        using var testDb = new TestDb(); // own database: these direct wallet calls write no ledger rows
        await using var db = testDb.NewContext();
        var id = Guid.NewGuid();
        db.Users.Add(new UserEntity { Id = id, Username = "bonus", DisplayName = "B", Balance = 20m, BonusPoints = 100m });
        await db.SaveChangesAsync();

        // Bar (spec area 5): the 100 bonus does not count
        Assert.Null(await Wallet.TryChargeAsync(db, id, 50m, SpendPurpose.Bar));
        Assert.Equal(new Charge(0m, 15m), await Wallet.TryChargeAsync(db, id, 15m, SpendPurpose.Bar));
        // PC time: bonus first
        Assert.Equal(new Charge(50m, 0m), await Wallet.TryChargeAsync(db, id, 50m, SpendPurpose.PcTime));
        Assert.Equal(new Charge(0m, 5m), await Wallet.ChargeUpToAsync(db, id, 5m, SpendPurpose.PcTime, useBonus: false));
        Assert.Equal(new Charge(50m, 0m), await Wallet.ChargeUpToAsync(db, id, 80m, SpendPurpose.PcTime)); // all that is left
        Assert.Equal((0m, 0m), await Wallet.GetAsync(db, id));
    }
}
