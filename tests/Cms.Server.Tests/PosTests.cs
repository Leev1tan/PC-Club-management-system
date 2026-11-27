using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Pos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Bar catalog, checkout, receipts, refunds and the stock log through the HTTP API (default club settings).</summary>
[Collection(ShiftCollection.Name)]
public sealed class PosApiTests : ApiTestBase
{
    public PosApiTests(ShiftFactory factory) : base(factory) { }

    private async Task<long> OpenShiftAsync(HttpClient cashier, decimal openingFloat = 500m)
    {
        var owner = await _factory.LoginOwnerAsync();
        var current = await owner.GetFromJsonAsync<JsonElement>("/api/shifts/current");
        if (current.GetProperty("shift").ValueKind == JsonValueKind.Object)
            await JsonAsync(await owner.PostAsJsonAsync($"/api/shifts/{current.GetProperty("shift").GetProperty("id").GetInt64()}/close", new { countedCash = 0 }));
        return (await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat }))).GetProperty("id").GetInt64();
    }

    private static object Product(string name, decimal price, bool track = true, int? stock = null, int? low = null, string category = "Енергетики") =>
        new { name, category, price, trackStock = track, initialStock = stock, lowStockThreshold = low };

    private async Task<string> TopUpAsync(HttpClient cashier, decimal amount)
    {
        var player = await PlayerAsync(cashier);
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount, method = "card" }));
        return player;
    }

    [Fact]
    public async Task Managers_edit_the_catalog_and_price_changes_are_audited()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/products", Product("Red Bull", 70m))).StatusCode);
        Assert.Equal("bad_product", await CodeAsync(await manager.PostAsJsonAsync("/api/products", Product("Free", 0m))));
        var redBull = await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Red Bull 0,25", 70m, stock: 24, low: 6)));
        var id = redBull.GetProperty("id").GetString();
        Assert.Equal((24, false), (redBull.GetProperty("stockQuantity").GetInt32(), redBull.GetProperty("lowStock").GetBoolean()));

        var changed = await JsonAsync(await manager.PutAsJsonAsync($"/api/products/{id}", Product("Red Bull 0,25", 75m, low: 6)));
        Assert.Equal((75m, 24), (Dec(changed, "price"), changed.GetProperty("stockQuantity").GetInt32())); // PUT never changes the stock
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/products/{id}", Product("Red Bull 0,25", 1m))).StatusCode);
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=product.price_changed&take=50")).EnumerateArray()
            .Single(e => e.GetProperty("targetId").GetString() == id);
        Assert.Contains("70 -> 75", audit.GetProperty("summary").GetString());

        // Off sale: hidden from the checkout, not sellable; the editor still sees it
        await JsonAsync(await manager.PutAsJsonAsync($"/api/products/{id}", new { name = "Red Bull 0,25", category = "Енергетики", price = 75, isActive = false }));
        Assert.DoesNotContain((await cashier.GetFromJsonAsync<JsonElement>("/api/products")).EnumerateArray(), p => p.GetProperty("id").GetString() == id);
        Assert.Contains((await manager.GetFromJsonAsync<JsonElement>("/api/products?includeInactive=true")).EnumerateArray(), p => p.GetProperty("id").GetString() == id);
        await OpenShiftAsync(cashier);
        var inactive = await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = id, quantity = 1 } }, paymentMethod = "cash" });
        Assert.Equal("product_inactive", await CodeAsync(inactive));
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/products")).StatusCode);

        // Delete: only without history
        Assert.Equal("product_in_use", await CodeAsync(await manager.DeleteAsync($"/api/products/{id}")));
        var fresh = await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Тест", 10m, track: false)));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/products/{fresh.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Checkout_takes_cash_card_or_the_real_balance_and_the_stock()
    {
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var monster = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Monster 0,5", 75m, stock: 10, low: 3)))).GetProperty("id").GetString();
        var coffee = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Капучино", 55m, track: false, category: "Кава")))).GetProperty("id").GetString();
        var shift = await OpenShiftAsync(cashier, 500m);

        // Cash: 2 x 75 + 55 = 205; the stock of Monster goes down, coffee is not counted
        var sale = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new
        {
            items = new object[] { new { productId = monster, quantity = 1 }, new { productId = coffee, quantity = 1 }, new { productId = monster, quantity = 1 } },
            paymentMethod = "cash"
        }));
        Assert.Equal((205m, "cash", shift), (Dec(sale, "total"), sale.GetProperty("method").GetString(), sale.GetProperty("shiftId").GetInt64()));
        Assert.Equal(2, sale.GetProperty("items").GetArrayLength()); // the two Monster lines are merged
        var stock = await cashier.GetFromJsonAsync<JsonElement>($"/api/products/{monster}");
        Assert.Equal(8, stock.GetProperty("stockQuantity").GetInt32());

        // Balance: the real balance only, bonus never pays at the bar
        var player = await TopUpAsync(cashier, 100m);
        await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/adjust", new { amount = 50, reason = "Бонус за турнір", target = "bonus" }));
        var fromBalance = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = monster, quantity = 1 } }, userId = player }));
        Assert.Equal("balance", fromBalance.GetProperty("method").GetString());
        var user = await cashier.GetFromJsonAsync<JsonElement>($"/api/users/{player}");
        Assert.Equal((25m, 50m), (Dec(user, "balance"), Dec(user, "bonusPoints")));
        var tooLow = await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = coffee, quantity = 1 } }, userId = player, paymentMethod = "balance" });
        Assert.Equal((HttpStatusCode.Conflict, "insufficient_balance_bar"), (tooLow.StatusCode, await CodeAsync(tooLow)));

        // Rules: no more than the stock, guests pay at the desk, quantities 1-100
        var outOfStock = await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = monster, quantity = 8 } }, paymentMethod = "card" });
        Assert.Equal("out_of_stock", await CodeAsync(outOfStock));
        Assert.Equal("guest_needs_desk", await CodeAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = coffee, quantity = 1 } }, paymentMethod = "balance" })));
        Assert.Equal("bad_quantity", await CodeAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = coffee, quantity = 0 } } })));
        Assert.Equal("empty_sale", await CodeAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = Array.Empty<object>() })));
        var card = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = monster, quantity = 5 } }, paymentMethod = "card" }));
        var low = await cashier.GetFromJsonAsync<JsonElement>($"/api/products/{monster}");
        Assert.Equal((2, true), (low.GetProperty("stockQuantity").GetInt32(), low.GetProperty("lowStock").GetBoolean()));
        Assert.Contains((await cashier.GetFromJsonAsync<JsonElement>("/api/products?lowStockOnly=true")).EnumerateArray(), p => p.GetProperty("id").GetString() == monster);

        // One receipt log with the ledger rows behind it
        var receipts = (await cashier.GetFromJsonAsync<JsonElement>($"/api/sales?shiftId={shift}")).EnumerateArray().ToList();
        Assert.Equal(3, receipts.Count);
        var owner = await _factory.LoginOwnerAsync();
        var rows = (await owner.GetFromJsonAsync<JsonElement>("/api/ledger?type=bar_sale&take=500")).EnumerateArray()
            .Where(r => r.GetProperty("shiftId").GetInt64() == shift).ToList();
        Assert.Equal(new[] { 75m * 5, 0m, 205m }, rows.Select(r => Dec(r, "deskAmount")));
        Assert.Equal(new[] { 0m, -75m, 0m }, rows.Select(r => Dec(r, "balanceDelta")));
        Assert.All(rows, r => Assert.NotEqual(JsonValueKind.Null, r.GetProperty("saleId").ValueKind));
        var check = await owner.GetFromJsonAsync<JsonElement>("/api/products/stock/check");
        Assert.True(check.GetProperty("ok").GetBoolean(), check.ToString());
        Assert.Equal(card.GetProperty("id").GetInt64(), receipts[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Refunds_need_a_manager_and_a_reason_and_pay_back_the_same_way()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var snickers = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Snickers", 40m, stock: 20, category: "Снеки")))).GetProperty("id").GetString();
        var shift = await OpenShiftAsync(cashier, 300m);
        var cash = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = snickers, quantity = 3 } }, paymentMethod = "cash" }));
        var player = await TopUpAsync(cashier, 200m);
        var balance = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = snickers, quantity = 2 } }, userId = player }));
        var cashId = cash.GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/sales/{cashId}/refund", new { reason = "Не та позиція" })).StatusCode);
        Assert.Equal("reason_required", await CodeAsync(await manager.PostAsJsonAsync($"/api/sales/{cashId}/refund", new { reason = "" })));
        var refund = await JsonAsync(await manager.PostAsJsonAsync($"/api/sales/{cashId}/refund", new { reason = "Батончик зіпсований" }));
        Assert.Equal(("refund", cashId, 120m), (refund.GetProperty("kind").GetString(), refund.GetProperty("refundOfSaleId").GetInt64(), Dec(refund, "total")));
        Assert.Equal("sale_already_refunded", await CodeAsync(await manager.PostAsJsonAsync($"/api/sales/{cashId}/refund", new { reason = "ще раз" })));
        Assert.Equal("not_a_sale", await CodeAsync(await manager.PostAsJsonAsync($"/api/sales/{refund.GetProperty("id").GetInt64()}/refund", new { reason = "чек повернення" })));
        var refunded = await cashier.GetFromJsonAsync<JsonElement>($"/api/sales/{cashId}");
        Assert.Equal(refund.GetProperty("id").GetInt64(), refunded.GetProperty("refundedBySaleId").GetInt64());

        // Balance receipt: back to the real balance; this time the goods do not go back to the stock
        await JsonAsync(await manager.PostAsJsonAsync($"/api/sales/{balance.GetProperty("id").GetInt64()}/refund", new { reason = "Відкрита упаковка", returnToStock = false }));
        Assert.Equal(200m, Dec(await cashier.GetFromJsonAsync<JsonElement>($"/api/users/{player}"), "balance"));
        Assert.Equal(18, (await cashier.GetFromJsonAsync<JsonElement>($"/api/products/{snickers}")).GetProperty("stockQuantity").GetInt32()); // 20 - 3 + 3 - 2

        var refunds = (await owner.GetFromJsonAsync<JsonElement>("/api/ledger?type=bar_refund&take=500")).EnumerateArray()
            .Where(r => r.GetProperty("shiftId").GetInt64() == shift).ToList();
        Assert.Equal(new[] { (0m, 80m), (-120m, 0m) }, refunds.Select(r => (Dec(r, "deskAmount"), Dec(r, "balanceDelta"))));
        Assert.Equal(300m, Dec(await manager.GetFromJsonAsync<JsonElement>("/api/shifts/current"), "expectedCash")); // 300 + 120 - 120
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=sale.refunded&take=50")).EnumerateArray()
            .Single(e => e.GetProperty("targetId").GetString() == cashId.ToString());
        Assert.Contains("Батончик зіпсований", audit.GetProperty("summary").GetString());
        Assert.Contains("Snickers x3", audit.GetProperty("detailsJson").GetString());
    }

    [Fact]
    public async Task Restocks_are_logged_and_corrections_need_a_manager_and_a_reason()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var water = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Моршинська 0,5", 25m, stock: 5, low: 6, category: "Вода")))).GetProperty("id").GetString();
        var hotdog = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", Product("Хот-дог", 65m, track: false, category: "Снеки")))).GetProperty("id").GetString();

        Assert.True((await cashier.GetFromJsonAsync<JsonElement>($"/api/products/{water}")).GetProperty("lowStock").GetBoolean());
        var restock = await JsonAsync(await cashier.PostAsJsonAsync($"/api/products/{water}/restock", new { quantity = 24, unitCost = 14.5, note = "Накладна 118" }));
        Assert.Equal((24, 29), (restock.GetProperty("delta").GetInt32(), restock.GetProperty("quantityAfter").GetInt32()));
        Assert.Equal("stock_not_tracked", await CodeAsync(await cashier.PostAsJsonAsync($"/api/products/{hotdog}/restock", new { quantity = 5 })));
        Assert.Equal("bad_stock_quantity", await CodeAsync(await cashier.PostAsJsonAsync($"/api/products/{water}/restock", new { quantity = 0 })));

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/products/{water}/correction", new { countedQuantity = 27, reason = "Перерахунок" })).StatusCode);
        Assert.Equal("reason_required", await CodeAsync(await manager.PostAsJsonAsync($"/api/products/{water}/correction", new { countedQuantity = 27 })));
        var correction = await JsonAsync(await manager.PostAsJsonAsync($"/api/products/{water}/correction", new { countedQuantity = 27, reason = "Перерахунок: 2 пляшки розбиті" }));
        Assert.Equal((-2, 27), (correction.GetProperty("delta").GetInt32(), correction.GetProperty("quantityAfter").GetInt32()));

        var log = (await cashier.GetFromJsonAsync<JsonElement>($"/api/products/movements?productId={water}")).EnumerateArray().ToList();
        Assert.Equal(new[] { "correction", "restock", "restock" }, log.Select(m => m.GetProperty("type").GetString()));
        Assert.All(log, m => Assert.False(string.IsNullOrEmpty(m.GetProperty("actorUsername").GetString())));
        Assert.Equal("Накладна 118", log[1].GetProperty("reason").GetString());
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/products/stock/check")).GetProperty("ok").GetBoolean());
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=stock.&take=100")).EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == water).Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(new[] { AuditActions.StockCorrected, AuditActions.StockRestocked }, audit);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/products/stock/check")).StatusCode);
    }
}

