using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

public sealed class AuditLogTests : IClassFixture<CmsFactory>
{
    private readonly CmsFactory _factory;

    public AuditLogTests(CmsFactory factory) => _factory = factory;

    private async Task<List<JsonElement>> AuditAsync(HttpClient client, string? action = null)
    {
        var url = "/api/audit?take=500" + (action == null ? "" : "&action=" + Uri.EscapeDataString(action));
        return (await client.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().ToList();
    }

    private async Task<Guid> AddDeviceAsync(string hostname)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var device = new DeviceEntity { Id = Guid.NewGuid(), Hostname = hostname, DeviceKey = DeviceKeys.Hash(Guid.NewGuid().ToString()), Status = "approved" };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    [Fact]
    public async Task Staff_actions_are_logged_with_actor_role_and_ip()
    {
        var owner = await _factory.LoginOwnerAsync();
        await owner.PostAsJsonAsync("/api/staff", new { username = "audit-cashier", password = "cashier-pass-1", role = "Cashier" });
        var cashier = await _factory.LoginAsync("audit-cashier", "cashier-pass-1");
        await _factory.NewClient().PostAsJsonAsync("/api/auth/login", new { username = "audit-cashier", password = "wrong-pass-0" });

        var pc = await AddDeviceAsync("AUDIT-PC");
        Assert.Equal(HttpStatusCode.OK, (await cashier.PostAsJsonAsync($"/api/devices/{pc}/commands", new { type = "unlock" })).StatusCode);

        var player = await (await cashier.PostAsJsonAsync("/api/users/register", new { username = "audit-player", password = "1357" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var playerId = player.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await cashier.PostAsJsonAsync($"/api/users/{playerId}/topup", new { amount = 150 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/users/{playerId}/adjust", new { amount = -50, reason = "test" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/users/{playerId}/adjust", new { amount = -50, reason = "refund of a broken PC" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/tariffs", new { name = "Audit tariff", pricePerHour = 45, isDefault = true })).StatusCode);

        var log = await AuditAsync(owner);
        JsonElement One(string action) => Assert.Single(log, e => e.GetProperty("action").GetString() == action);

        Assert.Contains(log, e => e.GetProperty("action").GetString() == AuditActions.Login && e.GetProperty("actorUsername").GetString() == "audit-cashier");
        var failed = Assert.Single(log, e => e.GetProperty("action").GetString() == AuditActions.LoginFailed && e.GetProperty("detailsJson").GetString()!.Contains("audit-cashier"));
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("actorUsername").ValueKind);

        var unlock = One(AuditActions.UnlockWithoutSession);
        Assert.Equal("audit-cashier", unlock.GetProperty("actorUsername").GetString());
        Assert.Equal("Cashier", unlock.GetProperty("actorRole").GetString());
        Assert.Equal(pc.ToString(), unlock.GetProperty("targetId").GetString());

        Assert.Contains("150", One(AuditActions.PlayerTopUp).GetProperty("summary").GetString());
        var adjust = One(AuditActions.PlayerBalanceAdjusted);
        Assert.Equal("Owner", adjust.GetProperty("actorRole").GetString());
        Assert.Contains("refund of a broken PC", adjust.GetProperty("summary").GetString());
        One(AuditActions.TariffCreated);
        One(AuditActions.StaffCreated);

        var sessions = await AuditAsync(owner, "player.");
        Assert.All(sessions, e => Assert.StartsWith("player.", e.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task Only_owner_and_manager_read_the_log_and_nobody_changes_it()
    {
        var owner = await _factory.LoginOwnerAsync();
        await owner.PostAsJsonAsync("/api/staff", new { username = "audit-tech", password = "tech-pass-123", role = "Tech" });
        await owner.PostAsJsonAsync("/api/staff", new { username = "audit-manager", password = "manager-pass-1", role = "Manager" });
        var tech = await _factory.LoginAsync("audit-tech", "tech-pass-123");
        var manager = await _factory.LoginAsync("audit-manager", "manager-pass-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/audit")).StatusCode);
        var entries = await AuditAsync(owner);
        var id = entries[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.DeleteAsync($"/api/audit?beforeId={id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/audit/{id}")).StatusCode);

        // Database level: EF refuses, and the SQLite trigger refuses plain SQL
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var entry = await db.AuditLog.FirstAsync();
        entry.Summary = "rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Summary\" = 'x'"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLog\""));
        Assert.True(await db.AuditLog.CountAsync() >= entries.Count);
    }
}

public sealed class PostgresAuditTriggerTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb());

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    [PostgresFact]
    public async Task The_migration_makes_the_audit_log_append_only()
    {
        await using var db = _db.Value.NewContext();
        db.AuditLog.Add(new AuditLogEntity { CreatedUtc = DateTimeOffset.UtcNow, Action = "test.entry" });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Summary\" = 'x'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLog\""));
        Assert.Equal(1, await db.AuditLog.CountAsync());

        // Only the demo reset clears it (TRUNCATE)
        await AuditLogGuard.ClearForDemoResetAsync(db);
        Assert.Equal(0, await db.AuditLog.CountAsync());
    }
}
