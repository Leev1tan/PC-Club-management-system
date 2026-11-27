using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

public sealed class RetentionTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly DeviceRuntimeState _runtime = new();

    public void Dispose() => _testDb.Dispose();

    private async Task<DeviceEntity> AddDeviceAsync(string key = "plain-key")
    {
        await using var db = _testDb.NewContext();
        var device = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-01", DeviceKey = Cms.Server.Auth.DeviceKeys.Hash(key) };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    [Fact]
    public async Task Heartbeats_update_last_seen_without_a_row_per_heartbeat()
    {
        await AddDeviceAsync();
        for (var i = 0; i < 10; i++)
        {
            await using var db = _testDb.NewContext();
            Assert.Equal("approved", new EfDeviceRepository(db, _runtime).Heartbeat("plain-key",
                new DeviceHeartbeatRequest(10, 20, "kiosk", "10.3.3.101", TimeSpan.FromMinutes(5))));
        }

        await using var check = _testDb.NewContext();
        Assert.Equal(1, await check.Heartbeats.CountAsync()); // one sample per 5 minutes per PC
        Assert.NotNull((await check.Devices.SingleAsync()).LastSeenUtc);
    }

    [Fact]
    public async Task Clean_up_deletes_old_rows_and_expires_stale_commands()
    {
        var device = await AddDeviceAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = _testDb.NewContext())
        {
            HeartbeatEntity Hb(DateTimeOffset at) => new() { Id = Guid.NewGuid(), DeviceId = device.Id, CreatedUtc = at };
            CommandEntity Cmd(string status, DateTimeOffset at) => new() { Id = Guid.NewGuid(), DeviceId = device.Id, Type = "lock", Status = status, CreatedUtc = at };
            db.Heartbeats.AddRange(Hb(now.AddDays(-8)), Hb(now.AddDays(-7.5)), Hb(now.AddDays(-1)), Hb(now));
            db.Commands.AddRange(
                Cmd("done", now.AddDays(-31)), Cmd("failed", now.AddDays(-40)), Cmd("done", now.AddDays(-2)),
                Cmd("pending", now.AddHours(-3)), Cmd("delivered", now.AddHours(-2)), Cmd("pending", now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        await using (var db = _testDb.NewContext())
        {
            var result = await RetentionService.RunOnceAsync(db, new RetentionOptions(), now);
            Assert.Equal(new RetentionResult(2, 2, 2), result);
        }

        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(2, await db.Heartbeats.CountAsync());
            var statuses = (await db.Commands.Select(c => c.Status).ToListAsync()).OrderBy(s => s).ToList();
            Assert.Equal(new[] { "done", "expired", "expired", "pending" }, statuses);
        }
    }

    [Fact]
    public async Task A_command_queued_long_ago_is_not_delivered()
    {
        var device = await AddDeviceAsync();
        await using (var db = _testDb.NewContext())
        {
            db.Commands.Add(new CommandEntity { Id = Guid.NewGuid(), DeviceId = device.Id, Type = "unlock", Status = "pending", CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2) });
            db.Commands.Add(new CommandEntity { Id = Guid.NewGuid(), DeviceId = device.Id, Type = "lock", Status = "pending", CreatedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var db = _testDb.NewContext())
        {
            var delivered = new EfDeviceRepository(db, _runtime).PollCommands(device.Id, 10).ToList();
            Assert.Equal("lock", Assert.Single(delivered).Type);
        }
        await using (var db = _testDb.NewContext())
            Assert.Equal("expired", (await db.Commands.SingleAsync(c => c.Type == "unlock")).Status);
    }
}