/// <summary>Parallel sales of the last units: the stock never goes below zero and every unit is sold once.</summary>
public abstract class PosConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;

    protected PosConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    protected async Task Parallel_sales_never_sell_more_than_the_stock()
    {
        var product = new ProductEntity { Id = Guid.NewGuid(), Name = "Burn", Category = "Енергетики", Price = 55m, TrackStock = true, CreatedUtc = DateTimeOffset.UtcNow };
        var player = new UserEntity { Id = Guid.NewGuid(), Username = "pos" + Guid.NewGuid().ToString("N")[..6], DisplayName = "P", Balance = 110m };
        await using (var db = _db.Value.NewContext())
        {
            db.AddRange(product, player);
            db.Ledger.Add(new LedgerEntryEntity { CreatedUtc = DateTimeOffset.UtcNow, UserId = player.Id, Type = LedgerTypes.OpeningBalance, Method = PaymentMethods.None,
                Amount = 110m, BalanceDelta = 110m, BalanceAfter = 110m, BonusAfter = 0m });
            await db.SaveChangesAsync();
            await Stock.AddAsync(db, product.Id, 3);
            await Stock.RecordAsync(db, product.Id, StockMovementTypes.Restock, 3, null, DateTimeOffset.UtcNow);
        }

        // 8 sales of one can for 3 cans; half of them from a balance that pays for two
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var db = _db.Value.NewContext();
            var r = await new SaleService(db).SellAsync(new[] { new SaleLine(product.Id, 1) }, i % 2 == 0 ? PaymentMethods.Cash : PaymentMethods.Balance, i % 2 == 0 ? null : player.Id);
            return r.Ok;
        })));

        await using var check = _db.Value.NewContext();
        Assert.Equal(3, results.Count(ok => ok));
        Assert.Equal(0, (await check.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id)).StockQuantity);
        Assert.Empty(await Stock.ReconcileAsync(check));
        Assert.Empty(await Ledger.ReconcileAsync(check));
        Assert.True((await check.Users.AsNoTracking().SingleAsync(u => u.Id == player.Id)).Balance >= 0m);
        Assert.Equal(3, await check.Sales.CountAsync());
        Assert.Equal(3, await check.Ledger.CountAsync(l => l.Type == LedgerTypes.BarSale));
    }
}

public sealed class SqlitePosConcurrencyTests : PosConcurrencyTestsBase
{
    public SqlitePosConcurrencyTests() : base(() => new SqliteFileDb()) { }

    [Fact]
    public Task Parallel_sales_never_sell_more_than_the_stock_on_sqlite() => Parallel_sales_never_sell_more_than_the_stock();
}

public sealed class PostgresPosConcurrencyTests : PosConcurrencyTestsBase
{
    public PostgresPosConcurrencyTests() : base(() => new PostgresDb()) { }

    [PostgresFact]
    public Task Parallel_sales_never_sell_more_than_the_stock_on_postgres() => Parallel_sales_never_sell_more_than_the_stock();
}
