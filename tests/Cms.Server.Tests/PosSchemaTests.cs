using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Phase 4 schema: shifts, bar catalog, stock log, receipts, debt and audit hashes, on both databases.</summary>
public sealed class PosSchemaTests : IDisposable
{
    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    private static ShiftEntity Shift(string status = ShiftStatuses.Open) => new()
    {
        Status = status, OpenedUtc = DateTimeOffset.UtcNow, OpenedByUsername = "cashier", OpeningFloat = 500m
    };

    [Fact]
    public async Task A_phase_3_sqlite_database_gets_the_new_tables_and_columns_and_keeps_its_data()
    {
        var player = Guid.NewGuid();
        await using (var db = _testDb.NewContext())
        {
            db.Users.Add(new UserEntity { Id = player, Username = "p3", DisplayName = "P3", Balance = 40m });
            db.Ledger.Add(new LedgerEntryEntity { CreatedUtc = DateTimeOffset.UtcNow, UserId = player, Type = LedgerTypes.TopUp, Method = PaymentMethods.Cash,
                Amount = 40m, BalanceDelta = 40m, DeskAmount = 40m, BalanceAfter = 40m, BonusAfter = 0m });
            db.AuditLog.Add(new AuditLogEntity { CreatedUtc = DateTimeOffset.UtcNow, Action = "player.topup" });
            await db.SaveChangesAsync();

            // The schema of phase 3
            await db.Database.ExecuteSqlRawAsync("""
                DROP TABLE "SaleItems"; DROP TABLE "Sales"; DROP TABLE "StockMovements"; DROP TABLE "Products"; DROP TABLE "Shifts";
                DROP INDEX "IX_LedgerEntries_ShiftId"; DROP INDEX "IX_LedgerEntries_SaleId";
                ALTER TABLE "LedgerEntries" DROP COLUMN "ShiftId"; ALTER TABLE "LedgerEntries" DROP COLUMN "SaleId";
                ALTER TABLE "LedgerEntries" DROP COLUMN "DebtDelta"; ALTER TABLE "LedgerEntries" DROP COLUMN "DebtAfter";
                ALTER TABLE "Users" DROP COLUMN "Debt";
                ALTER TABLE "AuditLog" DROP COLUMN "PrevHash"; ALTER TABLE "AuditLog" DROP COLUMN "Hash";
                """);
        }

        await using (var db = _testDb.NewContext())
        {
            var changes = await SqliteSchemaUpgrader.UpgradeAsync(db);
            foreach (var table in new[] { "Shifts", "Products", "StockMovements", "Sales", "SaleItems" })
                Assert.Contains($"table {table}", changes);
            foreach (var column in new[] { "LedgerEntries.ShiftId", "LedgerEntries.SaleId", "LedgerEntries.DebtDelta", "LedgerEntries.DebtAfter",
                         "Users.Debt", "AuditLog.PrevHash", "AuditLog.Hash" })
                Assert.Contains($"column {column}", changes);
            Assert.Contains($"index {CmsDbContext.OpenShiftIndex}", changes);
            await PosGuard.EnsureSqliteTriggersAsync(db);
            Assert.Empty(await SqliteSchemaUpgrader.UpgradeAsync(db)); // a second start finds nothing to do
        }

        await using (var db = _testDb.NewContext())
        {
            var user = await db.Users.AsNoTracking().SingleAsync();
            Assert.Equal((40m, 0m), (user.Balance, user.Debt));
            var row = await db.Ledger.AsNoTracking().SingleAsync();
            Assert.Equal((40m, 0m, (long?)null, (decimal?)null), (row.DeskAmount, row.DebtDelta, row.ShiftId, row.DebtAfter));
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.Null((await db.AuditLog.AsNoTracking().SingleAsync()).Hash);
        }
    }

