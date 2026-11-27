using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>A database where every context has its own connection, so requests really run in parallel.</summary>
public interface IParallelDb : IDisposable
{
    CmsDbContext NewContext();
}

public sealed class SqliteFileDb : IParallelDb
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ccms-concurrency-{Guid.NewGuid():N}.db");

    public SqliteFileDb()
    {
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public CmsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CmsDbContext>().UseSqlite($"Data Source={_path};Default Timeout=60").Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { /* temp */ }
    }
}

/// <summary>Fresh PostgreSQL database (migrations applied) when CMS_TEST_POSTGRES is set.</summary>
public sealed class PostgresDb : IParallelDb
{
    public const string Variable = "CMS_TEST_POSTGRES";
    private readonly string _admin;
    private readonly string _name = $"ccms_test_{Guid.NewGuid():N}";
    private readonly string _connection;

    public PostgresDb()
    {
        _admin = Environment.GetEnvironmentVariable(Variable)!;
        _connection = new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString;
        using var db = NewContext();
        db.Database.Migrate();
    }

    public CmsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CmsDbContext>().UseNpgsql(_connection).Options);

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        using var conn = new NpgsqlConnection(_admin);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PostgresDb.Variable)))
            Skip = $"Set {PostgresDb.Variable} to a PostgreSQL connection string to run this test.";
    }
}

public abstract class ConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;

    protected ConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    private CmsDbContext Ctx() => _db.Value.NewContext();

    private async Task<(TariffEntity Tariff, UserEntity User, List<DeviceEntity> Pcs)> SeedAsync(decimal balance, int pcs)
    {
        await using var db = Ctx();
        var tariff = new TariffEntity { Id = Guid.NewGuid(), Name = "Std", PricePerHour = 60m, IsDefault = true };
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P", Balance = balance };
        var devices = Enumerable.Range(1, pcs).Select(i => new DeviceEntity
        {
            Id = Guid.NewGuid(), Hostname = $"PC-{i:00}", DeviceKey = "sha256:" + Guid.NewGuid(), Status = "approved"
        }).ToList();
        db.AddRange(tariff, user);
        db.Devices.AddRange(devices);
        await db.SaveChangesAsync();
        return (tariff, user, devices);
    }

    private async Task<IActionResult?> RunAsync(Func<SessionsController, Task<ActionResult<SessionDetailView>>> action)
    {
        await using var db = Ctx();
        var controller = new SessionsController(db, new EfDeviceRepository(db));
        try
        {
            return (await action(controller)).Result;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException or NpgsqlException)
        {
            return new ObjectResult(ex.GetType().Name) { StatusCode = 500 };
        }
    }

    private async Task<decimal> BalanceAsync(Guid userId)
    {
        await using var db = Ctx();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).Balance;
    }

    protected async Task Parallel_prepaid_starts_never_spend_more_than_the_balance()
    {
        // 100 ₴ pays for one 60-minute session (60 ₴), not for two
        var (tariff, user, pcs) = await SeedAsync(100m, 8);
        var results = await Task.WhenAll(pcs.Select(pc =>
            RunAsync(c => c.Start(new StartSessionRequest(pc.Id, tariff.Id, user.Id, 60)))));

        Assert.Equal(1, results.Count(r => r is OkObjectResult));
        Assert.Equal(40m, await BalanceAsync(user.Id));
        await using var db = Ctx();
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.UserId == user.Id));
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.UserId == user.Id && s.Status == "active"));
    }

    protected async Task Parallel_starts_on_one_pc_create_one_session()
    {
        var (tariff, _, pcs) = await SeedAsync(0m, 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            RunAsync(c => c.Start(new StartSessionRequest(pcs[0].Id, tariff.Id, null, 30)))));

        Assert.Equal(1, results.Count(r => r is OkObjectResult));
        Assert.All(results.Where(r => r is not OkObjectResult), r => Assert.IsType<ConflictObjectResult>(r));
        await using var db = Ctx();
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.DeviceId == pcs[0].Id && s.Status == "active"));

        // The database itself refuses a second active session on the PC
        db.Sessions.Add(new SessionEntity { Id = Guid.NewGuid(), DeviceId = pcs[0].Id, StartUtc = DateTimeOffset.UtcNow, Status = "active" });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    protected async Task Parallel_extends_stop_at_zero_balance()
    {
        // 60 ₴ start + 2 × 30 ₴ extensions = 120 ₴; the other extensions must fail
        var (tariff, user, pcs) = await SeedAsync(120m, 1);
        var start = await RunAsync(c => c.Start(new StartSessionRequest(pcs[0].Id, tariff.Id, user.Id, 60)));
        var sessionId = Assert.IsType<SessionDetailView>(Assert.IsType<OkObjectResult>(start).Value).Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            RunAsync(c => c.Extend(sessionId, new ExtendSessionRequest(30)))));

        var ok = results.Count(r => r is OkObjectResult);
        Assert.True(ok <= 2, $"{ok} extensions succeeded");
        var balance = await BalanceAsync(user.Id);
        Assert.True(balance >= 0m);
        await using var db = Ctx();
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        // Money taken == money on the session: no lost update in either direction
        Assert.Equal(120m - balance, session.TotalCost);
        Assert.Equal(60m + ok * 30m, session.TotalCost);
    }

    protected async Task Parallel_ends_charge_an_open_session_once()
    {
        var (tariff, user, pcs) = await SeedAsync(500m, 1);
        await using (var db = Ctx())
        {
            db.Sessions.Add(new SessionEntity
            {
                Id = Guid.NewGuid(), DeviceId = pcs[0].Id, TariffId = tariff.Id, UserId = user.Id,
                StartUtc = DateTimeOffset.UtcNow.AddMinutes(-90), Status = "active", IsPrepaid = false
            });
            await db.SaveChangesAsync();
        }
        Guid id;
        await using (var db = Ctx()) id = (await db.Sessions.SingleAsync(s => s.DeviceId == pcs[0].Id)).Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunAsync(c => c.End(id))));

        Assert.Equal(1, results.Count(r => r is OkObjectResult));
        await using var check = Ctx();
        Assert.Equal(1, await check.Transactions.CountAsync(t => t.UserId == user.Id));
        Assert.InRange(await BalanceAsync(user.Id), 409m, 411m); // 90 min at 60 ₴/h = about 90 ₴
    }

    protected async Task A_postpaid_session_never_makes_the_balance_negative()
    {
        var (tariff, user, pcs) = await SeedAsync(50m, 1);
        await using (var db = Ctx())
        {
            db.Sessions.Add(new SessionEntity
            {
                Id = Guid.NewGuid(), DeviceId = pcs[0].Id, TariffId = tariff.Id, UserId = user.Id,
                StartUtc = DateTimeOffset.UtcNow.AddMinutes(-120), Status = "active", IsPrepaid = false
            });
            await db.SaveChangesAsync();
        }
        Guid id;
        await using (var db = Ctx()) id = (await db.Sessions.SingleAsync(s => s.DeviceId == pcs[0].Id)).Id;

        var result = Assert.IsType<OkObjectResult>(await RunAsync(c => c.End(id)));
        var view = Assert.IsType<SessionDetailView>(result.Value);
        Assert.Equal(0m, await BalanceAsync(user.Id));
        Assert.InRange(view.AmountDue, 69m, 71m); // 120 ₴ cost - 50 ₴ balance
    }
}

