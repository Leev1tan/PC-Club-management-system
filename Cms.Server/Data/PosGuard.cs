using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

/// <summary>
/// Database triggers of the bar and shift tables (SQLite here; PostgreSQL: migration PosShiftsAudit):
///  - Sales, SaleItems, StockMovements are append-only (no UPDATE, no DELETE);
///  - Shifts cannot be deleted, and a closed shift cannot be changed (no edit, no reopen).
/// </summary>
public static class PosGuard
{
    private static readonly string[] AppendOnlyTables = { "Sales", "SaleItems", "StockMovements" };

    private static string SqliteCreateTriggers =>
        string.Concat(AppendOnlyTables.Select(t => $"""
            CREATE TRIGGER IF NOT EXISTS "{t}_no_update" BEFORE UPDATE ON "{t}"
            BEGIN SELECT RAISE(ABORT, '{t} is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS "{t}_no_delete" BEFORE DELETE ON "{t}"
            BEGIN SELECT RAISE(ABORT, '{t} is append-only'); END;

            """)) + """
            CREATE TRIGGER IF NOT EXISTS "Shifts_no_delete" BEFORE DELETE ON "Shifts"
            BEGIN SELECT RAISE(ABORT, 'Shifts cannot be deleted'); END;
            CREATE TRIGGER IF NOT EXISTS "Shifts_closed_guard" BEFORE UPDATE ON "Shifts" WHEN OLD."Status" = 'closed'
            BEGIN SELECT RAISE(ABORT, 'A closed shift cannot be changed'); END;
            """;

    private static string SqliteDropTriggers =>
        string.Concat(AppendOnlyTables.Select(t => $"DROP TRIGGER IF EXISTS \"{t}_no_update\"; DROP TRIGGER IF EXISTS \"{t}_no_delete\"; ")) +
        "DROP TRIGGER IF EXISTS \"Shifts_no_delete\"; DROP TRIGGER IF EXISTS \"Shifts_closed_guard\";";

    public static Task EnsureSqliteTriggersAsync(CmsDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(SqliteCreateTriggers, ct);

    /// <summary>
    /// Demo reset only: removes receipts, the stock log, shifts and products. PostgreSQL: TRUNCATE
    /// (row triggers do not fire); SQLite: drop the triggers, delete, create them again.
    /// </summary>
    public static async Task ClearForDemoResetAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"SaleItems\", \"Sales\", \"StockMovements\", \"Shifts\"", ct);
            await db.Products.ExecuteDeleteAsync(ct);
            return;
        }
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(SqliteDropTriggers, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SaleItems\"; DELETE FROM \"Sales\"; DELETE FROM \"StockMovements\"; DELETE FROM \"Shifts\";", ct);
            await db.Products.ExecuteDeleteAsync(ct);
            await EnsureSqliteTriggersAsync(db, ct);
            return;
        }
        await db.SaleItems.ExecuteDeleteAsync(ct);
        await db.Sales.ExecuteDeleteAsync(ct);
        await db.StockMovements.ExecuteDeleteAsync(ct);
        await db.Shifts.ExecuteDeleteAsync(ct);
        await db.Products.ExecuteDeleteAsync(ct);
    }
}
