using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

public sealed class SqliteSchemaUpgradeTests : IDisposable
{
    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task Upgrade_adds_missing_tables_columns_and_indexes_and_keeps_data()
    {
        await using (var db = _testDb.NewContext())
        {
            db.Devices.Add(new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-01", DeviceKey = "k1", PositionX = 3 });
            await db.SaveChangesAsync();

            // Simulate a database created by an older version
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Transactions\"");
            await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Commands_DeviceId_Status\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Devices\" DROP COLUMN \"PositionY\"");
            await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_SessionSegments_EndUtc\""); // phase 5 (analytics)
        }

        IReadOnlyList<string> changes;
        await using (var db = _testDb.NewContext())
            changes = await SqliteSchemaUpgrader.UpgradeAsync(db);

        Assert.Contains("table Transactions", changes);
        Assert.Contains("index IX_Commands_DeviceId_Status", changes);
        Assert.Contains("column Devices.PositionY", changes);
        Assert.Contains("index IX_SessionSegments_EndUtc", changes);

        await using (var db = _testDb.NewContext())
        {
            var device = await db.Devices.SingleAsync();
            Assert.Equal("PC-01", device.Hostname);
            Assert.Equal(3, device.PositionX);
            Assert.Null(device.PositionY);
            Assert.Equal(0, await db.Transactions.CountAsync());

            // A second run finds nothing to do
            Assert.Empty(await SqliteSchemaUpgrader.UpgradeAsync(db));
        }
    }

    [Fact]
    public async Task Upgrade_ends_duplicate_active_sessions_before_the_unique_index()
    {
        var deviceId = Guid.NewGuid();
        await using (var db = _testDb.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Sessions_DeviceId_Active\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Sessions\" DROP COLUMN \"ConcurrencyStamp\"");
            db.Devices.Add(new DeviceEntity { Id = deviceId, Hostname = "PC-01", DeviceKey = "k" });
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            foreach (var minutes in new[] { 90, 60, 30 })
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Sessions\" (\"Id\", \"DeviceId\", \"StartUtc\", \"Status\", \"TotalCost\", \"IsPrepaid\") VALUES ({0}, {1}, {2}, 'active', '0.0', 0)",
                    Guid.NewGuid().ToString().ToUpperInvariant(), deviceId.ToString().ToUpperInvariant(), now.AddMinutes(-minutes));
            }
        }

        await using (var db = _testDb.NewContext())
        {
            var changes = await SqliteSchemaUpgrader.UpgradeAsync(db);
            Assert.Contains("index IX_Sessions_DeviceId_Active", changes);
            Assert.Contains("column Sessions.ConcurrencyStamp", changes);
        }

        await using (var db = _testDb.NewContext())
        {
            var sessions = (await db.Sessions.AsNoTracking().ToListAsync()).OrderBy(s => s.StartUtc).ToList();
            Assert.Equal(new[] { "ended", "ended", "active" }, sessions.Select(s => s.Status));
        }
    }
}
