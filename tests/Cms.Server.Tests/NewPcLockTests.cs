using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// A club PC is locked unless a session runs on it or staff unlocked it. The server locks a PC that joins the club
/// (enrollment token or approval) and marks a staff unlock without a session (v0.8.0-beta.1: a new PC was unlocked).
/// </summary>
public sealed class NewPcLockApiTests : IClassFixture<EnrollmentFactory>
{
    private readonly EnrollmentFactory _factory;

    public NewPcLockApiTests(EnrollmentFactory factory) => _factory = factory;

    private sealed record Registration(Guid DeviceId, string DeviceKey, string Status);

    private async Task<Registration> RegisterAsync(string hostname, string? token = null, Guid? deviceId = null, string? deviceKey = null)
    {
        var res = await _factory.NewClient(csrfHeader: false).PostAsJsonAsync("/api/devices/register",
            new { hostname, osVersion = "Windows 11", agentVersion = "0.8.1", token, deviceId, deviceKey });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<Registration>())!;
    }

    /// <summary>The commands that the agent gets now: "type" or "type {payload}".</summary>
    private async Task<List<string>> PollAsync(Registration pc)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/devices/{pc.DeviceId}/commands?max=50");
        req.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, pc.DeviceKey);
        var res = await _factory.NewClient(csrfHeader: false).SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(c => c.GetProperty("type").GetString() +
                         (c.GetProperty("payload").ValueKind == JsonValueKind.Null ? "" : " " + c.GetProperty("payload").GetRawText()))
            .ToList();
    }

    private async Task AddActiveSessionAsync(Guid deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        db.Sessions.Add(new SessionEntity { Id = Guid.NewGuid(), DeviceId = deviceId, StartUtc = DateTimeOffset.UtcNow.AddMinutes(-5), Status = "active" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Registration_with_the_token_locks_the_pc()
    {
        var pc = await RegisterAsync("PC-NEWLOCK", EnrollmentFactory.Token);
        Assert.Equal(DeviceStatuses.Approved, pc.Status);
        Assert.Equal(new[] { CommandTypes.Lock }, await PollAsync(pc));

        // The agent starts again with its key: no new command
        await RegisterAsync("PC-NEWLOCK", deviceId: pc.DeviceId, deviceKey: pc.DeviceKey);
        Assert.Empty(await PollAsync(pc));

        // Reinstall with the token: a new key, locked again
        var again = await RegisterAsync("PC-NEWLOCK", EnrollmentFactory.Token);
        Assert.Equal(pc.DeviceId, again.DeviceId);
        Assert.Equal(new[] { CommandTypes.Lock }, await PollAsync(again));
    }

    [Fact]
    public async Task Approval_locks_the_pc()
    {
        var pc = await RegisterAsync("PC-APPROVELOCK");
        Assert.Equal(DeviceStatuses.Pending, pc.Status);
        var owner = await _factory.LoginOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/devices/{pc.DeviceId}/approve", null)).StatusCode);
        Assert.Equal(new[] { CommandTypes.Lock }, await PollAsync(pc));
    }

    [Fact]
    public async Task A_pc_with_an_active_session_is_not_locked_on_registration_or_approval()
    {
        var enrolled = await RegisterAsync("PC-BUSY1", EnrollmentFactory.Token);
        Assert.Equal(new[] { CommandTypes.Lock }, await PollAsync(enrolled));
        await AddActiveSessionAsync(enrolled.DeviceId);
        var reinstalled = await RegisterAsync("PC-BUSY1", EnrollmentFactory.Token);
        Assert.Empty(await PollAsync(reinstalled));

        var pending = await RegisterAsync("PC-BUSY2");
        await AddActiveSessionAsync(pending.DeviceId);
        var owner = await _factory.LoginOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/devices/{pending.DeviceId}/approve", null)).StatusCode);
        Assert.Empty(await PollAsync(pending));
    }

    [Fact]
    public async Task A_staff_unlock_without_a_session_tells_the_agent_so()
    {
        var pc = await RegisterAsync("PC-STAFFUNLOCK", EnrollmentFactory.Token);
        await PollAsync(pc);
        var owner = await _factory.LoginOwnerAsync();

        // A payload of the staff is not passed on: the server decides the reason
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/devices/{pc.DeviceId}/commands",
            new { type = "unlock", payload = new { reason = "session" } })).StatusCode);
        Assert.Equal(new[] { "unlock {\"reason\":\"staff\"}" }, await PollAsync(pc));

        // While a session runs, the unlock is an unlock for the session (no payload: the session end applies)
        await AddActiveSessionAsync(pc.DeviceId);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/devices/{pc.DeviceId}/commands",
            new { type = "unlock", payload = new { reason = "staff" } })).StatusCode);
        Assert.Equal(new[] { CommandTypes.Unlock }, await PollAsync(pc));
    }
}

