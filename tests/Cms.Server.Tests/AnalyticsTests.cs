using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cms.Server.Analytics;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Owner analytics computed from the ledger, the session segments and the receipts (exact numbers, club time).</summary>
public sealed class AnalyticsServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private static readonly TimeZoneInfo Zone = ClubTimeZone.Default;
    private static readonly DateOnly Monday = new(2026, 11, 23);

    public void Dispose() => _testDb.Dispose();

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        ClubTimeZone.LocalToUtc(day.ToDateTime(new TimeOnly(0, 0)).AddHours(hour).AddMinutes(minute), Zone);

    private static ClubClock ClockAt(DateTimeOffset utc) => new(new FixedTimeProvider(utc), Zone);

    private sealed record Club(Guid A1, Guid A2, Guid B1, Guid ZoneA, Guid ZoneB, long ShiftId);

    /// <summary>Two zones, three approved PCs and a pending one; play time and money of Monday 2026-11-23.</summary>
    private async Task<Club> SeedAsync()
    {
        await using var db = _testDb.NewContext();
        var zoneA = new ZoneEntity { Id = Guid.NewGuid(), Name = "Hall", Color = "#00f0ff", SortOrder = 0 };
        var zoneB = new ZoneEntity { Id = Guid.NewGuid(), Name = "VIP", Color = "#bf5af2", SortOrder = 1 };
        db.Zones.AddRange(zoneA, zoneB);
        DeviceEntity Pc(string name, Guid zone, string status = "approved") => new()
        {
            Id = Guid.NewGuid(), Hostname = name, DeviceKey = DeviceKeys.Hash(Guid.NewGuid().ToString()), Status = status, ZoneId = zone
        };
        var a1 = Pc("A-01", zoneA.Id);
        var a2 = Pc("A-02", zoneA.Id);
        var b1 = Pc("B-01", zoneB.Id);
        db.Devices.AddRange(a1, a2, b1, Pc("NEW-01", zoneA.Id, DeviceStatuses.Pending));

        SessionEntity Session(DeviceEntity pc, DateTimeOffset start, DateTimeOffset end, params (DateTimeOffset Start, DateTimeOffset End)[] segments)
        {
            var s = new SessionEntity { Id = Guid.NewGuid(), DeviceId = pc.Id, StartUtc = start, EndUtc = end, Status = "ended", IsPrepaid = true };
            db.Sessions.Add(s);
            foreach (var (from, to) in segments)
                db.SessionSegments.Add(new SessionSegmentEntity { Id = Guid.NewGuid(), SessionId = s.Id, DeviceId = pc.Id, StartUtc = from, EndUtc = to });
            return s;
        }
        // A-01 10:00-12:00; B-01 10:30-11:00, paused, 11:30-12:00; A-02 23:30 until 01:00 on Tuesday
        var guest = Session(a1, At(Monday, 10), At(Monday, 12), (At(Monday, 10), At(Monday, 12)));
        var package = Session(b1, At(Monday, 10, 30), At(Monday, 12), (At(Monday, 10, 30), At(Monday, 11)), (At(Monday, 11, 30), At(Monday, 12)));
        var night = Session(a2, At(Monday, 23, 30), At(Monday.AddDays(1), 1), (At(Monday, 23, 30), At(Monday.AddDays(1), 1)));

        var player = new UserEntity { Id = Guid.NewGuid(), Username = "olena", DisplayName = "Олена" };
        db.Users.Add(player);
        var shift = new ShiftEntity
        {
            Status = ShiftStatuses.Closed, OpenedUtc = At(Monday, 9), ClosedUtc = At(Monday, 21), OpeningFloat = 500m,
            ExpectedCash = 1000m, CountedCash = 995m, Variance = -5m, ClosedByUsername = "cashier"
        };
        db.Shifts.Add(shift);
        var sale = new SaleEntity { CreatedUtc = At(Monday, 12), Method = "cash", Total = 75m };
        var balanceSale = new SaleEntity { CreatedUtc = At(Monday, 12, 10), Method = "balance", Total = 40m, UserId = player.Id };
        db.Sales.AddRange(sale, balanceSale);
        await db.SaveChangesAsync();
        var refund = new SaleEntity { CreatedUtc = At(Monday, 12, 30), Kind = SaleKinds.Refund, RefundOfSaleId = sale.Id, Method = "cash", Total = 75m };
        db.Sales.Add(refund);
        await db.SaveChangesAsync();
        var monster = new ProductEntity { Id = Guid.NewGuid(), Name = "Monster", Category = "Енергетики", Price = 75m };
        var water = new ProductEntity { Id = Guid.NewGuid(), Name = "Вода", Category = "Вода", Price = 20m };
        db.Products.AddRange(monster, water);
        db.SaleItems.AddRange(
            new SaleItemEntity { SaleId = sale.Id, ProductId = monster.Id, Name = "Monster", Category = "Енергетики", UnitPrice = 75m, Quantity = 1, LineTotal = 75m },
            new SaleItemEntity { SaleId = balanceSale.Id, ProductId = water.Id, Name = "Вода", Category = "Вода", UnitPrice = 20m, Quantity = 2, LineTotal = 40m },
            new SaleItemEntity { SaleId = refund.Id, ProductId = monster.Id, Name = "Monster", Category = "Енергетики", UnitPrice = 75m, Quantity = 1, LineTotal = 75m });

        LedgerEntryEntity Row(DateTimeOffset at, string type, string method, decimal amount, decimal balance = 0, decimal bonus = 0, decimal desk = 0,
            decimal debt = 0, Guid? session = null, long? saleId = null, bool inShift = true) => new()
        {
            CreatedUtc = at, Type = type, Method = method, Amount = amount, BalanceDelta = balance, BonusDelta = bonus, DeskAmount = desk,
            DebtDelta = debt, SessionId = session, SaleId = saleId, ShiftId = inShift ? shift.Id : null,
            UserId = method is "balance" || debt != 0 || type is LedgerTypes.TopUp or LedgerTypes.BonusAccrual or LedgerTypes.Refund ? player.Id : null
        };
        db.Ledger.AddRange(
            Row(At(Monday, 9, 30), LedgerTypes.TopUp, "cash", 500m, balance: 500m, desk: 500m),
            Row(At(Monday, 9, 30), LedgerTypes.BonusAccrual, "none", 50m, bonus: 50m),
            Row(At(Monday, 10), LedgerTypes.SessionCharge, "cash", 120m, desk: 120m, session: guest.Id),
            Row(At(Monday, 11, 5), LedgerTypes.PackagePurchase, "balance", 150m, balance: -100m, bonus: -50m, session: package.Id),
            Row(At(Monday, 12), LedgerTypes.BarSale, "cash", 75m, desk: 75m, saleId: sale.Id),
            Row(At(Monday, 12, 10), LedgerTypes.BarSale, "balance", 40m, balance: -40m, saleId: balanceSale.Id),
            Row(At(Monday, 12, 30), LedgerTypes.BarRefund, "cash", 75m, desk: -75m, saleId: refund.Id),
            Row(At(Monday, 13), LedgerTypes.Refund, "cash", 100m, balance: -100m, desk: -100m),
            Row(At(Monday, 23, 35), LedgerTypes.SessionCharge, "debt", 60m, debt: 60m, session: night.Id, inShift: false));
        await db.SaveChangesAsync();
        return new Club(a1.Id, a2.Id, b1.Id, zoneA.Id, zoneB.Id, shift.Id);
    }

    [Fact]
    public async Task One_day_adds_up_money_occupancy_per_zone_and_the_heatmap()
    {
        var club = await SeedAsync();
        var clock = ClockAt(At(Monday.AddDays(2), 12));
        await using var db = _testDb.NewContext();
        var period = AnalyticsPeriod.Resolve(null, Monday, Monday, clock)!;
        var a = await AnalyticsService.BuildAsync(db, period, clock);

        // Money: PC time 120 cash + 150 package (100 balance, 50 bonus) + 60 debt; bar 75 + 40 - 75
        Assert.Equal((370m, 330m, 40m), (a.Revenue.Total, a.Revenue.Time, a.Revenue.Bar));
        Assert.Equal((120m, 0m, 140m, 50m, 60m), (a.Revenue.ByMethod.Cash, a.Revenue.ByMethod.Card, a.Revenue.ByMethod.Balance, a.Revenue.ByMethod.Bonus, a.Revenue.ByMethod.Debt));
        Assert.Equal(a.Revenue.Total, a.Revenue.ByMethod.Total);
        Assert.Equal((75m, 1, 100m, 1, 175m), (a.Refunds.Bar, a.Refunds.BarReceipts, a.Refunds.Balance, a.Refunds.BalanceRefunds, a.Refunds.Total));
        Assert.Equal((50m, 50m), (a.Bonus.Issued, a.Bonus.Spent));
        Assert.Equal((500m, 1), (a.TopUps.Total, a.TopUps.Count));
        Assert.Equal((110m, 3, 57.5m, 2), (a.AverageTicket.Session, a.AverageTicket.Sessions, a.AverageTicket.Receipt, a.AverageTicket.Receipts));
        Assert.Equal((695m - 175m, 0m), (a.Cash.Net, a.Card.Net)); // 500 + 120 + 75 - 75 - 100
        Assert.DoesNotContain(a.Bar.Items, i => i.Name == "Monster"); // sold and refunded
        Assert.Equal((2, 40m), (a.Bar.Items.Single(i => i.Name == "Вода").Quantity, a.Bar.Items.Single(i => i.Name == "Вода").Amount));

        // Occupancy: 3 approved PCs x 24 h; busy A-01 2 h, B-01 1 h (the pause is not play time), A-02 0.5 h before midnight
        Assert.Equal((3, 72m, 3.5m, 4.9m), (a.Occupancy.Pcs, a.Occupancy.CapacityHours, a.Occupancy.BusyHours, a.Occupancy.Percent));
        var hall = a.Occupancy.Zones.Single(z => z.ZoneId == club.ZoneA);
        var vip = a.Occupancy.Zones.Single(z => z.ZoneId == club.ZoneB);
        Assert.Equal((2, 2.5m, 48m, 5.2m), (hall.Pcs, hall.BusyHours, hall.CapacityHours, hall.Percent));
        Assert.Equal((1, 1m, 24m, 4.2m), (vip.Pcs, vip.BusyHours, vip.CapacityHours, vip.Percent));

        // Heatmap: Monday 10:00 and 11:00 = 90 of 180 PC-minutes, 23:00 = 30 of 180; Tuesday is not in the period
        var monday = a.Heatmap.Percent[0];
        Assert.Equal((50.0m, 50.0m, 16.7m, 0m), (monday[10], monday[11], monday[23], monday[9]));
        Assert.All(a.Heatmap.Percent[1], cell => Assert.Null(cell));
        Assert.Equal(50.0m, a.Heatmap.Max);

        // One day: the chart has 24 club hours
        Assert.Equal("hour", a.Granularity);
        Assert.Equal(24, a.Series.Count);
        Assert.Equal(("10:00", 120m, 50.0m), (a.Series[10].Label, a.Series[10].Time, a.Series[10].Occupancy));
        Assert.Equal((150m, 0m), (a.Series[11].Time, a.Series[11].Bar));
        Assert.Equal(a.Revenue.Total, a.Series.Sum(p => p.Total));

        // Shifts and the reconciliation with the Z-report of the shift and the ledger
        Assert.Equal((1, -5m, 5m, 0m), (a.Shifts.Closed, a.Shifts.Variance, a.Shifts.Shortage, a.Shifts.Surplus));
        Assert.True(a.Check.Reconciled);
        Assert.True(a.Check.ShiftsAddUp);
        Assert.Equal(1, a.Check.ShiftsInside);
        await using var zdb = _testDb.NewContext();
        var z = await ZReport.BuildAsync(zdb, zdb.Shifts.Single(s => s.Id == club.ShiftId), clock);
        Assert.Equal((z.Time.Total, z.Bar.Net, z.Cash.Net, z.Card.Net), (a.Check.ZReports.Time, a.Check.ZReports.Bar, a.Check.ZReports.CashNet, a.Check.ZReports.CardNet));
        Assert.Equal((60m, 0m, 1), (a.Check.OutsideShifts.Time, a.Check.OutsideShifts.Bar, a.Check.OutsideShifts.Rows)); // the debt row after the shift
        Assert.Equal(a.Revenue.Time, z.Time.Total + a.Check.OutsideShifts.Time);
    }

    [Fact]
    public async Task Several_days_by_day_until_now_and_the_period_names()
    {
        await SeedAsync();
        var now = At(Monday.AddDays(2), 12); // Wednesday noon
        var clock = ClockAt(now);
        await using var db = _testDb.NewContext();
        var a = await AnalyticsService.BuildAsync(db, AnalyticsPeriod.Resolve(null, Monday, Monday.AddDays(2), clock)!, clock);
        Assert.Equal(("day", 3), (a.Granularity, a.Series.Count));
        Assert.Equal(now, a.EndUtc);
        Assert.Equal(3 * 60m, a.Occupancy.CapacityHours); // 3 PCs x (24 + 24 + 12) h
        Assert.Equal(33.3m, a.Heatmap.Percent[1][0]);     // Tuesday 00:00-01:00: A-02 of 3 PCs
        Assert.Null(a.Heatmap.Percent[2][12]);            // Wednesday after noon: not yet
        Assert.Equal(0m, a.Heatmap.Percent[2][11]);
        Assert.Equal(1m / 72m * 100m, a.Series[1].Occupancy!.Value, 1);

        var today = AnalyticsPeriod.Resolve("today", null, null, clock)!;
        Assert.Equal((Monday.AddDays(2), Monday.AddDays(2), now), (today.From, today.To, today.EndUtc));
        var week = AnalyticsPeriod.Resolve("7d", null, null, clock)!;
        Assert.Equal((Monday.AddDays(-4), At(Monday.AddDays(-4), 0)), (week.From, week.FromUtc));
        Assert.Equal(Monday.AddDays(-27), AnalyticsPeriod.Resolve("30d", null, null, clock)!.From);
        Assert.Null(AnalyticsPeriod.Resolve("year", null, null, clock));
        Assert.Null(AnalyticsPeriod.Resolve(null, Monday, null, clock));
        Assert.Null(AnalyticsPeriod.Resolve(null, Monday.AddDays(3), Monday.AddDays(4), clock)); // future
        Assert.Null(AnalyticsPeriod.Resolve(null, Monday.AddDays(-400), Monday, clock));

        // The dashboard tiles use the same sums
        var (revToday, revWeek, revMonth) = await AnalyticsService.RevenueAsync(db, clock);
        Assert.Equal((0m, 370m, 370m), (revToday, revWeek, revMonth));
    }

    [Fact]
    public async Task Autumn_daylight_saving_day_has_25_club_hours()
    {
        await SeedAsync();
        var day = new DateOnly(2026, 10, 25); // Kyiv: 04:00 summer time -> 03:00 winter time
        var clock = ClockAt(At(Monday, 12));
        await using var db = _testDb.NewContext();
        var a = await AnalyticsService.BuildAsync(db, AnalyticsPeriod.Resolve(null, day, day, clock)!, clock);
        Assert.Equal(3 * 25m, a.Occupancy.CapacityHours);
        Assert.Equal(24, a.Series.Count);
        Assert.Equal(1.0, (a.Series[3].StartUtc - a.Series[2].StartUtc).TotalHours);
        Assert.Equal(2.0, (a.Series[4].StartUtc - a.Series[3].StartUtc).TotalHours); // 03:00 twice
    }
}

