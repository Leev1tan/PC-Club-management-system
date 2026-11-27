using System.Text.RegularExpressions;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Events;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// Money in the stored texts has two decimals on both databases: PostgreSQL returns 45.00 for numeric(10,2), SQLite 45.0
/// for its text column, a C# literal 45; the audit summaries and ledger descriptions must still read "45.00 ₴".
/// </summary>
public sealed class MoneyTextTests
{
    private static readonly Regex Amount = new(@"(?<![\w.])[+-]?\d+(?:\.\d+)? (?:₴|€|\$|zł)", RegexOptions.Compiled);

    /// <summary>The same money operations on a fresh database; returns the audit summaries and ledger descriptions in order.</summary>
    private static async Task<List<string>> ScenarioAsync(IParallelDb store)
    {
        using var _ = store;
        await using var db = store.NewContext();
        var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = "Standard" };
        db.Zones.Add(zone);
        db.Tariffs.Add(new TariffEntity { Id = Guid.NewGuid(), Name = "T", PricePerHour = 45m, ZoneId = zone.Id, IsDefault = true });
        var pc = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-01", DeviceKey = "sha256:x", Status = "approved", ZoneId = zone.Id };
        var pc2 = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-02", DeviceKey = "sha256:y", Status = "approved", ZoneId = zone.Id };
        db.Devices.AddRange(pc, pc2);
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "mt-player", DisplayName = "P" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var audit = new AuditLogger(db, new HttpContextAccessor());
        var actor = new StaffActor(null, "manager");
        await Wallet.CreditAsync(db, user.Id, 200m);
        await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = user.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = 200m, BalanceDelta = 200m }, actor, DateTimeOffset.UtcNow);
        db.ChangeTracker.Clear();

        var sessions = new SessionService(db, new EfDeviceRepository(db), ClubClock.System(), audit, actor);
        var s = await sessions.StartAsync(new StartSessionRequest(pc.Id, null, user.Id, 60));
        Assert.True(s.Ok, s.Error?.Message);
        Assert.True((await sessions.ExtendAsync(s.Value!.Id, new ExtendSessionRequest(30))).Ok);
        db.ChangeTracker.Clear();
        Assert.True((await sessions.EndAsync(s.Value.Id)).Ok);
        db.ChangeTracker.Clear();

        var (date, time) = EventApiTests.Tomorrow(18);
        var reservations = new ReservationService(db, new EfDeviceRepository(db), ClubClock.System(), audit, actor);
        var r = await reservations.CreateAsync(new CreateReservationRequest(pc.Id, null, date, time, 60, user.Id, null, 30m));
        Assert.True(r.Ok, r.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.True((await reservations.CancelAsync(r.Value!.Id, null)).Ok);
        db.ChangeTracker.Clear();

        var events = new EventService(db, new EfDeviceRepository(db), ClubClock.System(), audit, actor);
        var (date2, time2) = EventApiTests.Tomorrow(20);
        var e = await events.CreateAsync(new CreateEventRequest("Cup", null, new[] { pc2.Id }, date2, time2, 60, EventPriceModes.Fixed, 25m, 20m));
        Assert.True(e.Ok, e.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.True((await events.RegisterAsync(e.Value!.Id, new RegisterTeamRequest(null, new[] { user.Id }))).Ok);
        db.ChangeTracker.Clear();
        Assert.True((await events.CancelAsync(e.Value.Id, null)).Ok);

        var texts = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Action + ": " + a.Summary).ToListAsync();
        texts.AddRange(await db.Ledger.AsNoTracking().OrderBy(l => l.Id).Select(l => l.Type + ": " + l.Description).ToListAsync());
        return texts;
    }

    private static void AssertTwoDecimals(IEnumerable<string> texts)
    {
        var amounts = texts.SelectMany(t => Amount.Matches(t).Select(m => (Text: t, Amount: m.Value))).ToList();
        Assert.True(amounts.Count >= 10, $"only {amounts.Count} amounts");
        Assert.All(amounts, a => Assert.Matches(@"^[+-]?\d+\.\d{2} ", a.Amount));
        Assert.Contains(amounts, a => a.Amount == "45.00 ₴");   // a tariff price read from the database
        Assert.Contains(amounts, a => a.Amount == "30.00 ₴");   // a deposit from the request
        Assert.Contains(amounts, a => a.Amount == "20.00 ₴");   // an entry fee
    }

    [Fact]
    public async Task Sqlite_texts_show_money_with_two_decimals() => AssertTwoDecimals(await ScenarioAsync(new SqliteFileDb()));

    [PostgresFact]
    public async Task Sqlite_and_postgres_write_the_same_texts()
    {
        var sqlite = await ScenarioAsync(new SqliteFileDb());
        var postgres = await ScenarioAsync(new PostgresDb());
        AssertTwoDecimals(postgres);
        Assert.Equal(sqlite, postgres);
    }

    [Fact]
    public void The_helper_always_writes_two_decimals()
    {
        Assert.Equal("30.00", MoneyText.Of(30m));
        Assert.Equal("30.00", MoneyText.Of(30.0m));
        Assert.Equal("30.50", MoneyText.Of(30.5m));
        Assert.Equal("1234.57", MoneyText.Of(1234.567m));
        Assert.Equal("—", MoneyText.Of((decimal?)null));
        Assert.Equal("+30.00", MoneyText.Signed(30m));
        Assert.Equal("-12.50", MoneyText.Signed(-12.5m));
        Assert.Equal("0.00", MoneyText.Signed(0m));
        Assert.Equal("150.00 ₴", ClubCurrency.Uah.Text(150m));
    }
}
