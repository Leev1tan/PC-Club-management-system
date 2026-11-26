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
        }

        IReadOnlyList<string> changes;
        await using (var db = _testDb.NewContext())
            changes = await SqliteSchemaUpgrader.UpgradeAsync(db);

        Assert.Contains("table Transactions", changes);
        Assert.Contains("index IX_Commands_DeviceId_Status", changes);
        Assert.Contains("column Devices.PositionY", changes);

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
}
