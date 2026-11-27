using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>A database of phase 2 (no tariff rules, packages, segments or ledger) gets the new schema and a ledger.</summary>
public sealed class BillingUpgradeTests : IDisposable
{
    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    private static readonly Guid Player = Guid.NewGuid();
    private static readonly Guid Empty = Guid.NewGuid();

    /// <summary>Turns the fresh database into the phase-2 schema and fills it with phase-2 data.</summary>
    private async Task MakePhase2DatabaseAsync()
    {
        await using var db = _testDb.NewContext();
        foreach (var table in new[] { "LedgerEntries", "SessionSegments", "TariffRules" })
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{table}\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Tariffs\" DROP COLUMN \"BillingStepMinutes\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Tariffs\" DROP COLUMN \"MinimumCharge\"");
        // Sessions of phase 2 (PackageId has a foreign key, so the table is rebuilt instead of DROP COLUMN)
        await db.Database.ExecuteSqlRawAsync("""
            PRAGMA foreign_keys = OFF;
            DROP TABLE "Sessions";
            DROP TABLE "Packages";
            CREATE TABLE "Sessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY, "DeviceId" TEXT NOT NULL, "TariffId" TEXT NULL, "UserId" TEXT NULL,
                "StartUtc" TEXT NOT NULL, "EndUtc" TEXT NULL, "Status" TEXT NOT NULL, "TotalCost" TEXT NOT NULL, "IsPrepaid" INTEGER NOT NULL,
                "ConcurrencyStamp" TEXT NOT NULL,
                CONSTRAINT "FK_Sessions_Devices_DeviceId" FOREIGN KEY ("DeviceId") REFERENCES "Devices" ("Id") ON DELETE CASCADE);
            PRAGMA foreign_keys = ON;
            """);

        var tariff = Guid.NewGuid();
        var device = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Tariffs\" (\"Id\", \"Name\", \"PricePerHour\", \"IsDefault\") VALUES ({0}, 'Old', '60.0', 1)", tariff.ToString().ToUpperInvariant());
        db.Devices.Add(new DeviceEntity { Id = device, Hostname = "PC-01", DeviceKey = "k" });
        db.Users.AddRange(
            new UserEntity { Id = Player, Username = "old", DisplayName = "Old", Balance = 120m, BonusPoints = 15m, CreatedUtc = now.AddDays(-30) },
            new UserEntity { Id = Empty, Username = "empty", DisplayName = "Empty", CreatedUtc = now.AddDays(-3) });
        db.Transactions.AddRange(
            new TransactionEntity { Id = Guid.NewGuid(), UserId = Player, Amount = 200m, Type = "topup", Description = "Поповнення", CreatedUtc = now.AddDays(-10) },
            new TransactionEntity { Id = Guid.NewGuid(), UserId = Player, Amount = -100m, Type = "session_charge", Description = "Сесія", CreatedUtc = now.AddDays(-9) });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Sessions\" (\"Id\", \"DeviceId\", \"TariffId\", \"StartUtc\", \"Status\", \"TotalCost\", \"IsPrepaid\", \"ConcurrencyStamp\") VALUES ({0}, {1}, {2}, {3}, 'active', '0.0', 0, {4})",
            Guid.NewGuid().ToString().ToUpperInvariant(), device.ToString().ToUpperInvariant(), tariff.ToString().ToUpperInvariant(),
            now.AddMinutes(-30), Guid.NewGuid().ToString().ToUpperInvariant());
    }

    [Fact]
    public async Task A_phase_2_sqlite_database_gets_the_billing_schema_and_its_balances_in_the_ledger()
    {
        await MakePhase2DatabaseAsync();

        await using (var db = _testDb.NewContext())
        {
            var changes = await SqliteSchemaUpgrader.UpgradeAsync(db);
            Assert.Contains("table LedgerEntries", changes);
            Assert.Contains("table Packages", changes);
            Assert.Contains("table TariffRules", changes);
            Assert.Contains("table SessionSegments", changes);
            Assert.Contains("column Tariffs.BillingStepMinutes", changes);
            Assert.Contains("column Sessions.PausedUtc", changes);
            Assert.Contains("column Sessions.PackageId", changes);
            Assert.Contains("index IX_Sessions_DeviceId_Active", changes);
            await LedgerGuard.EnsureSqliteTriggersAsync(db);
            Assert.Equal(3, await LedgerBackfill.RunAsync(db, DateTimeOffset.UtcNow));
        }

        await using (var db = _testDb.NewContext())
        {
            var rows = await db.Ledger.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
            Assert.Equal(new[] { LedgerTypes.OpeningBalance, LedgerTypes.TopUp, LedgerTypes.SessionCharge }, rows.Select(r => r.Type));
            Assert.Equal((20m, 15m), (rows[0].BalanceDelta, rows[0].BonusDelta)); // 120 - (200 - 100) not explained by the history
            Assert.Equal(new decimal?[] { 20m, 220m, 120m }, rows.Select(r => r.BalanceAfter));
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.Null(await Ledger.CheckChainAsync(db, Player));
            Assert.False(await db.Ledger.AnyAsync(l => l.UserId == Empty));

            // Old tariffs bill per minute; old sessions keep working (no segments: billed from the start)
            var tariff = await db.Tariffs.AsNoTracking().SingleAsync();
            Assert.Equal(0, tariff.BillingStepMinutes);
            Assert.Equal(1, TariffPricing.From(tariff, Array.Empty<TariffRuleEntity>()).Step);
            var session = await db.Sessions.SingleAsync();
            var service = new SessionService(db, new Cms.Server.Repositories.EfDeviceRepository(db));
            var end = await service.EndAsync(session.Id);
            Assert.InRange(end.Value!.Session.TotalCost, 29m, 32m);

            // A second start finds nothing to do; the ledger is append-only
            Assert.Empty(await SqliteSchemaUpgrader.UpgradeAsync(db));
            Assert.Equal(0, await LedgerBackfill.RunAsync(db, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"LedgerEntries\""));
        }
    }
}

