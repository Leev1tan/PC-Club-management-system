using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Server.Pos;
using Cms.Server.Repositories;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Demo mode, phase 4: bar catalog, shifts, receipts, debt and the audit chain of the seed; the simulator at the bar.</summary>
public sealed class DemoBarTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly DeviceRuntimeState _runtime = new();

    public void Dispose() => _testDb.Dispose();

    private async Task<(DemoSimulator Sim, DemoSeedResult Seed)> SeedAsync(DateTimeOffset now)
    {
        await using var db = _testDb.NewShiftContext();
        var seed = await DemoSeeder.ResetAsync(db, now, new Random(42));
        var sim = new DemoSimulator(NullLogger<DemoSimulator>.Instance, _runtime, new Random(7), posRng: new Random(11));
        sim.Load(seed, now);
        return (sim, seed);
    }

    private async Task TickAsync(DemoSimulator sim, DateTimeOffset now)
    {
        await using var db = _testDb.NewShiftContext();
        await sim.TickAsync(db, new EfDeviceRepository(db, _runtime), now);
    }

    [Theory]
    [InlineData("now")]
    [InlineData("09:01")] // a minute after the day shift opened
    [InlineData("21:01")] // a minute after the night shift opened
    [InlineData("00:01")] // a minute into the club day
    [InlineData("10:00")] // an hour after the day shift opened
    [InlineData("22:00")] // an hour after the night shift opened
    [InlineData("14:00")]
    public async Task Seed_has_a_bar_shifts_with_a_variance_and_everything_reconciles(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        await SeedAsync(now);
        await using var db = _testDb.NewContext();

        // Catalog: Ukrainian bar in UAH, with stock, a low-stock warning and a product off sale
        var products = await db.Products.AsNoTracking().ToListAsync();
        Assert.True(products.Count >= 15);
        foreach (var category in new[] { "Енергетики", "Кава", "Снеки", "Вода й напої" }) Assert.Contains(products, p => p.Category == category);
        Assert.Contains(products, p => p.TrackStock && p.LowStockThreshold.HasValue && p.StockQuantity <= p.LowStockThreshold.Value);
        Assert.Contains(products, p => !p.IsActive);
        Assert.All(products.Where(p => p.TrackStock), p => Assert.True(p.StockQuantity >= 0));
        Assert.Empty(await Stock.ReconcileAsync(db));

        // Shifts: back to back, exactly one open (the demo cashier's), yesterday's day shift 15 ₴ short
        var shifts = await db.Shifts.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        var open = Assert.Single(shifts, s => s.Status == ShiftStatuses.Open);
        Assert.Equal("demo-cashier", open.OpenedByUsername);
        Assert.Equal(open.Id, shifts[^1].Id);
        Assert.All(shifts.Zip(shifts.Skip(1)), pair => Assert.Equal(pair.First.ClosedUtc, pair.Second.OpenedUtc));
        Assert.Contains(shifts, s => s.Variance == -15m && s.CloseNote != null && (now - s.ClosedUtc!.Value).TotalHours < 48);
        // A minute after 09:00 or 21:00 the new shift can have no receipt yet; after an hour it has some
        if (now - open.OpenedUtc >= TimeSpan.FromHours(1))
            Assert.True(await db.Sales.CountAsync(s => s.ShiftId == open.Id) >= 3);

        // Every desk amount belongs to a shift; expected cash and the Z-reports reconcile with the ledger
        Assert.False(await db.Ledger.AnyAsync(l => l.ShiftId == null && (l.Method == PaymentMethods.Cash || l.Method == PaymentMethods.Card)));
        var clock = ClubClock.System();
        foreach (var shift in shifts)
        {
            var z = await ZReport.BuildAsync(db, shift, clock);
            Assert.True(z.Ledger.Reconciled, $"shift #{shift.Id}");
            if (shift.Status == ShiftStatuses.Closed)
                Assert.Equal(shift.ExpectedCash, await ShiftService.ExpectedCashAsync(db, shift));
        }
        var ledger = await db.Ledger.AsNoTracking().ToListAsync();
        foreach (var type in new[] { LedgerTypes.BarSale, LedgerTypes.BarRefund, LedgerTypes.CashPayout, LedgerTypes.DebtPayment })
            Assert.True(ledger.Any(l => l.Type == type), $"no {type} row in the seed");
        Assert.Contains(ledger, l => l.Type == LedgerTypes.BarSale && l.Method == PaymentMethods.Balance && l.BonusDelta == 0 && l.BalanceDelta < 0);
        Assert.All(ledger.Where(l => l.Type is LedgerTypes.BarSale or LedgerTypes.BarRefund), l => Assert.NotNull(l.SaleId));

        // Receipts: one refund that points to its sale; balances, bonus and debt are explained by the ledger
        var refund = Assert.Single(await db.Sales.AsNoTracking().Where(s => s.Kind == SaleKinds.Refund).ToListAsync());
        Assert.True(await db.Sales.AnyAsync(s => s.Id == refund.RefundOfSaleId && s.Kind == SaleKinds.Sale));
        Assert.Empty(await Ledger.ReconcileAsync(db));
        foreach (var user in await db.Users.AsNoTracking().ToListAsync()) Assert.Null(await Ledger.CheckChainAsync(db, user.Id));
        var debtor = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "ghost_ua");
        Assert.True(debtor.Debt > 0m);
        Assert.Contains(await db.Sessions.AsNoTracking().ToListAsync(), s => s.PaymentMethod == PaymentMethods.Free && s.TotalCost == 0m);

        // The audit log of the seed is a valid hash chain with the anti-fraud events
        var verify = await AuditChain.VerifyAsync(db);
        Assert.True(verify.Ok, $"{verify.Problem} at {verify.FirstBrokenId}");
        var actions = await db.AuditLog.AsNoTracking().Select(a => a.Action).ToListAsync();
        foreach (var action in new[] { AuditActions.EmergencyUnlock, AuditActions.SaleRefunded, AuditActions.StockCorrected, AuditActions.SessionFreeTime,
                     AuditActions.ShiftCashPayout, AuditActions.ShiftClosed, AuditActions.PlayerDebtRecorded, AuditActions.ProductPriceChanged })
            Assert.Contains(action, actions);
    }

    [Fact]
    public async Task Simulator_sells_at_the_bar_and_reopens_a_closed_shift()
    {
        var now = DateTimeOffset.UtcNow;
        var (sim, _) = await SeedAsync(now);
        int receipts;
        await using (var db = _testDb.NewContext()) receipts = await db.Sales.CountAsync();

        for (var t = 1; t <= 300; t += 3) await TickAsync(sim, now.AddSeconds(t)); // 5 minutes
        await using (var db = _testDb.NewContext())
        {
            Assert.True(await db.Sales.CountAsync() > receipts);
            var open = await ShiftService.OpenShiftAsync(db);
            var actor = new StaffActor(open!.OpenedById, open.OpenedByUsername);
            Assert.True((await new ShiftService(db, actor: actor).CloseAsync(open.Id, 1000m, "visitor", isManager: false)).Ok);
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.Empty(await Stock.ReconcileAsync(db));
        }

        // Without a shift the desk takes no money; after a few minutes the demo cashier opens the next shift
        for (var t = 301; t <= 360; t += 3) await TickAsync(sim, now.AddSeconds(t));
        await using (var db = _testDb.NewContext())
            Assert.Null(await ShiftService.OpenShiftAsync(db));
        for (var t = 361; t <= 600; t += 3) await TickAsync(sim, now.AddSeconds(t));
        await using (var db = _testDb.NewContext())
        {
            var reopened = await ShiftService.OpenShiftAsync(db);
            Assert.NotNull(reopened);
            Assert.Equal("demo-cashier", reopened!.OpenedByUsername);
            Assert.False(await db.Ledger.AnyAsync(l => l.ShiftId == null && (l.Method == PaymentMethods.Cash || l.Method == PaymentMethods.Card)));
            Assert.True((await AuditChain.VerifyAsync(db)).Ok);
        }
    }
}
