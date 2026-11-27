using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Events;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The events API: roles (401 without a login, 403 for a cashier on manager actions) and the views.</summary>
public sealed class EventApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public EventApiTests(CmsFactory factory) : base(factory) { }

    /// <summary>Tomorrow at this club hour (Europe/Kyiv, the default club time zone): always in the future.</summary>
    internal static (string Date, string Time) Tomorrow(int hour)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ClubTimeZone.Default).Date.AddDays(1).AddHours(hour);
        return (local.ToString("yyyy-MM-dd"), local.ToString("HH:mm"));
    }

    private async Task<Guid> ZoneAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = name };
        db.Zones.Add(zone);
        await db.SaveChangesAsync();
        return zone.Id;
    }

    [Theory]
    [InlineData("GET", "/api/events")]
    [InlineData("GET", "/api/events/1")]
    [InlineData("POST", "/api/events")]
    [InlineData("POST", "/api/events/1/teams")]
    [InlineData("POST", "/api/events/1/start")]
    [InlineData("PUT", "/api/events/1/matches/1")]
    public async Task The_event_endpoints_need_a_login(string method, string path)
    {
        var res = await _factory.NewClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { })
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("Tech", "GET", "/api/events")]
    [InlineData("Cashier", "POST", "/api/events")]
    [InlineData("Cashier", "PUT", "/api/events/1")]
    [InlineData("Cashier", "POST", "/api/events/1/start")]
    [InlineData("Cashier", "POST", "/api/events/1/finish")]
    [InlineData("Cashier", "POST", "/api/events/1/cancel")]
    [InlineData("Cashier", "POST", "/api/events/1/bracket")]
    [InlineData("Tech", "POST", "/api/events/1/teams")]
    public async Task Manager_actions_are_forbidden_for_a_cashier(string role, string method, string path)
    {
        var client = await StaffAsync(role);
        var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { })
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task A_manager_creates_an_event_and_a_cashier_registers_a_team()
    {
        var zone = await ZoneAsync("Arena-" + Guid.NewGuid().ToString("N")[..4]);
        for (var i = 0; i < 4; i++) await AddDeviceAsync(zone);
        var manager = await StaffAsync("Manager");
        var cashier = await StaffAsync("Cashier");
        var (date, time) = Tomorrow(19);
        var created = await JsonAsync(await manager.PostAsJsonAsync("/api/events", new
        {
            name = "Нічний турнір", zoneId = zone, date, time, durationMinutes = 120, priceMode = "free", entryFee = 20m, teamSize = 2
        }));
        var e = created.GetProperty("event");
        var id = e.GetProperty("id").GetInt64();
        Assert.Equal("scheduled", e.GetProperty("status").GetString());
        Assert.Equal(time, e.GetProperty("startLocal").GetString());
        Assert.Equal(4, e.GetProperty("pcs").GetInt32());
        Assert.Equal(2, e.GetProperty("maxTeams").GetInt32());
        Assert.Equal(4, created.GetProperty("pcs").GetArrayLength());

        var p1 = await PlayerAsync(cashier);
        var p2 = await PlayerAsync(cashier);
        // Not enough real balance for the entry fee
        var poor = await cashier.PostAsJsonAsync($"/api/events/{id}/teams", new { name = "Сови", playerIds = new[] { p1, p2 } });
        Assert.Equal(HttpStatusCode.Conflict, poor.StatusCode);
        Assert.Equal("insufficient_balance_fee", await CodeAsync(poor));
        foreach (var p in new[] { p1, p2 })
            await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{p}/topup", new { amount = 50m, method = "cash" }));
        var detail = await JsonAsync(await cashier.PostAsJsonAsync($"/api/events/{id}/teams", new { name = "Сови", playerIds = new[] { p1, p2 }, captainId = p2 }));
        var team = detail.GetProperty("teams")[0];
        Assert.Equal("Сови", team.GetProperty("name").GetString());
        Assert.Equal(2, team.GetProperty("players").GetArrayLength());
        Assert.True(team.GetProperty("players")[1].GetProperty("captain").GetBoolean());
        Assert.Equal("held", team.GetProperty("players")[0].GetProperty("feeStatus").GetString());
        Assert.Equal(40m, detail.GetProperty("feesHeld").GetDecimal());

        // The list, the holds check, and the cashier cannot start it
        var list = await JsonAsync(await cashier.GetAsync("/api/events"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetInt64() == id && x.GetProperty("teams").GetInt32() == 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/events/{id}/start", new { })).StatusCode);
        var holds = await JsonAsync(await manager.GetAsync("/api/ledger/holds"));
        Assert.True(holds.GetProperty("eventFees").GetProperty("ok").GetBoolean());

        // The manager cancels: the fees go back
        var cancelled = await JsonAsync(await manager.PostAsJsonAsync($"/api/events/{id}/cancel", new { reason = "test" }));
        Assert.Equal("cancelled", cancelled.GetProperty("event").GetProperty("status").GetString());
        var player = await JsonAsync(await cashier.GetAsync($"/api/users/{p1}"));
        Assert.Equal(50m, player.GetProperty("balance").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync("/api/events/999999")).StatusCode);
    }
}

