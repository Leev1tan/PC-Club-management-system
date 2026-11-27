using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Sessions.LimitToBalance and BalanceEndUtc (player login): the upgrade keeps the sessions of earlier versions.</summary>
public sealed class PlayerLoginSchemaTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb(migrate: false));
    private readonly TestDb _sqlite = new();

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
        _sqlite.Dispose();
    }

    [Fact]
    public async Task A_sqlite_database_of_phase_5_gets_the_session_columns()
    {
        var device = Guid.NewGuid();
        await using (var db = _sqlite.NewContext())
        {
            db.Devices.Add(new DeviceEntity { Id = device, Hostname = "PC-1", DeviceKey = "k" });
            db.Sessions.Add(new SessionEntity { Id = Guid.NewGuid(), DeviceId = device, StartUtc = DateTimeOffset.UtcNow, Status = "active", IsPrepaid = true });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Sessions\" DROP COLUMN \"LimitToBalance\"; ALTER TABLE \"Sessions\" DROP COLUMN \"BalanceEndUtc\";");
        }
        await using (var db = _sqlite.NewContext())
        {
            var changes = await SqliteSchemaUpgrader.UpgradeAsync(db);
            Assert.Contains("column Sessions.LimitToBalance", changes);
            Assert.Contains("column Sessions.BalanceEndUtc", changes);
            var session = await db.Sessions.AsNoTracking().SingleAsync();
            Assert.Equal((false, (DateTimeOffset?)null), (session.LimitToBalance, session.BalanceEndUtc));
        }
    }

    [PostgresFact]
    public async Task The_postgres_migration_keeps_running_sessions()
    {
        await using var db = _db.Value.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127035716_AnalyticsIndexes"); // phase 5
        var device = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Devices\" (\"Id\", \"Hostname\", \"OsVersion\", \"AgentVersion\", \"DeviceKey\", \"Status\") VALUES ({0}, 'PC-1', 'w', '1', 'sha256:y', 'approved')", device);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Sessions\" (\"Id\", \"DeviceId\", \"StartUtc\", \"Status\", \"TotalCost\", \"IsPrepaid\", \"ConcurrencyStamp\") VALUES ({0}, {1}, now(), 'active', 0, false, {2})",
            Guid.NewGuid(), device, Guid.NewGuid());
        await migrator.MigrateAsync();
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal((false, (DateTimeOffset?)null, "active"), (session.LimitToBalance, session.BalanceEndUtc, session.Status));
    }
}
