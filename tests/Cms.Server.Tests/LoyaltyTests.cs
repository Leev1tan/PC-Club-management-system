using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Server.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

/// <summary>Loyalty tiers (spec area 3) with a fixed club clock (UTC+2): the spend of the period, the tier, the discount on PC time.</summary>
public sealed class LoyaltyTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = Clock(Local(0, 10)); // Monday 10:00: Standard costs 45/h
    private readonly Guid _std, _vip, _std1, _std2, _std3, _vip1, _pack3h;
    private static readonly LoyaltySettings Tiers = new(30, new[]
    {
        new LoyaltyTier("Bronze", 0m, 0m, 0m),
        new LoyaltyTier("Silver", 100m, 2m, 5m),
        new LoyaltyTier("Gold", 300m, 5m, 10m),
    });

    public LoyaltyTests()
    {
        using var db = _testDb.NewContext();
        (_std, _vip) = (Guid.NewGuid(), Guid.NewGuid());
        db.Zones.AddRange(new ZoneEntity { Id = _std, Name = "Standard" }, new ZoneEntity { Id = _vip, Name = "VIP", SortOrder = 1 });
        var std = Standard(_std);
        std.Tariff.IsDefault = true;
        var vip = Vip(_vip);
        db.Tariffs.AddRange(std.Tariff, vip.Tariff);
        db.TariffRules.AddRange(std.Rules.Concat(vip.Rules));
        DeviceEntity Pc(string name, Guid zone) => new() { Id = Guid.NewGuid(), Hostname = name, DeviceKey = "k-" + name, Status = "approved", ZoneId = zone };
        var pcs = new[] { Pc("STD-01", _std), Pc("STD-02", _std), Pc("STD-03", _std), Pc("VIP-01", _vip) };
        (_std1, _std2, _std3, _vip1) = (pcs[0].Id, pcs[1].Id, pcs[2].Id, pcs[3].Id);
        db.Devices.AddRange(pcs);
        _pack3h = Guid.NewGuid();
        db.Packages.Add(new PackageEntity { Id = _pack3h, Name = "3 години", ZoneId = _std, Price = 150m, DurationMinutes = 180 });
        db.SaveChanges();
        Loyalty.SaveAsync(db, Tiers, "owner", _clock.UtcNow).GetAwaiter().GetResult();
    }

    public void Dispose() => _testDb.Dispose();

    private SessionService Sessions(CmsDbContext db) => new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor()));

    /// <summary>A player with a real balance and the real money spent so many days ago (a PC time row of the ledger).</summary>
    private async Task<UserEntity> PlayerAsync(decimal balance, decimal spent = 0m, int daysAgo = 2)
    {
        await using var db = _testDb.NewContext();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await Wallet.CreditAsync(db, user.Id, balance + spent);
        await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = user.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = balance + spent, BalanceDelta = balance + spent },
            null, _clock.UtcNow.AddDays(-daysAgo - 1));
        if (spent > 0)
        {
            await Wallet.TryTakeAsync(db, user.Id, spent, 0m);
            await Ledger.AppendAsync(db, new LedgerEntryEntity
            {
                UserId = user.Id, Type = LedgerTypes.SessionCharge, Method = PaymentMethods.Balance, Amount = spent, BalanceDelta = -spent
            }, null, _clock.UtcNow.AddDays(-daysAgo));
        }
        return user;
    }

    private async Task AssertReconciledAsync()
    {
        await using var db = _testDb.NewContext();
        Assert.Empty(await Ledger.ReconcileAsync(db));
        Assert.True(LedgerTotals.Of(await db.Ledger.AsNoTracking().ToListAsync()).Reconciled);
    }

    [Fact]
    public void A_ledger_row_counts_only_the_real_money_spent_at_the_club()
    {
        Assert.Equal(70m, Loyalty.Spend(LedgerTypes.SessionCharge, PaymentMethods.Balance, 100m, -70m, 0m)); // 30 of it was bonus
        Assert.Equal(150m, Loyalty.Spend(LedgerTypes.PackagePurchase, PaymentMethods.Cash, 150m, 0m, 150m));
        Assert.Equal(50m, Loyalty.Spend(LedgerTypes.BarSale, PaymentMethods.Card, 50m, 0m, 50m));
        Assert.Equal(-20m, Loyalty.Spend(LedgerTypes.BarRefund, PaymentMethods.Balance, 20m, 20m, 0m));
        Assert.Equal(-15m, Loyalty.Spend(LedgerTypes.BarRefund, PaymentMethods.Cash, 15m, 0m, -15m));
        Assert.Equal(0m, Loyalty.Spend(LedgerTypes.SessionCharge, PaymentMethods.Debt, 80m, 0m, 0m));   // counted when paid
        Assert.Equal(80m, Loyalty.Spend(LedgerTypes.DebtPayment, PaymentMethods.Cash, 80m, 0m, 80m));
        Assert.Equal(30m, Loyalty.Spend(LedgerTypes.DepositKept, PaymentMethods.Balance, 30m, 0m, 0m));
        Assert.Equal(25m, Loyalty.Spend(LedgerTypes.EventFeeKept, PaymentMethods.Balance, 25m, 0m, 0m));
        Assert.Equal(0m, Loyalty.Spend(LedgerTypes.TopUp, PaymentMethods.Cash, 500m, 500m, 500m));
        Assert.Equal(0m, Loyalty.Spend(LedgerTypes.DepositHold, PaymentMethods.Balance, 30m, -30m, 0m));
        Assert.Equal(0m, Loyalty.Spend(LedgerTypes.SessionCharge, PaymentMethods.Free, 0m, 0m, 0m));

        // The tier: the highest spend reached; the next one and what is missing
        var t = Loyalty.TierFor(Tiers, 150m);
        Assert.Equal("Silver", t.Tier!.Name);
        Assert.Equal("Gold", t.Next!.Name);
        Assert.Equal(150m, t.ToNext);
        Assert.Equal("Gold", Loyalty.TierFor(Tiers, 300m).Tier!.Name);
        Assert.Null(Loyalty.TierFor(Tiers, 1000m).Next);
        Assert.Null(Loyalty.TierFor(new LoyaltySettings(30, new[] { new LoyaltyTier("Silver", 100m, 1m, 1m) }), 50m).Tier);
        Assert.Equal(4.05m, Loyalty.Discount(40.5m, 10m));
        Assert.Equal(36.45m, Loyalty.Apply(40.5m, 10m));
        Assert.Equal(0m, Loyalty.Discount(40m, 0m));
    }

    [Fact]
    public async Task Only_the_spend_of_the_rolling_period_counts()
    {
        var old = await PlayerAsync(100m, spent: 500m, daysAgo: 31);   // out of the 30 days
        var recent = await PlayerAsync(100m, spent: 120m, daysAgo: 29);
        await using var db = _testDb.NewContext();
        Assert.Equal("Bronze", (await Loyalty.OfPlayerAsync(db, old.Id, _clock.UtcNow))!.Tier!.Name);
        Assert.Equal("Silver", (await Loyalty.OfPlayerAsync(db, recent.Id, _clock.UtcNow))!.Tier!.Name);
        Assert.Equal(120m, (await Loyalty.SpendByPlayerAsync(db, _clock.UtcNow, 30))[recent.Id]);
        // A day later the recent spend leaves the period too
        Assert.Equal("Bronze", (await Loyalty.OfPlayerAsync(db, recent.Id, _clock.UtcNow.AddDays(2)))!.Tier!.Name);
        // Loyalty off: no tier at all
        await Loyalty.SaveAsync(db, LoyaltySettings.Off, "owner", _clock.UtcNow);
        Assert.Null(await Loyalty.OfPlayerAsync(db, recent.Id, _clock.UtcNow));
    }

    [Fact]
    public async Task The_discount_of_the_tier_lowers_every_kind_of_pc_time_and_the_ledger_reconciles()
    {
        var gold = await PlayerAsync(1000m, spent: 400m);
        var silver = await PlayerAsync(1000m, spent: 150m);
        await using (var db = _testDb.NewContext())
        {
            // Prepaid minutes: 60 min at 45/h, Gold -10% = 40.50
            var s = await Sessions(db).StartAsync(new StartSessionRequest(_std1, null, gold.Id, 60));
            Assert.True(s.Ok, s.Error?.Message);
            Assert.Equal(40.50m, s.Value!.TotalCost);
            Assert.Equal(10m, s.Value.DiscountPercent);
            var row = await db.Ledger.AsNoTracking().Where(l => l.SessionId == s.Value.Id).SingleAsync();
            Assert.Equal(40.50m, row.Amount);
            Assert.Contains("знижка Gold 10%", row.Description);
            // An extension keeps the percent of the start: 30 min = 22.50 - 2.25
            var ext = await Sessions(db).ExtendAsync(s.Value.Id, new ExtendSessionRequest(30));
            Assert.True(ext.Ok, ext.Error?.Message);
            Assert.Equal(40.50m + 20.25m, ext.Value!.TotalCost);
            // A move to VIP: the price difference is discounted too
            var quote = await Sessions(db).QuoteTransferAsync(s.Value.Id, _vip1);
            Assert.True(quote.Ok);
            var listSurcharge = TariffCalculator.Round(quote.Value!.Surcharge / 0.9m);
            Assert.True(quote.Value.Surcharge > 0 && quote.Value.Surcharge < listSurcharge);
        }
        await using (var db = _testDb.NewContext())
        {
            // A package: 150 - 5% (Silver) = 142.50
            var p = await Sessions(db).StartAsync(new StartSessionRequest(_std2, null, silver.Id, null, _pack3h));
            Assert.True(p.Ok, p.Error?.Message);
            Assert.Equal(142.50m, p.Value!.TotalCost);
            // A guest pays the list price
            var guest = await Sessions(db).StartAsync(new StartSessionRequest(_std3, null, null, 60, PaymentMethod: PaymentMethods.Cash));
            Assert.Equal(45m, guest.Value!.TotalCost);
            Assert.Equal(0m, guest.Value.DiscountPercent);
        }
        await using (var db = _testDb.NewContext())
        {
            // Post-paid: the discount of the start applies at the end (60 min = 45 - 4.50)
            var ended = await Sessions(db).EndAsync((await db.Sessions.AsNoTracking().SingleAsync(x => x.DeviceId == _std3 && x.Status == "active")).Id);
            Assert.True(ended.Ok);
            var open = await Sessions(db).StartAsync(new StartSessionRequest(_std3, null, gold.Id, null));
            Assert.True(open.Ok, open.Error?.Message);
            _clock.Time().UtcNow = Local(0, 11);
            var end = await Sessions(db).EndAsync(open.Value!.Id);
            Assert.True(end.Ok, end.Error?.Message);
            Assert.Equal(40.50m, end.Value!.Session.TotalCost);
        }
        await AssertReconciledAsync();
        await using (var db = _testDb.NewContext())
        {
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.Action == AuditActions.SessionStarted).ToListAsync();
            Assert.Contains(audit, a => a.Summary!.Contains("tier Gold -10%"));
        }
    }

    [Fact]
    public async Task A_session_from_the_balance_lasts_longer_with_the_discount_and_an_event_price_has_none()
    {
        var gold = await PlayerAsync(45m, spent: 300m);
        await using var db = _testDb.NewContext();
        var s = await Sessions(db).StartAsync(new StartSessionRequest(_std1, null, gold.Id, null, PaymentMethod: PaymentMethods.Balance, LimitToBalance: true));
        Assert.True(s.Ok, s.Error?.Message);
        // 45 at 45/h less 10%: 66 whole minutes instead of 60
        Assert.Equal(Local(0, 11, 6), s.Value!.BalanceEndUtc);
        Assert.Equal(Local(0, 11, 6), await Sessions(db).ProjectBalanceEndAsync(s.Value, _clock.UtcNow));

        var other = await PlayerAsync(100m, spent: 300m);
        var service = new SessionService(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor()))
        {
            Event = new EventTerms(7, Local(0, 12), Free: false, Price: 40m), CanGiveFreeTime = true
        };
        var ev = await service.StartAsync(new StartSessionRequest(_std2, null, other.Id, null, PaymentMethod: PaymentMethods.Balance));
        Assert.True(ev.Ok, ev.Error?.Message);
        Assert.Equal(40m, ev.Value!.TotalCost);
        Assert.Equal(0m, ev.Value.DiscountPercent);
    }
}