public sealed class SqliteConcurrencyTests : ConcurrencyTestsBase
{
    public SqliteConcurrencyTests() : base(() => new SqliteFileDb()) { }

    [Fact] public Task Prepaid_starts() => Parallel_prepaid_starts_never_spend_more_than_the_balance();
    [Fact] public Task Starts_on_one_pc() => Parallel_starts_on_one_pc_create_one_session();
    [Fact] public Task Extends() => Parallel_extends_stop_at_zero_balance();
    [Fact] public Task Ends() => Parallel_ends_charge_an_open_session_once();
    [Fact] public Task Postpaid_debt() => A_postpaid_session_never_makes_the_balance_negative();
}

public sealed class PostgresConcurrencyTests : ConcurrencyTestsBase
{
    public PostgresConcurrencyTests() : base(() => new PostgresDb()) { }

    [PostgresFact] public Task Prepaid_starts() => Parallel_prepaid_starts_never_spend_more_than_the_balance();
    [PostgresFact] public Task Starts_on_one_pc() => Parallel_starts_on_one_pc_create_one_session();
    [PostgresFact] public Task Extends() => Parallel_extends_stop_at_zero_balance();
    [PostgresFact] public Task Ends() => Parallel_ends_charge_an_open_session_once();
    [PostgresFact] public Task Postpaid_debt() => A_postpaid_session_never_makes_the_balance_negative();
}