    [Fact]
    public async Task Sqlite_keeps_receipts_and_the_stock_log_append_only_and_closed_shifts_final()
    {
        await using var db = _testDb.NewContext();
        await PosGuard.EnsureSqliteTriggersAsync(db);
        var product = new ProductEntity { Id = Guid.NewGuid(), Name = "Monster", Category = "Енергетики", Price = 75m, TrackStock = true, CreatedUtc = DateTimeOffset.UtcNow };
        db.Products.Add(product);
        var open = Shift();
        db.Shifts.Add(open);
        await db.SaveChangesAsync();
        var sale = new SaleEntity { CreatedUtc = DateTimeOffset.UtcNow, Kind = SaleKinds.Sale, Method = PaymentMethods.Cash, Total = 75m, ShiftId = open.Id };
        db.Sales.Add(sale);
        await db.SaveChangesAsync();
        db.SaleItems.Add(new SaleItemEntity { SaleId = sale.Id, ProductId = product.Id, Name = product.Name, Category = product.Category, UnitPrice = 75m, Quantity = 1, LineTotal = 75m });
        db.StockMovements.Add(new StockMovementEntity { CreatedUtc = DateTimeOffset.UtcNow, ProductId = product.Id, Type = StockMovementTypes.Restock, Delta = 10, QuantityAfter = 10 });
        await db.SaveChangesAsync();

        foreach (var table in new[] { "Sales", "SaleItems", "StockMovements" })
        {
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE \"{table}\" SET \"Id\" = \"Id\""));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\""));
        }
        sale.Total = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // One open shift at a time (unique index)
        db.Shifts.Add(Shift());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // An open shift can be closed; a closed shift cannot be changed, reopened or deleted
        await db.Database.ExecuteSqlRawAsync($"UPDATE \"Shifts\" SET \"Status\" = 'closed', \"CountedCash\" = 575 WHERE \"Id\" = {open.Id}");
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"Shifts\" SET \"Status\" = 'open'"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Shifts\""));
        var closed = await db.Shifts.SingleAsync();
        closed.Status = ShiftStatuses.Open;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // A new shift can open after the close; a product with history cannot be deleted
        db.Shifts.Add(Shift());
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Products\""));

        // The demo reset clears all of it
        await PosGuard.ClearForDemoResetAsync(db);
        Assert.Equal((0, 0, 0, 0), (await db.Sales.CountAsync(), await db.StockMovements.CountAsync(), await db.Shifts.CountAsync(), await db.Products.CountAsync()));
        db.Shifts.Add(Shift()); // the triggers are back after the reset
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Shifts\""));
    }
}

public sealed class PostgresPosMigrationTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb(migrate: false));

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    [PostgresFact]
    public async Task The_migration_keeps_phase_3_data_and_guards_the_new_tables()
    {
        await using var db = _db.Value.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127005956_Billing"); // last migration of phase 3

        var player = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Users\" (\"Id\", \"Username\", \"DisplayName\", \"Balance\", \"BonusPoints\", \"CreatedUtc\") VALUES ({0}, 'p3', 'P3', 40, 0, now())", player);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"LedgerEntries\" (\"CreatedUtc\", \"UserId\", \"Type\", \"Method\", \"Amount\", \"BalanceDelta\", \"BonusDelta\", \"DeskAmount\", \"BalanceAfter\", \"BonusAfter\") " +
            "VALUES (now(), {0}, 'topup', 'cash', 40, 40, 0, 40, 40, 0)", player);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"AuditLog\" (\"CreatedUtc\", \"Action\") VALUES (now(), 'player.topup')");

        await migrator.MigrateAsync();

        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal((40m, 0m), (user.Balance, user.Debt));
        var row = await db.Ledger.AsNoTracking().SingleAsync();
        Assert.Equal((0m, (long?)null), (row.DebtDelta, row.ShiftId));
        Assert.Empty(await Ledger.ReconcileAsync(db));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"LedgerEntries\" SET \"Amount\" = 1"));

        var product = new ProductEntity { Id = Guid.NewGuid(), Name = "Monster", Category = "Енергетики", Price = 75m, CreatedUtc = DateTimeOffset.UtcNow };
        var shift = new ShiftEntity { Status = ShiftStatuses.Open, OpenedUtc = DateTimeOffset.UtcNow, OpeningFloat = 500m };
        db.AddRange(product, shift);
        await db.SaveChangesAsync();
        var sale = new SaleEntity { CreatedUtc = DateTimeOffset.UtcNow, Kind = SaleKinds.Sale, Method = PaymentMethods.Cash, Total = 75m, ShiftId = shift.Id };
        db.Sales.Add(sale);
        db.StockMovements.Add(new StockMovementEntity { CreatedUtc = DateTimeOffset.UtcNow, ProductId = product.Id, Type = StockMovementTypes.Restock, Delta = 5, QuantityAfter = 5 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        foreach (var table in new[] { "Sales", "StockMovements" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE \"{table}\" SET \"Id\" = \"Id\""));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\""));
        }
        db.Shifts.Add(new ShiftEntity { Status = ShiftStatuses.Open, OpenedUtc = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); // one open shift
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync($"UPDATE \"Shifts\" SET \"Status\" = 'closed' WHERE \"Id\" = {shift.Id}");
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"Shifts\" SET \"Status\" = 'open'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Shifts\""));

        await PosGuard.ClearForDemoResetAsync(db); // TRUNCATE: demo reset only
        Assert.Equal((0, 0, 0), (await db.Sales.CountAsync(), await db.Shifts.CountAsync(), await db.Products.CountAsync()));
    }
}
