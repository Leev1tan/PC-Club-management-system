using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// Shifts with the default club settings (cash and card need an open shift): the rule, the expected
/// cash, the variance, blind close for cashiers, payouts and the role checks. The API tests share one
/// server, so each test closes the shifts it opens.
/// </summary>
[Collection(ShiftCollection.Name)]
public sealed class ShiftApiTests : ApiTestBase
{
    public ShiftApiTests(ShiftFactory factory) : base(factory) { }

    private async Task CloseAnyOpenShiftAsync()
    {
        var owner = await _factory.LoginOwnerAsync();
        var current = await owner.GetFromJsonAsync<JsonElement>("/api/shifts/current");
        if (current.GetProperty("shift").ValueKind == JsonValueKind.Object)
            await JsonAsync(await owner.PostAsJsonAsync($"/api/shifts/{current.GetProperty("shift").GetProperty("id").GetInt64()}/close", new { countedCash = 0 }));
    }

    private async Task<(Guid Zone, Guid Pc)> HallAsync(HttpClient owner)
    {
        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Shift hall " + Guid.NewGuid().ToString("N")[..4] }));
        var zoneId = Guid.Parse(zone.GetProperty("id").GetString()!);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Shift tariff", pricePerHour = 60, isDefault = false, zoneId }));
        return (zoneId, await AddDeviceAsync(zoneId));
    }

    [Fact]
    public async Task Cash_and_card_need_an_open_shift_and_balance_payments_do_not()
    {
        await CloseAnyOpenShiftAsync();
        var owner = await _factory.LoginOwnerAsync();
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var (_, pc) = await HallAsync(owner);
        var player = await PlayerAsync(cashier);

        var cash = await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 100, method = "cash" });
        Assert.Equal((HttpStatusCode.Conflict, "shift_required"), (cash.StatusCode, await CodeAsync(cash)));
        var card = await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 100, method = "card" });
        Assert.Equal((HttpStatusCode.Conflict, "shift_required"), (card.StatusCode, await CodeAsync(card)));
        var guest = await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, durationMinutes = 30, paymentMethod = "cash" });
        Assert.Equal("shift_required", await CodeAsync(guest));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            Assert.Equal(0m, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(player))).Balance); // rolled back
            Assert.False(await db.Sessions.AnyAsync(s => s.DeviceId == pc));
        }

        var current = await cashier.GetFromJsonAsync<JsonElement>("/api/shifts/current");
        Assert.True(current.GetProperty("requireOpenShift").GetBoolean());
        var shift = await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat = 500 }));
        var shiftId = shift.GetProperty("id").GetInt64();
        var again = await owner.PostAsJsonAsync("/api/shifts", new { openingFloat = 100 });
        Assert.Equal((HttpStatusCode.Conflict, "shift_already_open"), (again.StatusCode, await CodeAsync(again)));

        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 100, method = "cash" }));
        // Balance payment: allowed with and without a shift; the row belongs to the open shift
        var session = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, userId = player, durationMinutes = 30 }));
        var rows = (await owner.GetFromJsonAsync<JsonElement>($"/api/ledger?userId={player}")).EnumerateArray().ToList();
        Assert.All(rows, r => Assert.Equal(shiftId, r.GetProperty("shiftId").GetInt64()));

        await JsonAsync(await cashier.PostAsJsonAsync($"/api/shifts/{shiftId}/close", new { countedCash = 600 }));
        await JsonAsync(await cashier.PostAsync($"/api/sessions/{session.GetProperty("id").GetString()}/end", null));
        var after = await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 50 });
        Assert.Equal("shift_required", await CodeAsync(after));
    }

    [Fact]
    public async Task Close_computes_expected_cash_and_the_variance_and_the_shift_is_final()
    {
        await CloseAnyOpenShiftAsync();
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var other = await StaffAsync(StaffRoles.Cashier);
        var (_, pc) = await HallAsync(owner);
        var player = await PlayerAsync(cashier);

        var shift = await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat = 500, note = "Ранкова зміна" }));
        var id = shift.GetProperty("id").GetInt64();
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 200, method = "cash" }));   // +200
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 300, method = "card" }));   // card: not in the drawer
        var guest = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, durationMinutes = 60, paymentMethod = "cash" }));
        var sessionCash = Dec(guest, "totalCost");                                                                              // +60
        await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/refund", new { amount = 50, method = "cash", reason = "Гравець пішов" })); // -50

        // Payouts: Owner and Manager only, with a reason, never more than the drawer holds
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/shifts/{id}/payout", new { amount = 100, reason = "Закупівля" })).StatusCode);
        Assert.Equal("reason_required", await CodeAsync(await manager.PostAsJsonAsync($"/api/shifts/{id}/payout", new { amount = 100 })));
        var tooMuch = await manager.PostAsJsonAsync($"/api/shifts/{id}/payout", new { amount = 5000, reason = "Інкасація" });
        Assert.Equal((HttpStatusCode.Conflict, "cash_too_low"), (tooMuch.StatusCode, await CodeAsync(tooMuch)));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/shifts/{id}/payout", new { amount = 100, reason = "Закупівля води" }));   // -100
        var expected = 500m + 200m + sessionCash - 50m - 100m;

        // Blind close: the cashier does not see the expected cash before the count; the manager does
        var blind = await cashier.GetFromJsonAsync<JsonElement>("/api/shifts/current");
        Assert.True(blind.GetProperty("blind").GetBoolean());
        Assert.Equal(JsonValueKind.Null, blind.GetProperty("expectedCash").ValueKind);
        var live = await manager.GetFromJsonAsync<JsonElement>("/api/shifts/current");
        Assert.Equal(expected, Dec(live, "expectedCash"));

        // Only the opener, a manager or the owner closes it
        var notYours = await other.PostAsJsonAsync($"/api/shifts/{id}/close", new { countedCash = expected });
        Assert.Equal((HttpStatusCode.Forbidden, "shift_not_yours"), (notYours.StatusCode, await CodeAsync(notYours)));
        Assert.Equal("bad_counted", await CodeAsync(await cashier.PostAsJsonAsync($"/api/shifts/{id}/close", new { countedCash = -1 })));
        var closed = await JsonAsync(await cashier.PostAsJsonAsync($"/api/shifts/{id}/close", new { countedCash = expected - 20m, note = "Не вистачає 20" }));
        Assert.Equal((expected, expected - 20m, -20m), (Dec(closed, "expectedCash"), Dec(closed, "countedCash"), Dec(closed, "variance")));
        Assert.Equal("closed", closed.GetProperty("status").GetString());

        // A closed shift is final for everybody: no second close, no payout, no edit endpoint
        Assert.Equal("shift_closed", await CodeAsync(await owner.PostAsJsonAsync($"/api/shifts/{id}/close", new { countedCash = expected })));
        Assert.Equal("shift_closed", await CodeAsync(await owner.PostAsJsonAsync($"/api/shifts/{id}/payout", new { amount = 1, reason = "late" })));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PutAsJsonAsync($"/api/shifts/{id}", new { status = "open" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.DeleteAsync($"/api/shifts/{id}")).StatusCode);

        // Lists: the cashier sees own shifts, the manager all of them; the audit log has open, payout, close
        var mine = (await cashier.GetFromJsonAsync<JsonElement>("/api/shifts")).EnumerateArray().ToList();
        Assert.Contains(mine, s => s.GetProperty("id").GetInt64() == id);
        Assert.DoesNotContain((await other.GetFromJsonAsync<JsonElement>("/api/shifts")).EnumerateArray(), s => s.GetProperty("id").GetInt64() == id);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/shifts/{id}")).StatusCode);
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=shift.&take=200")).EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == id.ToString()).Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(new[] { AuditActions.ShiftClosed, AuditActions.ShiftCashPayout, AuditActions.ShiftOpened }, audit);
    }

    [Fact]
    public async Task Tech_has_no_shift_access()
    {
        var tech = await StaffAsync(StaffRoles.Tech);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/shifts/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync("/api/shifts", new { openingFloat = 0 })).StatusCode);
    }
}

