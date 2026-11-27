using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Analytics;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Pos;
using Cms.Server.Repositories;
using Cms.Server.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

/// <summary>Bar orders from the lock screen with a fixed club clock: hold, delivery as a bar sale, cancel, limits, reconciliation.</summary>
public sealed class BarOrderTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = Clock(Local(0, 18));
    private readonly Guid _pc1, _pc2, _cola, _chips, _coffee;

    public BarOrderTests()
    {
        using var db = _testDb.NewContext();
        var tariff = new TariffEntity { Id = Guid.NewGuid(), Name = "Std", PricePerHour = 60m, IsDefault = true };
        db.Tariffs.Add(tariff);
        DeviceEntity Pc(string name) => new() { Id = Guid.NewGuid(), Hostname = name, DeviceKey = "k-" + name, Status = "approved" };
        var pcs = new[] { Pc("STD-01"), Pc("STD-02") };
        (_pc1, _pc2) = (pcs[0].Id, pcs[1].Id);
        db.Devices.AddRange(pcs);
        ProductEntity Product(string name, decimal price, int? stock) => new()
        {
            Id = Guid.NewGuid(), Name = name, Category = "Напої", Price = price, TrackStock = stock.HasValue, StockQuantity = stock ?? 0, CreatedUtc = _clock.UtcNow
        };
        var products = new[] { Product("Кола 0,5", 45m, 5), Product("Чипси", 60m, 2), Product("Кава", 50m, null) };
        (_cola, _chips, _coffee) = (products[0].Id, products[1].Id, products[2].Id);
        db.Products.AddRange(products);
        db.SaveChanges();
        foreach (var p in products.Where(p => p.TrackStock))
            db.StockMovements.Add(new StockMovementEntity { CreatedUtc = _clock.UtcNow, ProductId = p.Id, Type = StockMovementTypes.Restock, Delta = p.StockQuantity, QuantityAfter = p.StockQuantity });
        db.SaveChanges();
    }

    public void Dispose() => _testDb.Dispose();

    private BarOrderService Orders(CmsDbContext db) => new(db, _clock, new AuditLogger(db, new HttpContextAccessor()), new StaffActor(null, "cashier"));

    private async Task<Guid> PlayerSessionAsync(Guid pc, decimal balance, decimal bonus = 0m)
    {
        await using var db = _testDb.NewContext();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await Wallet.CreditAsync(db, user.Id, balance, bonus);
        await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = user.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = balance + bonus, BalanceDelta = balance, BonusDelta = bonus }, null, _clock.UtcNow);
        Assert.True((await new SessionService(db, new EfDeviceRepository(db), _clock).StartAsync(new StartSessionRequest(pc, null, user.Id, null, PaymentMethod: "balance", LimitToBalance: true))).Ok);
        return user.Id;
    }

    private async Task GuestSessionAsync(Guid pc)
    {
        await using var db = _testDb.NewContext();
        Assert.True((await new SessionService(db, new EfDeviceRepository(db), _clock).StartAsync(new StartSessionRequest(pc, null, null, 120, PaymentMethod: "cash"))).Ok);
    }

    private async Task<decimal> BalanceAsync(Guid user)
    {
        await using var db = _testDb.NewContext();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user)).Balance;
    }

    [Fact]
    public async Task A_prepaid_order_is_held_then_sold_at_its_prices_and_everything_reconciles()
    {
        await using (var db = _testDb.NewShiftContext()) Assert.True((await new ShiftService(db, _clock, actor: new StaffActor(null, "cashier")).OpenAsync(0m, null)).Ok);
        var player = await PlayerSessionAsync(_pc1, 300m, bonus: 500m);
        long id;
        await using (var db = _testDb.NewShiftContext())
        {
            var menu = await Orders(db).MenuAsync(_pc1);
            Assert.True(menu.Available && menu.CanPayFromBalance);
            Assert.Equal(300m, menu.Balance);
            Assert.Equal(new[] { 2, 5, 10 }, menu.Products.Select(p => p.MaxQuantity).OrderBy(x => x));
            var placed = await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_cola, 2), new OrderLine(_coffee, 1) }, "balance");
            Assert.True(placed.Ok, placed.Error?.Message);
            id = placed.Value!.Id;
            Assert.Equal(140m, placed.Value.Total);
        }
        Assert.Equal(160m, await BalanceAsync(player)); // the real balance; the 500 bonus never pays at the bar

        // The price changes before the delivery: the order keeps its price
        await using (var db = _testDb.NewShiftContext())
        {
            await db.Products.Where(p => p.Id == _cola).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 55m));
            Assert.Equal("order_already_open", (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_coffee, 1) }, "balance")).Error!.Code);
            Assert.True((await Orders(db).PrepareAsync(id)).Ok);
            Assert.Equal("order_preparing", (await Orders(db).CancelAsync(id, null, byPlayer: true)).Error!.Code);
            var delivered = await Orders(db).DeliverAsync(id, null);
            Assert.True(delivered.Ok, delivered.Error?.Message);
            Assert.Equal("order_not_open", (await Orders(db).DeliverAsync(id, null)).Error!.Code);
        }
        Assert.Equal(160m, await BalanceAsync(player));
        await using (var db = _testDb.NewContext())
        {
            var order = await db.BarOrders.AsNoTracking().SingleAsync(o => o.Id == id);
            var sale = await db.Sales.AsNoTracking().SingleAsync(s => s.Id == order.SaleId);
            Assert.Equal((PaymentMethods.Balance, 140m, player), (sale.Method, sale.Total, sale.UserId!.Value));
            Assert.Equal(3, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == _cola)).StockQuantity);
            Assert.Empty(await Stock.ReconcileAsync(db));
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.Null(await Ledger.CheckChainAsync(db, player));
            var holds = await Holds.OrdersAsync(db);
            Assert.True(holds.Ok);
            Assert.Equal((0m, 0), (holds.Held, holds.Open));
            var z = await ZReport.BuildAsync(db, await db.Shifts.AsNoTracking().SingleAsync(), _clock);
            Assert.True(z.Ledger.Reconciled);
            Assert.Equal(140m, z.Bar.Sales.Balance);
            Assert.Equal((140m, 140m), (z.Holds.OrdersHeld, z.Holds.OrdersReleased));
            _clock.Time().Advance(TimeSpan.FromMinutes(1)); // the period ends at "now": the rows of this frozen clock are at "now"
            var report = await AnalyticsService.BuildAsync(db, AnalyticsPeriod.Resolve("today", null, null, _clock)!, _clock);
            Assert.Equal(140m, report.Revenue.Bar);
            Assert.True(report.Check.Reconciled && report.Check.ShiftsAddUp);
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("order.")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
            Assert.Equal(new[] { AuditActions.OrderPlaced, AuditActions.OrderDelivered }, audit);
        }
    }

    [Fact]
    public async Task Cancel_refunds_a_prepaid_order_and_a_desk_order_is_paid_at_the_delivery()
    {
        var player = await PlayerSessionAsync(_pc1, 100m);
        await GuestSessionAsync(_pc2);
        await using var db = _testDb.NewShiftContext();
        var prepaid = (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_chips, 1) }, "balance")).Value!;
        Assert.Equal(40m, await BalanceAsync(player));
        Assert.True((await Orders(db).CancelAsync(prepaid.Id, null, byPlayer: true)).Ok);
        Assert.Equal(100m, await BalanceAsync(player));
        Assert.Equal("order_not_open", (await Orders(db).CancelAsync(prepaid.Id, "x")).Error!.Code);

        // A guest pays at the desk: no balance; cash or card at the delivery needs an open shift
        Assert.Equal("order_balance_needs_player", (await Orders(db).PlaceAsync(_pc2, new[] { new OrderLine(_coffee, 1) }, "balance")).Error!.Code);
        var desk = (await Orders(db).PlaceAsync(_pc2, new[] { new OrderLine(_coffee, 2) }, "desk")).Value!;
        Assert.Equal("order_desk_method", (await Orders(db).DeliverAsync(desk.Id, null)).Error!.Code);
        db.ChangeTracker.Clear();
        Assert.Equal("shift_required", (await Orders(db).DeliverAsync(desk.Id, "card")).Error!.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(BarOrderStatuses.New, (await db.BarOrders.AsNoTracking().SingleAsync(o => o.Id == desk.Id)).Status); // rolled back
        Assert.True((await new ShiftService(db, _clock, actor: new StaffActor(null, "cashier")).OpenAsync(0m, null)).Ok);
        db.ChangeTracker.Clear();
        Assert.True((await Orders(db).DeliverAsync(desk.Id, "card")).Ok);
        var sale = await db.Sales.AsNoTracking().SingleAsync();
        Assert.Equal((PaymentMethods.Card, 100m, (Guid?)null), (sale.Method, sale.Total, sale.UserId));
        Assert.True((await Holds.OrdersAsync(db)).Ok);
        Assert.Empty(await Ledger.ReconcileAsync(db));
    }

    [Fact]
    public async Task Limits_one_open_order_the_most_per_order_stock_and_a_session()
    {
        await using var db = _testDb.NewShiftContext();
        Assert.Equal("order_needs_session", (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_coffee, 1) }, "desk")).Error!.Code);
        var player = await PlayerSessionAsync(_pc1, 2000m);
        Assert.Equal("order_bad_lines", (await Orders(db).PlaceAsync(_pc1, Array.Empty<OrderLine>(), "desk")).Error!.Code);
        Assert.Equal("order_bad_lines", (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_coffee, 11) }, "desk")).Error!.Code);
        Assert.Equal("out_of_stock", (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_chips, 3) }, "desk")).Error!.Code);
        Assert.Equal("order_too_much", (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_coffee, 10), new OrderLine(_cola, 1) }, "desk")).Error!.Code);
        await BarOrderSettings.SaveAsync(db, BarOrderSettings.Default with { Enabled = false }, "manager", _clock.UtcNow);
        Assert.Equal("orders_disabled", (await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_coffee, 1) }, "desk")).Error!.Code);
        Assert.False((await Orders(db).MenuAsync(_pc1)).Available);
        await BarOrderSettings.SaveAsync(db, BarOrderSettings.Default, "manager", _clock.UtcNow);
        Assert.True((await Orders(db).PlaceAsync(_pc1, new[] { new OrderLine(_chips, 2) }, "desk")).Ok);

        // Sold out before the delivery: refused, the order stays open; the staff cancel it
        await db.Products.Where(p => p.Id == _chips).ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, 1));
        var order = await db.BarOrders.AsNoTracking().SingleAsync();
        db.ChangeTracker.Clear();
        Assert.True((await new ShiftService(db, _clock, actor: new StaffActor(null, "cashier")).OpenAsync(0m, null)).Ok);
        Assert.Equal("out_of_stock", (await Orders(db).DeliverAsync(order.Id, "cash")).Error!.Code);
        db.ChangeTracker.Clear();
        Assert.True((await Orders(db).CancelAsync(order.Id, "закінчились")).Ok);
        Assert.Empty(await db.Sales.ToListAsync());
    }
}