/// <summary>A club with its own database: cash and card need an open shift (default).</summary>
public sealed class AnalyticsFactory : CmsFactory
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Setup:OwnerUsername"] = OwnerUsername,
        ["Setup:OwnerPassword"] = OwnerPassword,
    };
}

/// <summary>The analytics API reconciles with the Z-reports of the shifts and with the ledger; roles; CSV.</summary>
public sealed class AnalyticsApiTests : ApiTestBase, IClassFixture<AnalyticsFactory>
{
    public AnalyticsApiTests(AnalyticsFactory factory) : base(factory) { }

    private static decimal D(JsonElement e, params string[] path)
    {
        foreach (var p in path) e = e.GetProperty(p);
        return e.GetDecimal();
    }

    [Fact]
    public async Task Analytics_of_today_equal_the_z_reports_plus_rows_outside_shifts_and_the_ledger()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);

        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Hall" }));
        var zoneId = Guid.Parse(zone.GetProperty("id").GetString()!);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Hall", pricePerHour = 60, isDefault = true, zoneId }));
        var pack = await JsonAsync(await owner.PostAsJsonAsync("/api/packages", new { name = "3 h", zoneId, price = 150, durationMinutes = 180 }));
        var pc1 = await AddDeviceAsync(zoneId);
        var pc2 = await AddDeviceAsync(zoneId);
        var pc3 = await AddDeviceAsync(zoneId);
        await JsonAsync(await manager.PutAsJsonAsync("/api/billing/settings", new { bonusTopUpRules = new[] { new { minAmount = 500, percent = 10 } } }));
        var cola = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", new { name = "Cola", category = "Напої", price = 35, trackStock = false }))).GetProperty("id").GetString();
        var player = await PlayerAsync(cashier);

        // Shift 1: top-up with cashback, a guest session in cash, bar in cash and by card, a refunded receipt
        var s1 = (await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat = 500 }))).GetProperty("id").GetInt64();
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 500, method = "cash" }));
        var guest = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc1, durationMinutes = 60, paymentMethod = "cash" }));
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = cola, quantity = 2 } }, paymentMethod = "cash" }));
        var card = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = cola, quantity = 1 } }, paymentMethod = "card" }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/sales/{card.GetProperty("id").GetInt64()}/refund", new { reason = "Помилка" }));
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/shifts/{s1}/close", new { countedCash = 500 + 500 + D(guest, "totalCost") + 70 - 10 }));

        // Between the shifts: a package from the balance (bonus first) needs no shift
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc2, userId = player, packageId = pack.GetProperty("id").GetString() }));

        // Shift 2: bar from the balance, a balance refund, a payout
        var s2 = (await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat = 300 }))).GetProperty("id").GetInt64();
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = cola, quantity = 1 } }, userId = player }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/refund", new { amount = 50, method = "cash", reason = "Частина" }));
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc3, durationMinutes = 30, paymentMethod = "card" }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/shifts/{s2}/payout", new { amount = 20, reason = "Вода для бару" }));
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/shifts/{s2}/close", new { countedCash = 230 }));

        // Roles and input
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/analytics/export.csv")).StatusCode);
        var bad = await manager.GetAsync("/api/analytics?period=year");
        Assert.Equal((HttpStatusCode.BadRequest, "bad_period"), (bad.StatusCode, await CodeAsync(bad)));

        var a = await JsonAsync(await manager.GetAsync("/api/analytics?period=today"));
        var z1 = await JsonAsync(await manager.GetAsync($"/api/shifts/{s1}/report"));
        var z2 = await JsonAsync(await manager.GetAsync($"/api/shifts/{s2}/report"));

        // Analytics = Z-report 1 + Z-report 2 + the package between the shifts (150: 50 bonus + 100 balance)
        Assert.Equal(D(z1, "time", "total") + D(z2, "time", "total") + 150m, D(a, "revenue", "time"));
        Assert.Equal(D(z1, "bar", "net") + D(z2, "bar", "net"), D(a, "revenue", "bar"));
        Assert.Equal(D(z1, "cash", "net") + D(z2, "cash", "net"), D(a, "cash", "net"));
        Assert.Equal(D(z1, "card", "net") + D(z2, "card", "net"), D(a, "card", "net"));
        Assert.Equal(D(z1, "bonus", "issued") + D(z2, "bonus", "issued"), D(a, "bonus", "issued"));
        Assert.Equal(50m, D(a, "bonus", "spent"));
        Assert.Equal(D(z1, "topUps", "byMethod", "total") + D(z2, "topUps", "byMethod", "total"), D(a, "topUps", "byMethod", "total"));
        Assert.Equal((35m, 1, 50m, 1), (D(a, "refunds", "bar"), a.GetProperty("refunds").GetProperty("barReceipts").GetInt32(),
            D(a, "refunds", "balance"), a.GetProperty("refunds").GetProperty("balanceRefunds").GetInt32()));
        Assert.Equal(20m, D(a, "payouts"));
        Assert.Equal(D(a, "revenue", "total"), D(a, "revenue", "byMethod", "total"));
        Assert.Equal(D(a, "revenue", "time") + D(a, "revenue", "bar"), D(a, "revenue", "total"));

        var check = a.GetProperty("check");
        Assert.True(check.GetProperty("reconciled").GetBoolean());
        Assert.True(check.GetProperty("shiftsAddUp").GetBoolean());
        Assert.Equal(2, check.GetProperty("shiftsInside").GetInt32());
        Assert.Equal((150m, 1), (D(check, "outsideShifts", "time"), check.GetProperty("outsideShifts").GetProperty("rows").GetInt32()));

        // The same numbers straight from the ledger
        var rows = (await owner.GetFromJsonAsync<JsonElement>("/api/ledger?take=500")).EnumerateArray().ToList();
        Assert.Equal(rows.Count, check.GetProperty("ledgerRows").GetInt32());
        Assert.Equal(rows.Where(r => r.GetProperty("type").GetString() is "session_charge" or "package_purchase").Sum(r => D(r, "amount")), D(a, "revenue", "time"));
        Assert.Equal(rows.Where(r => r.GetProperty("type").GetString() == "bar_sale").Sum(r => D(r, "amount"))
                     - rows.Where(r => r.GetProperty("type").GetString() == "bar_refund").Sum(r => D(r, "amount")), D(a, "revenue", "bar"));
        Assert.Equal(rows.Where(r => r.GetProperty("method").GetString() == "cash").Sum(r => D(r, "deskAmount")), D(a, "cash", "net"));
        Assert.Equal(rows.Where(r => r.GetProperty("method").GetString() == "card").Sum(r => D(r, "deskAmount")), D(a, "card", "net"));

        // Shifts, occupancy of the three PCs, the dashboard tiles
        Assert.Equal((2, -10m), (a.GetProperty("shifts").GetProperty("closed").GetInt32(), D(a, "shifts", "variance")));
        Assert.Equal(3, a.GetProperty("occupancy").GetProperty("pcs").GetInt32());
        Assert.True(D(a, "occupancy", "capacityHours") > 0m); // the sessions started seconds ago: exact play time in AnalyticsServiceTests
        Assert.Equal("Hall", a.GetProperty("occupancy").GetProperty("zones")[0].GetProperty("name").GetString());
        Assert.Equal(7, a.GetProperty("heatmap").GetProperty("percent").GetArrayLength());
        var revenue = await JsonAsync(await manager.GetAsync("/api/sessions/revenue"));
        Assert.Equal((D(a, "revenue", "total"), D(a, "revenue", "total")), (D(revenue, "today"), D(revenue, "week")));
        var week = await JsonAsync(await manager.GetAsync("/api/analytics?period=7d"));
        Assert.Equal(("day", 7), (week.GetProperty("granularity").GetString(), week.GetProperty("series").GetArrayLength()));
        Assert.Equal(D(a, "revenue", "total"), D(week, "revenue", "total"));

        // CSV of the period: BOM, currency in the headers, the checks
        var bytes = await (await manager.GetAsync("/api/analytics/export.csv?period=today")).Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var uk = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Розділ;Показник;Сума, ₴;Кількість", uk);
        Assert.Contains($"Виручка;Разом (час + бар);{D(a, "revenue", "total"):0.00}".Replace('.', ','), uk);
        Assert.Contains("Перевірка;Збігається із Z-звітами;так", uk);
        Assert.Contains("Година;Час на ПК, ₴;Бар, ₴;Разом, ₴", uk);
        Assert.Contains("Нд;", uk);
        var en = Encoding.UTF8.GetString(await (await owner.GetAsync("/api/analytics/export.csv?period=7d&lang=en&sep=comma")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Section,Item,\"Amount, UAH\",Count", en);
        Assert.Contains("Check,Reconciled with the Z-reports,yes", en);
        Assert.Contains("Date,\"PC time, UAH\"", en);
    }
}