/// <summary>Cashiers register players at the same moment: an event never has more players than PCs, a player never twice.</summary>
public abstract class EventConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;

    protected EventConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    private CmsDbContext Ctx() => _db.Value.NewContext();

    private static EventService Service(CmsDbContext db) =>
        new(db, new EfDeviceRepository(db), ClubClock.System(), new AuditLogger(db, new HttpContextAccessor()), new StaffActor(null, "cashier"));

    private async Task<(long Event, List<Guid> Players)> SeedAsync(int pcs, int players)
    {
        await using var db = Ctx();
        var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = "Z" };
        db.Add(zone);
        db.Devices.AddRange(Enumerable.Range(1, pcs).Select(i => new DeviceEntity
        {
            Id = Guid.NewGuid(), Hostname = $"PC-{i:00}", DeviceKey = "sha256:" + Guid.NewGuid(), Status = "approved", ZoneId = zone.Id
        }));
        var users = Enumerable.Range(1, players).Select(i => new UserEntity { Id = Guid.NewGuid(), Username = $"p{i}-" + Guid.NewGuid().ToString("N")[..6], DisplayName = "P" }).ToList();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        foreach (var u in users)
        {
            await Wallet.CreditAsync(db, u.Id, 100m);
            await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = u.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = 100m, BalanceDelta = 100m }, null, DateTimeOffset.UtcNow);
        }
        var (date, time) = EventApiTests.Tomorrow(18);
        var e = await Service(db).CreateAsync(new CreateEventRequest("Cup", zone.Id, null, date, time, 120, EntryFee: 30m));
        Assert.True(e.Ok, e.Error?.Message);
        return (e.Value!.Id, users.Select(u => u.Id).ToList());
    }

    private async Task<bool> RegisterAsync(long id, Guid player)
    {
        await using var db = Ctx();
        try
        {
            return (await Service(db).RegisterAsync(id, new RegisterTeamRequest(null, new[] { player }))).Ok;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException or NpgsqlException)
        {
            return false;
        }
    }

    private async Task CheckAsync(long id)
    {
        await using var db = Ctx();
        var players = await db.EventPlayers.AsNoTracking().Where(p => p.EventId == id && p.Status == EventMemberStatuses.Active).ToListAsync();
        Assert.Equal(players.Count, players.Select(p => p.DeviceId).Distinct().Count());
        Assert.Equal(players.Count, players.Select(p => p.UserId).Distinct().Count());
        Assert.Empty(await Ledger.ReconcileAsync(db));
        Assert.True((await Holds.EventFeesAsync(db)).Ok);
    }

    protected async Task Parallel_registrations_never_exceed_the_pcs()
    {
        var (id, players) = await SeedAsync(pcs: 3, players: 8);
        var results = await Task.WhenAll(players.Select(p => RegisterAsync(id, p)));
        await using var db = Ctx();
        var active = await db.EventPlayers.AsNoTracking().CountAsync(p => p.EventId == id && p.Status == EventMemberStatuses.Active);
        Assert.Equal(3, active);
        Assert.Equal(results.Count(ok => ok), active);
        await CheckAsync(id);
    }

    protected async Task One_player_is_registered_once()
    {
        var (id, players) = await SeedAsync(pcs: 4, players: 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RegisterAsync(id, players[0])));
        Assert.Equal(1, results.Count(ok => ok));
        await using var db = Ctx();
        Assert.Equal(70m, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == players[0])).Balance); // one fee only
        await CheckAsync(id);
    }
}

public sealed class SqliteEventConcurrencyTests : EventConcurrencyTestsBase
{
    public SqliteEventConcurrencyTests() : base(() => new SqliteFileDb()) { }

    [Fact] public Task Capacity() => Parallel_registrations_never_exceed_the_pcs();
    [Fact] public Task Once() => One_player_is_registered_once();
}

public sealed class PostgresEventConcurrencyTests : EventConcurrencyTestsBase
{
    public PostgresEventConcurrencyTests() : base(() => new PostgresDb()) { }

    [PostgresFact] public Task Capacity() => Parallel_registrations_never_exceed_the_pcs();
    [PostgresFact] public Task Once() => One_player_is_registered_once();
}
