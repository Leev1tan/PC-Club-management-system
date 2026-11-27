using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Cms.Server.Data;

/// <summary>
/// Creates or upgrades the database schema at startup.
/// PostgreSQL: applies EF Core migrations (Cms.Server/Migrations).
/// SQLite (single-PC installs, tests): EnsureCreated() for a new database; an existing
/// database gets missing tables, columns and indexes from <see cref="SqliteSchemaUpgrader"/>.
/// </summary>
public static class DatabaseInitializer
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static async Task InitializeAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();

                if (db.Database.IsNpgsql())
                {
                    await BaselineLegacyDatabaseAsync(db, logger, ct);
                    var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                    if (pending.Count > 0)
                        logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                    await db.Database.MigrateAsync(ct);
                }
                else
                {
                    // New database: create the full schema. Existing database: add what a newer
                    // version needs (EnsureCreated does nothing on an existing database).
                    if (!await db.Database.EnsureCreatedAsync(ct))
                    {
                        var changes = await SqliteSchemaUpgrader.UpgradeAsync(db, ct);
                        if (changes.Count > 0)
                            logger.LogWarning("Upgraded the SQLite database schema: added {Changes}", string.Join(", ", changes));
                    }
                    await Cms.Server.Auth.AuditLogGuard.EnsureSqliteTriggersAsync(db, ct);
                }

                var hashed = await Cms.Server.Auth.DeviceKeys.HashLegacyKeysAsync(db, ct);
                if (hashed > 0)
                    logger.LogWarning("Replaced {Count} stored device key(s) with their SHA-256 hash", hashed);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                // PostgreSQL may still be starting (docker compose / Kubernetes)
                logger.LogWarning("Database not reachable yet (attempt {Attempt}/{Max}): {Message}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// Prepares the migration history table before Migrate() runs:
    ///  - creates it when missing (EF Core 9 otherwise logs a failed query on a fresh database);
    ///  - earlier versions created the schema with EnsureCreated(), which leaves no history table.
    ///    Such a database already has the full schema, so all migrations are recorded as applied
    ///    instead of letting Migrate() try to create tables that already exist.
    /// </summary>
    private static async Task BaselineLegacyDatabaseAsync(CmsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (!await db.Database.CanConnectAsync(ct)) return; // database does not exist yet; Migrate() creates it

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (await TableExistsAsync(db, "__EFMigrationsHistory", ct)) return;

            // "Zones" only exists in schemas created from the full model by EnsureCreated()
            var legacySchema = await TableExistsAsync(db, "Devices", ct) && await TableExistsAsync(db, "Zones", ct);

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            if (!legacySchema) return;

            foreach (var migration in db.Database.GetMigrations())
            {
                await db.Database.ExecuteSqlRawAsync(
                    history.GetInsertScript(new HistoryRow(migration, ProductInfo.GetVersion())), ct);
            }
            logger.LogWarning("Existing database without migration history found; recorded current migrations as applied");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(CmsDbContext db, string table, CancellationToken ct)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        var p = cmd.CreateParameter();
        p.ParameterName = "name";
        p.Value = $"\"{table}\"";
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }
}