/// <summary>Two lock screens of one PC order at the same moment: one open order (the database index decides).</summary>
public abstract class BarOrderConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;
    protected BarOrderConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);
    public void Dispose() { if (_db.IsValueCreated) _db.Value.Dispose(); }

    protected async Task Parallel_orders_of_one_pc_create_one_order()
    {
        Guid pc, user, coffee;
        await using (var db = _db.Value.NewContext())
        {
            var tariff = new TariffEntity { Id = Guid.NewGuid(), Name = "Std", PricePerHour = 60m, IsDefault = true };
            var device = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-01", DeviceKey = "sha256:" + Guid.NewGuid(), Status = "approved" };
            var u = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P" };
            var product = new ProductEntity { Id = Guid.NewGuid(), Name = "Кава", Category = "Кава", Price = 50m, CreatedUtc = DateTimeOffset.UtcNow };
            db.AddRange(tariff, device, u, product);
            await db.SaveChangesAsync();
            await Wallet.CreditAsync(db, u.Id, 1000m);
            await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = u.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = 1000m, BalanceDelta = 1000m }, null, DateTimeOffset.UtcNow);
            Assert.True((await new SessionService(db, new EfDeviceRepository(db)).StartAsync(new StartSessionRequest(device.Id, null, u.Id, 120, PaymentMethod: "balance"))).Ok);
            (pc, user, coffee) = (device.Id, u.Id, product.Id);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = _db.Value.NewContext();
            try { return (await new BarOrderService(db).PlaceAsync(pc, new[] { new OrderLine(coffee, 1) }, "balance")).Ok; }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException or NpgsqlException) { return false; }
        }));
        Assert.Equal(1, results.Count(ok => ok));
        await using var check = _db.Value.NewContext();
        Assert.Equal(1, await check.BarOrders.CountAsync());
        Assert.Equal(1000m - 120m - 50m, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == user)).Balance); // one hold only
        Assert.Empty(await Ledger.ReconcileAsync(check));
        Assert.True((await Holds.OrdersAsync(check)).Ok);
    }
}

