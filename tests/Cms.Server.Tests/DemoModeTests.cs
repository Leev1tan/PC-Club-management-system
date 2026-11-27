using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cms.Server.Tests;

public sealed class DemoModeTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly DeviceRuntimeState _runtime = new();

    public void Dispose() => _testDb.Dispose();

    private async Task<(DemoSimulator Sim, DemoSeedResult Seed)> SeedAsync(DateTimeOffset now)
    {
        await using var db = _testDb.NewContext();
        var seed = await DemoSeeder.ResetAsync(db, now, new Random(42));
        var sim = new DemoSimulator(NullLogger<DemoSimulator>.Instance, _runtime, new Random(7));
        sim.Load(seed, now);
        return (sim, seed);
    }

    private async Task TickAsync(DemoSimulator sim, DateTimeOffset now)
    {
        await using var db = _testDb.NewContext();
        await sim.TickAsync(db, new EfDeviceRepository(db, _runtime), now);
    }

    private Guid Enqueue(Guid deviceId, string type, object? payload = null)
    {
        using var db = _testDb.NewContext();
        return new EfDeviceRepository(db, _runtime).EnqueueCommand(deviceId, new EnqueueCommandRequest(type, payload))!.Id;
    }

    [Fact]
    public async Task Seed_creates_demo_club_with_20_pcs_in_three_zones()
    {
        await SeedAsync(DateTimeOffset.UtcNow);

        await using var db = _testDb.NewContext();
        var zones = (await db.Zones.ToListAsync()).OrderBy(z => z.SortOrder).ToList();
        var devices = await db.Devices.ToListAsync();
        var sessions = await db.Sessions.ToListAsync();
        var users = await db.Users.ToListAsync();

        Assert.Equal(new[] { 12, 6, 2 }, zones.Select(z => devices.Count(d => d.ZoneId == z.Id)));
        Assert.All(devices, d => Assert.Equal(DemoSeeder.AgentVersion, d.AgentVersion));
        Assert.True(await db.Tariffs.CountAsync() >= 4);
        Assert.Contains(users, u => u.Username == DemoOptions.PlayerLogin &&
                                    Cms.Server.Auth.Passwords.Verify(u.PasswordHash, DemoOptions.PlayerPassword) == Cms.Server.Auth.PasswordCheck.Success);
        Assert.All(users, u => Assert.True(u.Balance >= 50m));
        Assert.Contains(sessions, s => s.Status == "active" && !s.IsPrepaid);
        Assert.True(sessions.Count(s => s.Status == "active") >= 8);
        Assert.True(sessions.Count(s => s.Status == "ended") >= 100);
        Assert.DoesNotContain(sessions.Where(s => s.Status == "active").GroupBy(s => s.DeviceId), g => g.Count() > 1);
    }

    [Fact]
    public async Task Guard_accepts_empty_and_demo_databases()
    {
        await using (var db = _testDb.NewContext())
            Assert.True(await DemoSeeder.IsDemoOrEmptyAsync(db));

        await SeedAsync(DateTimeOffset.UtcNow);

        await using (var db = _testDb.NewContext())
            Assert.True(await DemoSeeder.IsDemoOrEmptyAsync(db));
    }

    [Fact]
    public async Task Guard_refuses_a_database_with_real_club_data()
    {
        await using var db = _testDb.NewContext();
        db.Zones.Add(new ZoneEntity { Id = Guid.NewGuid(), Name = "Main hall" });
        await db.SaveChangesAsync();
        Assert.False(await DemoSeeder.IsDemoOrEmptyAsync(db));

        db.Devices.Add(new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-01", AgentVersion = "1.0.0.0", DeviceKey = "real-key" });
        await db.SaveChangesAsync();
        Assert.False(await DemoSeeder.IsDemoOrEmptyAsync(db));
    }

    [Fact]
    public async Task Reset_removes_changes_made_by_visitors()
    {
        await SeedAsync(DateTimeOffset.UtcNow);
        await using (var db = _testDb.NewContext())
        {
            db.Users.Add(new UserEntity { Id = Guid.NewGuid(), Username = "visitor", DisplayName = "Visitor" });
            db.Zones.Add(new ZoneEntity { Id = Guid.NewGuid(), Name = "Visitor zone" });
            await db.SaveChangesAsync();
        }

        await SeedAsync(DateTimeOffset.UtcNow);

        await using (var db = _testDb.NewContext())
        {
            Assert.DoesNotContain(await db.Users.ToListAsync(), u => u.Username == "visitor");
            Assert.Equal(3, await db.Zones.CountAsync());
            Assert.Equal(20, await db.Devices.CountAsync());
        }
    }

    [Fact]
    public async Task Simulator_executes_lock_and_unlock_commands()
    {
        var now = DateTimeOffset.UtcNow;
        var (sim, seed) = await SeedAsync(now);
        var pc = seed.Pcs.First(p => p.Locked == false);

        var lockId = Enqueue(pc.Id, CommandTypes.Lock);
        await TickAsync(sim, now.AddSeconds(1)); // picked up
        await TickAsync(sim, now.AddSeconds(3)); // acknowledged
        Assert.True(_runtime.IsLocked(pc.Id));

        var unlockId = Enqueue(pc.Id, CommandTypes.Unlock);
        await TickAsync(sim, now.AddSeconds(4));
        await TickAsync(sim, now.AddSeconds(6));
        Assert.False(_runtime.IsLocked(pc.Id));

        await using var db = _testDb.NewContext();
        var statuses = await db.Commands.Where(c => c.Id == lockId || c.Id == unlockId).Select(c => c.Status).ToListAsync();
        Assert.All(statuses, s => Assert.Equal("done", s));
    }

    [Fact]
    public async Task Simulator_shows_messages_and_ignores_unknown_commands()
    {
        var now = DateTimeOffset.UtcNow;
        var (sim, seed) = await SeedAsync(now);
        var pc = seed.Pcs.First(p => !p.AlwaysOffline);

        var messageId = Enqueue(pc.Id, CommandTypes.Message, new { text = "Your session ends in 5 minutes" });
        var unknownId = Enqueue(pc.Id, "format_c");
        await TickAsync(sim, now.AddSeconds(1));
        await TickAsync(sim, now.AddSeconds(3));

        await using var db = _testDb.NewContext();
        var message = await db.Commands.SingleAsync(c => c.Id == messageId);
        var unknown = await db.Commands.SingleAsync(c => c.Id == unknownId);
        Assert.Equal("done", message.Status);
        Assert.Contains("Your session ends in 5 minutes", message.Result);
        Assert.Equal("ignored", unknown.Status);
    }

    [Fact]
    public async Task Restart_takes_the_pc_offline_for_a_while()
    {
        var now = DateTimeOffset.UtcNow;
        var (sim, seed) = await SeedAsync(now);
        var pc = seed.Pcs.First(p => !p.AlwaysOffline);

        Enqueue(pc.Id, CommandTypes.Restart);
        await TickAsync(sim, now.AddSeconds(1));
        await TickAsync(sim, now.AddSeconds(3));

        Assert.False(sim.IsOnline(pc.Id, now.AddSeconds(25)));
        Assert.True(sim.IsOnline(pc.Id, now.AddSeconds(60)));
    }

    [Fact]
    public async Task Offline_pc_never_picks_up_commands()
    {
        var now = DateTimeOffset.UtcNow;
        var (sim, seed) = await SeedAsync(now);
        var offline = seed.Pcs.Single(p => p.AlwaysOffline);

        var id = Enqueue(offline.Id, CommandTypes.Lock);
        await TickAsync(sim, now.AddSeconds(1));
        await TickAsync(sim, now.AddSeconds(3));

        await using var db = _testDb.NewContext();
        Assert.Equal("pending", (await db.Commands.SingleAsync(c => c.Id == id)).Status);
    }

    [Fact]
    public async Task Autopilot_starts_walk_in_sessions_and_unlocks_the_pc()
    {
        var now = DateTimeOffset.UtcNow;
        var (sim, _) = await SeedAsync(now);
        int activeBefore;
        await using (var db = _testDb.NewContext())
            activeBefore = await db.Sessions.CountAsync(s => s.Status == "active");

        await TickAsync(sim, now.AddSeconds(21)); // autopilot: session + session_set + unlock
        await TickAsync(sim, now.AddSeconds(22)); // picked up
        await TickAsync(sim, now.AddSeconds(25)); // acknowledged

        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(activeBefore + 1, await db.Sessions.CountAsync(s => s.Status == "active"));
            var commands = await db.Commands.ToListAsync();
            Assert.Contains(commands, c => c.Type == CommandTypes.Unlock);
            Assert.All(commands, c => Assert.Equal("done", c.Status));
        }
    }

    [Fact]
    public async Task Device_list_reports_lock_state_and_pending_commands()
    {
        var now = DateTimeOffset.UtcNow;
        var (_, seed) = await SeedAsync(now);
        var offline = seed.Pcs.Single(p => p.AlwaysOffline);
        var locked = seed.Pcs.First(p => p.Locked == true);
        Enqueue(offline.Id, CommandTypes.Message, new { text = "hello" });

        await using var db = _testDb.NewContext();
        var controller = new DevicesController(new EfDeviceRepository(db, _runtime), db, _runtime);
        var ok = Assert.IsType<OkObjectResult>((await controller.List()).Result);
        var list = Assert.IsAssignableFrom<IEnumerable<DeviceDetailView>>(ok.Value).ToList();

        Assert.Equal(20, list.Count);
        var offlineView = list.Single(d => d.Id == offline.Id);
        Assert.Equal("offline", offlineView.Status);
        Assert.Equal(1, offlineView.PendingCommands);
        Assert.Null(offlineView.IsLocked);
        Assert.True(list.Single(d => d.Id == locked.Id).IsLocked);
        Assert.Equal(19, list.Count(d => d.Status == "online"));
    }
}
