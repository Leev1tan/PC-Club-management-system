using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Data;
using Cms.Server.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Phase 6 catalog tables: the PostgreSQL migration keeps phase 5 data; the SQLite upgrade adds them.</summary>
public sealed class GameSchemaTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb(migrate: false));
    private readonly TestDb _sqlite = new();

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
        _sqlite.Dispose();
    }

    [PostgresFact]
    public async Task The_migration_keeps_phase_5_data_and_the_catalog_works_on_postgres()
    {
        await using var db = _db.Value.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127035716_AnalyticsIndexes"); // last migration of phase 5
        var zone = Guid.NewGuid();
        var device = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Zones\" (\"Id\", \"Name\", \"Color\", \"SortOrder\") VALUES ({0}, 'VIP', '#bf5af2', 1)", zone);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Devices\" (\"Id\", \"Hostname\", \"OsVersion\", \"AgentVersion\", \"DeviceKey\", \"Status\", \"ZoneId\") VALUES ({0}, 'VIP-01', 'w', '1', 'sha256:x', 'approved', {1})", device, zone);

        await migrator.MigrateAsync();
        Assert.Equal("VIP-01", (await db.Devices.AsNoTracking().SingleAsync()).Hostname);

        var now = DateTimeOffset.UtcNow;
        var game = new GameEntity { Id = Guid.NewGuid(), Name = "Dota 2", Category = "MOBA", LaunchKind = "steam", SteamAppId = 570, AllZones = false, Color = "#22c55e", CreatedUtc = now, UpdatedUtc = now };
        db.Games.Add(game);
        db.GameZones.Add(new GameZoneEntity { GameId = game.Id, ZoneId = zone });
        db.GamePlays.Add(new GamePlayEntity { Id = Guid.NewGuid(), DeviceId = device, GameId = game.Id, StartUtc = now.AddMinutes(-90), EndUtc = now.AddMinutes(-30) });
        await db.SaveChangesAsync();

        Assert.Equal("Dota 2", Assert.Single(await GameCatalog.ForZoneAsync(db, zone)).Name);
        var top = Assert.Single(await GamePlays.TopAsync(db, now.AddHours(-1), now, now)); // the SQL filter of PostgreSQL
        Assert.Equal((1, 0.5m), (top.Plays, top.Hours));

        // A game with plays cannot be deleted; the zone links go with the zone
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Games\""));
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Devices\" SET \"ZoneId\" = NULL");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Zones\"");
        Assert.Equal(0, await db.GameZones.CountAsync());
    }

    [Fact]
    public async Task A_phase_5_sqlite_database_gets_the_catalog_tables()
    {
        await using (var db = _sqlite.NewContext())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"GamePlays\"; DROP TABLE \"GameZones\"; DROP TABLE \"Games\";");
        await using (var db = _sqlite.NewContext())
        {
            var changes = await SqliteSchemaUpgrader.UpgradeAsync(db);
            foreach (var table in new[] { "Games", "GameZones", "GamePlays" })
                Assert.Contains($"table {table}", changes);
            Assert.Empty(await SqliteSchemaUpgrader.UpgradeAsync(db));
        }
    }
}