/// <summary>The loyalty API: roles, validation, the audit row, the tier bonus on top-ups and its reversal.</summary>
public sealed class LoyaltyApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public LoyaltyApiTests(CmsFactory factory) : base(factory) { }

    private static object Settings(int days = 30, decimal bronzeBonus = 2m) => new
    {
        periodDays = days,
        tiers = new object[]
        {
            new { name = "Bronze", minSpend = 0m, topUpBonusPercent = bronzeBonus, pcDiscountPercent = 0m },
            new { name = "Gold", minSpend = 5000m, topUpBonusPercent = 5m, pcDiscountPercent = 10m },
        }
    };

    [Fact]
    public async Task Only_the_owner_changes_the_tiers()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.NewClient().GetAsync("/api/loyalty")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.NewClient().PutAsJsonAsync("/api/loyalty", Settings())).StatusCode);
        var cashier = await StaffAsync("Cashier");
        var manager = await StaffAsync("Manager");
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/loyalty")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/loyalty", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync("/api/loyalty", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await StaffAsync("Tech")).GetAsync("/api/loyalty")).StatusCode);

        var owner = await _factory.LoginOwnerAsync();
        var bad = await owner.PutAsJsonAsync("/api/loyalty", Settings(days: 3));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("bad_loyalty_settings", await CodeAsync(bad));
        Assert.Equal("bad_loyalty_settings", await CodeAsync(await owner.PutAsJsonAsync("/api/loyalty", Settings(bronzeBonus: 60m))));
        var saved = await JsonAsync(await owner.PutAsJsonAsync("/api/loyalty", Settings()));
        Assert.Equal(2, saved.GetProperty("tiers").GetArrayLength());
        Assert.Equal("Bronze", saved.GetProperty("tiers")[0].GetProperty("name").GetString());

        var audit = await JsonAsync(await owner.GetAsync("/api/audit?action=loyalty.settings_changed"));
        Assert.Contains("Bronze from 0", audit.ToString());

        // The tier bonus of a top-up is a bonus row of its own, taken back with the refund
        var player = await PlayerAsync(cashier);
        var topped = await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 500m, method = "cash" }));
        Assert.Equal(500m, Dec(topped, "balance"));
        Assert.Equal(10m, Dec(topped, "bonusPoints"));            // 2% of the Bronze tier
        var ledger = await JsonAsync(await cashier.GetAsync($"/api/users/{player}/ledger"));
        Assert.Contains(ledger.EnumerateArray(), r => r.GetProperty("type").GetString() == "bonus_accrual" && r.GetProperty("description").GetString()!.Contains("Bronze"));
        var refunded = await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/refund", new { amount = 200m, method = "cash", reason = "тест" }));
        Assert.Equal(6m, Dec(refunded, "bonusPoints"));           // 2% of 200 taken back
        var check = await JsonAsync(await manager.GetAsync($"/api/users/{player}/ledger/check"));
        Assert.True(check.GetProperty("ok").GetBoolean());

        // The tier on the player card and in the list
        var tier = await JsonAsync(await cashier.GetAsync($"/api/users/{player}/loyalty"));
        Assert.True(tier.GetProperty("enabled").GetBoolean());
        Assert.Equal("Bronze", tier.GetProperty("tier").GetProperty("name").GetString());
        Assert.Equal("Gold", tier.GetProperty("next").GetProperty("name").GetString());
        Assert.Equal(5000m, Dec(tier, "toNext"));
        var list = await JsonAsync(await cashier.GetAsync("/api/users"));
        Assert.Contains(list.EnumerateArray(), u => u.GetProperty("id").GetString() == player && u.GetProperty("tier").GetString() == "Bronze");
        var view = await JsonAsync(await cashier.GetAsync("/api/loyalty"));
        Assert.True(view.GetProperty("players")[0].GetInt32() >= 1);

        // An empty list turns loyalty off
        await JsonAsync(await owner.PutAsJsonAsync("/api/loyalty", new { periodDays = 30, tiers = Array.Empty<object>() }));
        Assert.False((await JsonAsync(await cashier.GetAsync($"/api/users/{player}/loyalty"))).GetProperty("enabled").GetBoolean());
    }
}