public sealed class SqliteBarOrderConcurrencyTests : BarOrderConcurrencyTestsBase
{
    public SqliteBarOrderConcurrencyTests() : base(() => new SqliteFileDb()) { }
    [Fact] public Task One_open_order() => Parallel_orders_of_one_pc_create_one_order();
}

public sealed class PostgresBarOrderConcurrencyTests : BarOrderConcurrencyTestsBase
{
    public PostgresBarOrderConcurrencyTests() : base(() => new PostgresDb()) { }
    [PostgresFact] public Task One_open_order() => Parallel_orders_of_one_pc_create_one_order();
}

/// <summary>Orders through the API: the agent of the PC orders for its own PC; the front desk handles them; roles.</summary>
public sealed class BarOrderApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public BarOrderApiTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task The_lock_screen_orders_and_the_desk_delivers()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = (await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Ord " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "T " + Guid.NewGuid().ToString("N")[..4], pricePerHour = 60, zoneId = zone }));
        var pc = await TestPc.AddAsync(_factory, zone);
        var other = await TestPc.AddAsync(_factory, zone);
        var product = await JsonAsync(await owner.PostAsJsonAsync("/api/products", new { name = "Монстр " + Guid.NewGuid().ToString("N")[..4], category = "Енергетики", price = 75, trackStock = false }));
        var productId = product.GetProperty("id").GetGuid();
        var player = await PlayerAsync(owner);
        await JsonAsync(await owner.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 300, method = "cash" }));
        await JsonAsync(await owner.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, userId = player, durationMinutes = 60 }));

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/devices/{pc.Id}/bar-menu")).StatusCode);
        var menu = await JsonAsync(await pc.GetAsync($"/api/devices/{pc.Id}/bar-menu"));
        Assert.True(menu.GetProperty("available").GetBoolean() && menu.GetProperty("canPayFromBalance").GetBoolean());
        var placed = await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/orders", new { items = new[] { new { productId, quantity = 2 } }, payment = "balance" }));
        var id = placed.GetProperty("orderId").GetInt64();
        Assert.Equal("new", (await pc.HeartbeatAsync()).GetProperty("session").GetProperty("order").GetProperty("status").GetString());

        // Staff: the queue with the badge; Tech has no orders
        var tech = await StaffAsync("Tech");
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/orders")).StatusCode);
        var cashier = await StaffAsync("Cashier");
        var queue = await JsonAsync(await cashier.GetAsync("/api/orders"));
        Assert.True(queue.GetProperty("newCount").GetInt32() >= 1);
        Assert.Contains(queue.GetProperty("open").EnumerateArray(), o => o.GetProperty("id").GetInt64() == id && o.GetProperty("hostname").GetString() == pc.Hostname);
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/orders/{id}/preparing", new { }));
        Assert.Equal("order_preparing", await CodeAsync(await pc.PostAsync($"/api/devices/{pc.Id}/orders/cancel")));
        var delivered = await JsonAsync(await cashier.PostAsJsonAsync($"/api/orders/{id}/deliver", new { }));
        Assert.Equal("delivered", delivered.GetProperty("status").GetString());
        Assert.Equal("delivered", (await pc.HeartbeatAsync()).GetProperty("session").GetProperty("order").GetProperty("status").GetString());

        // The settings: Owner and Manager (audited)
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/orders/settings", new { enabled = true, maxAmount = 300 })).StatusCode);
        await JsonAsync(await (await StaffAsync("Manager")).PutAsJsonAsync("/api/orders/settings", new { enabled = true, maxAmount = 300 }));
        Assert.Equal("order_too_much", await CodeAsync(await pc.PostAsync($"/api/devices/{pc.Id}/orders", new { items = new[] { new { productId, quantity = 5 } }, payment = "desk" })));
        await JsonAsync(await owner.PutAsJsonAsync("/api/orders/settings", new { enabled = true, maxAmount = 500 }));
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=order.settings_changed")).EnumerateArray(), a => a.GetProperty("summary").GetString()!.Contains("500"));
        var holds = await JsonAsync(await owner.GetAsync("/api/ledger/holds"));
        Assert.True(holds.GetProperty("orders").GetProperty("ok").GetBoolean());
    }
}