public sealed class PostgresBillingMigrationTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb(migrate: false));

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    [PostgresFact]
    public async Task The_billing_migration_keeps_phase_2_data_and_the_backfill_fills_the_ledger()
    {
        await using var db = _db.Value.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127001616_AuditLog"); // last migration of phase 2

        var player = Guid.NewGuid();
        var tariff = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Users\" (\"Id\", \"Username\", \"DisplayName\", \"Balance\", \"BonusPoints\", \"CreatedUtc\") VALUES ({0}, 'old', 'Old', 80, 5, now() - interval '20 days')", player);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Transactions\" (\"Id\", \"UserId\", \"Amount\", \"Type\", \"Description\", \"CreatedUtc\") VALUES ({0}, {1}, 100, 'topup', 'Поповнення', now() - interval '5 days')", Guid.NewGuid(), player);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Tariffs\" (\"Id\", \"Name\", \"PricePerHour\", \"IsDefault\") VALUES ({0}, 'Old', 60, true)", tariff);

        await migrator.MigrateAsync();
        Assert.Equal(2, await LedgerBackfill.RunAsync(db, DateTimeOffset.UtcNow));

        var rows = await db.Ledger.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(new[] { LedgerTypes.OpeningBalance, LedgerTypes.TopUp }, rows.Select(r => r.Type));
        Assert.Equal((-20m, 5m), (rows[0].BalanceDelta, rows[0].BonusDelta));
        Assert.Empty(await Ledger.ReconcileAsync(db));
        Assert.Equal(1, (await db.Tariffs.AsNoTracking().SingleAsync()).BillingStepMinutes); // migration default

        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"LedgerEntries\" SET \"Amount\" = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"LedgerEntries\""));
        await LedgerGuard.ClearForDemoResetAsync(db); // TRUNCATE: demo reset only
        Assert.Equal(0, await db.Ledger.CountAsync());
    }
}