/// <summary>The same rule through the controller on SQLite and PostgreSQL.</summary>
public abstract class NewPcLockDbTestsBase : IDisposable
{
    private const string Token = "db-test-enrollment-token-0123456789";
    private readonly Lazy<IParallelDb> _db;

    protected NewPcLockDbTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    private static DevicesController Controller(CmsDbContext db) =>
        new(new EfDeviceRepository(db, enrollment: new DeviceEnrollmentOptions { EnrollmentToken = Token }), db);

    private async Task<DeviceRegistrationResponse> RegisterAsync(string hostname, string? token)
    {
        await using var db = _db.Value.NewContext();
        var result = await Controller(db).Register(new DeviceRegistrationRequest(hostname, "Windows 11", "0.8.1", token));
        return Assert.IsType<DeviceRegistrationResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private async Task<List<string>> CommandsAsync(Guid deviceId)
    {
        await using var db = _db.Value.NewContext();
        return (await db.Commands.AsNoTracking().Where(c => c.DeviceId == deviceId).ToListAsync())
            .OrderBy(c => c.CreatedUtc).Select(c => c.Type).ToList();
    }

    private async Task AddActiveSessionAsync(Guid deviceId)
    {
        await using var db = _db.Value.NewContext();
        db.Sessions.Add(new SessionEntity { Id = Guid.NewGuid(), DeviceId = deviceId, StartUtc = DateTimeOffset.UtcNow.AddMinutes(-5), Status = "active" });
        await db.SaveChangesAsync();
    }

    protected async Task Registration_and_approval_lock_a_pc_without_a_session()
    {
        var enrolled = await RegisterAsync("PC-DB-1", Token);
        Assert.Equal(new[] { CommandTypes.Lock }, await CommandsAsync(enrolled.DeviceId));

        var pending = await RegisterAsync("PC-DB-2", null);
        Assert.Empty(await CommandsAsync(pending.DeviceId));
        await using (var db = _db.Value.NewContext())
            Assert.IsType<OkResult>(await Controller(db).Approve(pending.DeviceId));
        Assert.Equal(new[] { CommandTypes.Lock }, await CommandsAsync(pending.DeviceId));
    }

    protected async Task Registration_and_approval_do_not_lock_a_pc_with_a_session()
    {
        var enrolled = await RegisterAsync("PC-DB-3", Token);
        await AddActiveSessionAsync(enrolled.DeviceId);
        await RegisterAsync("PC-DB-3", Token); // reinstall during the session
        Assert.Equal(new[] { CommandTypes.Lock }, await CommandsAsync(enrolled.DeviceId)); // only the first one

        var pending = await RegisterAsync("PC-DB-4", null);
        await AddActiveSessionAsync(pending.DeviceId);
        await using (var db = _db.Value.NewContext())
            Assert.IsType<OkResult>(await Controller(db).Approve(pending.DeviceId));
        Assert.Empty(await CommandsAsync(pending.DeviceId));
    }
}

public sealed class SqliteNewPcLockTests : NewPcLockDbTestsBase
{
    public SqliteNewPcLockTests() : base(() => new SqliteFileDb()) { }

    [Fact] public Task Locked_without_session() => Registration_and_approval_lock_a_pc_without_a_session();
    [Fact] public Task Not_locked_with_session() => Registration_and_approval_do_not_lock_a_pc_with_a_session();
}

public sealed class PostgresNewPcLockTests : NewPcLockDbTestsBase
{
    public PostgresNewPcLockTests() : base(() => new PostgresDb()) { }

    [PostgresFact] public Task Locked_without_session() => Registration_and_approval_lock_a_pc_without_a_session();
    [PostgresFact] public Task Not_locked_with_session() => Registration_and_approval_do_not_lock_a_pc_with_a_session();
}
