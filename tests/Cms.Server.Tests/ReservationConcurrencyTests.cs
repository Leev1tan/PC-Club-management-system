using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Two (or eight) cashiers book at the same moment: one PC is never booked twice, a zone never over-booked.</summary>
public abstract class ReservationConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;

    protected ReservationConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    private CmsDbContext Ctx() => _db.Value.NewContext();

    private async Task<(Guid Zone, List<Guid> Pcs, Guid Player)> SeedAsync(int pcs)
    {
        await using var db = Ctx();
        var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = "Z" };
        var devices = Enumerable.Range(1, pcs).Select(i => new DeviceEntity
        {
            Id = Guid.NewGuid(), Hostname = $"PC-{i:00}", DeviceKey = "sha256:" + Guid.NewGuid(), Status = "approved", ZoneId = zone.Id
        }).ToList();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P" };
        db.Add(zone);
        db.Add(user);
        db.Devices.AddRange(devices);
        await db.SaveChangesAsync();
        await Wallet.CreditAsync(db, user.Id, 100m);
        await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = user.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = 100m, BalanceDelta = 100m }, null, DateTimeOffset.UtcNow);
        return (zone.Id, devices.Select(d => d.Id).ToList(), user.Id);
    }

    private static (string Date, string Time) Tomorrow(int hour)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ClubTimeZone.Default).Date.AddDays(1).AddHours(hour);
        return (local.ToString("yyyy-MM-dd"), local.ToString("HH:mm"));
    }

    private async Task<bool> BookAsync(CreateReservationRequest req)
    {
        await using var db = Ctx();
        try
        {
            return (await new ReservationService(db, new EfDeviceRepository(db), ClubClock.System(), new AuditLogger(db, new HttpContextAccessor())).CreateAsync(req)).Ok;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException or NpgsqlException)
        {
            return false;
        }
    }

    protected async Task Parallel_bookings_of_one_pc_create_one_reservation()
    {
        var (_, pcs, player) = await SeedAsync(1);
        var (date, time) = Tomorrow(18);
        // Eight cashiers book the same PC for overlapping times, some with a deposit of the same player
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            BookAsync(new CreateReservationRequest(pcs[0], null, date, time, 60 + 15 * (i % 3), i % 2 == 0 ? player : null, i % 2 == 0 ? null : $"Гість {i}", i % 2 == 0 ? 30m : 0m))));
        Assert.Equal(1, results.Count(ok => ok));
        await using var db = Ctx();
        Assert.Equal(1, await db.Reservations.CountAsync());
        Assert.Empty(await Ledger.ReconcileAsync(db));
        Assert.True((await Holds.DepositsAsync(db)).Ok);
    }

    protected async Task Parallel_any_pc_bookings_never_exceed_the_pcs_of_the_zone()
    {
        var (zone, pcs, _) = await SeedAsync(3);
        var (date, time) = Tomorrow(19);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            BookAsync(i < 2 ? new CreateReservationRequest(pcs[i], null, date, time, 120, GuestName: $"Гість {i}")
                            : new CreateReservationRequest(null, zone, date, time, 90, GuestName: $"Гість {i}"))));
        await using var db = Ctx();
        var booked = await db.Reservations.AsNoTracking().ToListAsync();
        Assert.Equal(3, booked.Count);                       // three PCs, never more
        Assert.Equal(results.Count(ok => ok), booked.Count);
        Assert.NotNull(ReservationPlan.Assign(pcs, booked.Select(r => new PlanItem(r.Id, r.DeviceId, r.StartUtc, r.EndUtc))));
    }
}

public sealed class SqliteReservationConcurrencyTests : ReservationConcurrencyTestsBase
{
    public SqliteReservationConcurrencyTests() : base(() => new SqliteFileDb()) { }

    [Fact] public Task One_pc() => Parallel_bookings_of_one_pc_create_one_reservation();
    [Fact] public Task One_zone() => Parallel_any_pc_bookings_never_exceed_the_pcs_of_the_zone();
}

public sealed class PostgresReservationConcurrencyTests : ReservationConcurrencyTestsBase
{
    public PostgresReservationConcurrencyTests() : base(() => new PostgresDb()) { }

    [PostgresFact] public Task One_pc() => Parallel_bookings_of_one_pc_create_one_reservation();
    [PostgresFact] public Task One_zone() => Parallel_any_pc_bookings_never_exceed_the_pcs_of_the_zone();
}