[CollectionDefinition(Name)]
public sealed class ShiftCollection : ICollectionFixture<ShiftFactory>
{
    public const string Name = "Club with shifts";
}

/// <summary>Payments that race with the close of the shift: each one is either counted in the shift or refused.</summary>
public abstract class ShiftConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;

    protected ShiftConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    private CmsDbContext Ctx() => _db.Value.NewContext();

    protected async Task Payments_during_the_close_are_counted_or_refused()
    {
        var actor = new StaffActor(Guid.NewGuid(), "cashier");
        long shiftId;
        await using (var db = Ctx())
            shiftId = (await new ShiftService(db, actor: actor).OpenAsync(1000m, null)).Value!.Id;

        // 20 cash payments of 10 ₴ race with the close; with no open shift a cash row is refused
        async Task<bool> TopUpAsync()
        {
            await using var db = new CmsDbContext(OptionsOf(Ctx()), new ShiftOptions { RequireOpenShift = true });
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await Ledger.AppendAsync(db, new LedgerEntryEntity { Type = LedgerTypes.SessionCharge, Method = PaymentMethods.Cash, Amount = 10m, DeskAmount = 10m }, actor, DateTimeOffset.UtcNow);
                await tx.CommitAsync();
                return true;
            }
            catch (ShiftRequiredException)
            {
                return false;
            }
        }
        var payments = Enumerable.Range(0, 20).Select(_ => Task.Run(TopUpAsync)).ToList();
        await Task.Delay(5);
        BillingResult<ShiftEntity> close;
        await using (var db = Ctx())
            close = await new ShiftService(db, actor: actor).CloseAsync(shiftId, 1000m, null, isManager: false);
        var accepted = (await Task.WhenAll(payments)).Count(ok => ok);

        await using var check = Ctx();
        var counted = await check.Ledger.AsNoTracking().Where(l => l.ShiftId == shiftId).Select(l => l.DeskAmount).ToListAsync();
        Assert.True(close.Ok);
        Assert.Equal(1000m + counted.Sum(), close.Value!.ExpectedCash);       // nothing was added after the close
        Assert.Equal(accepted, counted.Count);                                  // every accepted payment is in the shift
        Assert.Equal(0, await check.Ledger.CountAsync(l => l.ShiftId == null)); // no cash row without a shift
    }

    private static DbContextOptions<CmsDbContext> OptionsOf(CmsDbContext db)
    {
        var options = db.GetService<IDbContextOptions>();
        db.Dispose();
        return (DbContextOptions<CmsDbContext>)options;
    }
}

public sealed class SqliteShiftConcurrencyTests : ShiftConcurrencyTestsBase
{
    public SqliteShiftConcurrencyTests() : base(() => new SqliteFileDb()) { }

    [Fact]
    public Task Payments_during_the_close_are_counted_or_refused_on_sqlite() => Payments_during_the_close_are_counted_or_refused();
}

public sealed class PostgresShiftConcurrencyTests : ShiftConcurrencyTestsBase
{
    public PostgresShiftConcurrencyTests() : base(() => new PostgresDb()) { }

    [PostgresFact]
    public Task Payments_during_the_close_are_counted_or_refused_on_postgres() => Payments_during_the_close_are_counted_or_refused();
}
