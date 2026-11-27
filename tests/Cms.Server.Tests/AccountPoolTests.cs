using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Accounts;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Every log message of the server (all levels), to prove that no login or secret is ever logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>The server with every log message captured (debug level).</summary>
public sealed class LoggedFactory : CmsFactory
{
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Logging:LogLevel:Default", "Debug");
        builder.ConfigureLogging(l => l.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
    }
}

/// <summary>The club game-account pool through the API: roles, leases, the credentials once, and no secret anywhere.</summary>
public sealed class AccountPoolTests : ApiTestBase, IClassFixture<LoggedFactory>
{
    private readonly LoggedFactory _logged;
    private readonly List<string> _answers = new();

    public AccountPoolTests(LoggedFactory factory) : base(factory) => _logged = factory;

    private async Task<JsonElement> Keep(HttpResponseMessage res)
    {
        _answers.Add(await res.Content.ReadAsStringAsync());
        return await JsonAsync(res);
    }

    private async Task<Guid> ZoneAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = "Pool-" + Guid.NewGuid().ToString("N")[..4] };
        db.Zones.Add(zone);
        db.Tariffs.Add(new TariffEntity { Id = Guid.NewGuid(), Name = "T", PricePerHour = 60m, ZoneId = zone.Id, IsDefault = true });
        await db.SaveChangesAsync();
        return zone.Id;
    }

    [Theory]
    [InlineData("GET", "/api/game-accounts")]
    [InlineData("POST", "/api/game-accounts")]
    [InlineData("PUT", "/api/game-accounts/00000000-0000-0000-0000-000000000001/secret")]
    [InlineData("DELETE", "/api/game-accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/game-accounts/00000000-0000-0000-0000-000000000001/release")]
    public async Task The_pool_needs_a_login(string method, string path)
    {
        var res = await _factory.NewClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("Cashier", "POST", "/api/game-accounts")]
    [InlineData("Cashier", "PUT", "/api/game-accounts/00000000-0000-0000-0000-000000000001/secret")]
    [InlineData("Cashier", "PUT", "/api/game-accounts/00000000-0000-0000-0000-000000000001/active")]
    [InlineData("Cashier", "DELETE", "/api/game-accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("Tech", "GET", "/api/game-accounts")]
    [InlineData("Tech", "POST", "/api/game-accounts/00000000-0000-0000-0000-000000000001/release")]
    public async Task Only_owner_and_manager_change_accounts(string role, string method, string path)
    {
        var client = await StaffAsync(role);
        var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { secret = "x" }) });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task A_pc_in_a_session_leases_an_account_and_gets_the_secret_once_and_nothing_leaks()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        string Login(int i) => $"club.steam.{tag}.{i}";
        string Secret(int i) => $"S3cr3t-{tag}-{i}-Ωπ";
        var manager = await StaffAsync("Manager");
        var cashier = await StaffAsync("Cashier");

        // Add: the answer has the masked login and HasSecret only
        var a1 = await Keep(await manager.PostAsJsonAsync("/api/game-accounts", new { platform = "Steam", login = Login(1), secret = Secret(1) }));
        Assert.Equal("steam", a1.GetProperty("platform").GetString());
        Assert.Equal("cl•••.1", a1.GetProperty("loginMask").GetString());
        Assert.True(a1.GetProperty("hasSecret").GetBoolean());
        await Keep(await manager.PostAsJsonAsync("/api/game-accounts", new { platform = "steam", login = Login(2), secret = Secret(2) }));
        var noSecret = await Keep(await manager.PostAsJsonAsync("/api/game-accounts", new { platform = "steam", login = Login(3) }));
        Assert.False(noSecret.GetProperty("hasSecret").GetBoolean());
        var dup = await manager.PostAsJsonAsync("/api/game-accounts", new { platform = "steam", login = Login(1).ToUpperInvariant(), secret = "x" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("account_login_exists", await CodeAsync(dup));
        Assert.Equal("account_bad_platform", await CodeAsync(await manager.PostAsJsonAsync("/api/game-accounts", new { platform = "origin", login = "a", secret = "b" })));
        Assert.Equal("account_bad_secret", await CodeAsync(await manager.PostAsJsonAsync("/api/game-accounts", new { platform = "epic", login = "a", secret = "two\nlines" })));

        // A game that needs a Steam account, three PCs with sessions
        var zone = await ZoneAsync();
        var game = await JsonAsync(await manager.PostAsJsonAsync("/api/games", new { name = "Paid game " + tag, category = "Test", kind = "steam", steamAppId = 271590, accountPlatform = "steam" }));
        Assert.Equal("steam", game.GetProperty("accountPlatform").GetString());
        var gameId = game.GetProperty("id").GetString();
        var plain = await JsonAsync(await manager.PostAsJsonAsync("/api/games", new { name = "Free game " + tag, category = "Test", kind = "steam", steamAppId = 730 }));
        var pcs = new List<TestPc>();
        for (var i = 0; i < 3; i++)
        {
            var pc = await TestPc.AddAsync(_factory, zone);
            pcs.Add(pc);
        }
        // Without a session there is no account
        var early = await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account", new { gameId });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("account_needs_session", await CodeAsync(early));
        var sessions = new List<string>();
        foreach (var pc in pcs)
            sessions.Add((await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 60, paymentMethod = "cash" }))).GetProperty("id").GetString()!);

        Assert.Equal("game_needs_no_account", await CodeAsync(await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account", new { gameId = plain.GetProperty("id").GetString() })));
        var lease = await Keep(await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account", new { gameId }));
        var leaseId = lease.GetProperty("leaseId").GetInt64();
        Assert.False(lease.GetProperty("delivered").GetBoolean());
        // Another agent cannot use the route of this PC, nor fetch the lease through its own route
        Assert.Equal(HttpStatusCode.Forbidden, (await pcs[1].PostAsync($"/api/devices/{pcs[0].Id}/game-account/{leaseId}/credential")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pcs[1].PostAsync($"/api/devices/{pcs[1].Id}/game-account/{leaseId}/credential")).StatusCode);
        // A staff login cannot fetch credentials (the route takes only the device key)
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.PostAsJsonAsync($"/api/devices/{pcs[0].Id}/game-account/{leaseId}/credential", new { })).StatusCode);

        // The agent of the PC gets the credentials once
        var credRes = await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account/{leaseId}/credential");
        Assert.Equal("no-store", credRes.Headers.CacheControl?.ToString());
        var cred = await JsonAsync(credRes);
        var deliveredLogin = cred.GetProperty("login").GetString();
        Assert.Contains(deliveredLogin, new[] { Login(1), Login(2) });
        Assert.Equal(deliveredLogin == Login(1) ? Secret(1) : Secret(2), cred.GetProperty("secret").GetString());
        var again = await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account/{leaseId}/credential");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("credential_delivered", await CodeAsync(again));
        // The same PC asks again (the game started twice): the same lease, already handed out
        var same = await Keep(await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account", new { gameId }));
        Assert.Equal(leaseId, same.GetProperty("leaseId").GetInt64());
        Assert.True(same.GetProperty("delivered").GetBoolean());

        // The second PC gets the other account; the third finds none (the account without a secret is never leased)
        var second = await Keep(await pcs[1].PostAsync($"/api/devices/{pcs[1].Id}/game-account", new { gameId }));
        Assert.NotEqual(lease.GetProperty("loginMask").GetString(), second.GetProperty("loginMask").GetString());
        var none = await pcs[2].PostAsync($"/api/devices/{pcs[2].Id}/game-account", new { gameId });
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Equal("no_free_account", await CodeAsync(none));

        // The cashier sees which PC uses which account (masked) and frees the stuck one of PC 2
        var list = await Keep(await cashier.GetAsync("/api/game-accounts"));
        var used = list.EnumerateArray().Where(a => a.GetProperty("lease").ValueKind == JsonValueKind.Object).ToList();
        Assert.Contains(used, a => a.GetProperty("lease").GetProperty("hostname").GetString() == pcs[0].Hostname && a.GetProperty("lease").GetProperty("delivered").GetBoolean());
        var stuck = used.Single(a => a.GetProperty("lease").GetProperty("hostname").GetString() == pcs[1].Hostname);
        var freed = await Keep(await cashier.PostAsJsonAsync($"/api/game-accounts/{stuck.GetProperty("id").GetString()}/release", new { }));
        Assert.Equal(JsonValueKind.Null, freed.GetProperty("lease").ValueKind);
        Assert.Equal("account_not_leased", await CodeAsync(await cashier.PostAsJsonAsync($"/api/game-accounts/{stuck.GetProperty("id").GetString()}/release", new { })));
        Assert.True((await pcs[2].PostAsync($"/api/devices/{pcs[2].Id}/game-account", new { gameId })).IsSuccessStatusCode);

        // The end of the session ends the lease of PC 1; its account is free again
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/sessions/{sessions[0]}/end", new { }));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var ended = await db.GameAccountLeases.AsNoTracking().SingleAsync(l => l.Id == leaseId);
            Assert.NotNull(ended.EndedUtc);
            Assert.Equal("session_end", ended.EndReason);
        }
        Assert.Equal("lease_not_found", (await pcs[0].PostAsync($"/api/devices/{pcs[0].Id}/game-account/{leaseId}/credential")).StatusCode == HttpStatusCode.NotFound ? "lease_not_found" : "?");

        // Rotate (write-only) and remove (the encrypted data is deleted)
        var accountId = a1.GetProperty("id").GetString();
        var rotated = await Keep(await manager.PutAsJsonAsync($"/api/game-accounts/{accountId}/secret", new { secret = Secret(9) }));
        Assert.True(rotated.GetProperty("hasSecret").GetBoolean());
        var off = await Keep(await manager.PutAsJsonAsync($"/api/game-accounts/{accountId}/active", new { isActive = false }));
        Assert.False(off.GetProperty("isActive").GetBoolean());
        var removed = await Keep(await manager.DeleteAsync($"/api/game-accounts/{accountId}"));
        Assert.True(removed.GetProperty("removed").GetBoolean());
        Assert.False(removed.GetProperty("hasSecret").GetBoolean());

        // Nothing leaks: no login or secret in any staff answer, the audit log, the logs, or the database in clear text
        var secrets = Enumerable.Range(1, 3).Select(Secret).Append(Secret(9)).ToList();
        var logins = Enumerable.Range(1, 3).Select(Login).ToList();
        foreach (var text in _answers)
        {
            Assert.DoesNotContain(secrets, s => text.Contains(s));
            Assert.DoesNotContain(logins, l => text.Contains(l, StringComparison.OrdinalIgnoreCase));
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var audit = await db.AuditLog.AsNoTracking().ToListAsync();
            var auditText = string.Join("\n", audit.Select(a => $"{a.Action} {a.TargetType} {a.TargetId} {a.Summary} {a.DetailsJson}"));
            Assert.Contains("game_account.delivered", auditText);
            Assert.Contains("game_account.leased", auditText);
            Assert.Contains("cl•••.1", auditText);
            Assert.DoesNotContain(secrets, s => auditText.Contains(s));
            Assert.DoesNotContain(logins, l => auditText.Contains(l, StringComparison.OrdinalIgnoreCase));
            var stored = await db.GameAccounts.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(stored, a => secrets.Any(s => (a.SecretCipher ?? "").Contains(s) || a.LoginCipher.Contains(s)) || logins.Any(l => a.LoginCipher.Contains(l)));
            var removedRow = stored.Single(a => a.Id == Guid.Parse(accountId!));
            Assert.Equal(string.Empty, removedRow.LoginCipher);
            Assert.Null(removedRow.SecretCipher);
            // The ciphertext is Data Protection with the pool purpose: the vault reads it, the login cookie protector does not
            var vault = scope.ServiceProvider.GetRequiredService<GameAccountVault>();
            var kept = stored.First(a => a.SecretCipher != null);
            Assert.Contains(vault.TryUnprotect(kept.SecretCipher), secrets);
            var cookie = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware");
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cookie.Unprotect(kept.SecretCipher!));
        }
        var logText = string.Join("\n", _logged.Logs.Lines);
        Assert.True(logText.Length > 0);
        Assert.DoesNotContain(secrets, s => logText.Contains(s));
        Assert.DoesNotContain(logins, l => logText.Contains(l, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Leases at the same moment: one account is never leased twice, one PC never gets two accounts of a platform.</summary>
public abstract class AccountLeaseConcurrencyTestsBase : IDisposable
{
    private readonly Lazy<IParallelDb> _db;
    private readonly GameAccountVault _vault = GameAccountVault.Ephemeral();

    protected AccountLeaseConcurrencyTestsBase(Func<IParallelDb> create) => _db = new Lazy<IParallelDb>(create);

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    private CmsDbContext Ctx() => _db.Value.NewContext();

    private GameAccountService Service(CmsDbContext db) => new(db, _vault, ClubClock.System(), new AuditLogger(db, new HttpContextAccessor()));

    private async Task<(List<Guid> Pcs, Guid Game)> SeedAsync(int pcs, int accounts)
    {
        await using var db = Ctx();
        var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = "Z" };
        db.Add(zone);
        db.Tariffs.Add(new TariffEntity { Id = Guid.NewGuid(), Name = "T", PricePerHour = 60m, ZoneId = zone.Id, IsDefault = true });
        var devices = Enumerable.Range(1, pcs).Select(i => new DeviceEntity
        {
            Id = Guid.NewGuid(), Hostname = $"PC-{i:00}", DeviceKey = "sha256:" + Guid.NewGuid(), Status = "approved", ZoneId = zone.Id
        }).ToList();
        db.Devices.AddRange(devices);
        var game = new GameEntity { Id = Guid.NewGuid(), Name = "Paid", Category = "C", LaunchKind = "steam", SteamAppId = 1, AccountPlatform = "steam", Color = "#123456" };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        foreach (var d in devices)
            Assert.True((await new SessionService(db, new EfDeviceRepository(db)).StartAsync(new Controllers.StartSessionRequest(d.Id, null, null, 60, PaymentMethod: PaymentMethods.Cash))).Ok);
        for (var i = 1; i <= accounts; i++)
            Assert.True((await Service(db).AddAsync(new AddGameAccountRequest("steam", $"acc-{i}", "secret-" + i))).Ok);
        return (devices.Select(d => d.Id).ToList(), game.Id);
    }

    private async Task<long?> LeaseAsync(Guid pc, Guid game)
    {
        await using var db = Ctx();
        try
        {
            var r = await Service(db).LeaseAsync(pc, game);
            return r.Ok ? r.Value!.LeaseId : null;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException or NpgsqlException)
        {
            return null;
        }
    }

    protected async Task Parallel_leases_never_share_an_account()
    {
        var (pcs, game) = await SeedAsync(pcs: 8, accounts: 3);
        // Every PC asks twice at the same moment (the game started twice)
        var results = await Task.WhenAll(pcs.Concat(pcs).Select(pc => LeaseAsync(pc, game)));
        await using var db = Ctx();
        var open = await db.GameAccountLeases.AsNoTracking().Where(l => l.EndedUtc == null).ToListAsync();
        Assert.Equal(3, open.Count);                                    // three accounts, never more leases
        Assert.Equal(3, open.Select(l => l.AccountId).Distinct().Count()); // never one account twice
        Assert.Equal(3, open.Select(l => l.DeviceId).Distinct().Count());  // never two accounts on one PC
        Assert.Equal(3, results.Where(r => r != null).Distinct().Count());
    }

    protected async Task Parallel_credential_requests_hand_out_once()
    {
        var (pcs, game) = await SeedAsync(pcs: 1, accounts: 1);
        var lease = await LeaseAsync(pcs[0], game);
        Assert.NotNull(lease);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = Ctx();
            try { return (await Service(db).DeliverAsync(pcs[0], lease!.Value)).Ok; }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException or NpgsqlException) { return false; }
        }));
        Assert.Equal(1, results.Count(ok => ok));
    }
}

public sealed class SqliteAccountLeaseConcurrencyTests : AccountLeaseConcurrencyTestsBase
{
    public SqliteAccountLeaseConcurrencyTests() : base(() => new SqliteFileDb()) { }

    [Fact] public Task Leases() => Parallel_leases_never_share_an_account();
    [Fact] public Task Credentials() => Parallel_credential_requests_hand_out_once();
}

public sealed class PostgresAccountLeaseConcurrencyTests : AccountLeaseConcurrencyTestsBase
{
    public PostgresAccountLeaseConcurrencyTests() : base(() => new PostgresDb()) { }

    [PostgresFact] public Task Leases() => Parallel_leases_never_share_an_account();
    [PostgresFact] public Task Credentials() => Parallel_credential_requests_hand_out_once();
}

/// <summary>When a lease ends: session end, expiry, logout of the player, a transfer, and the recovery of the server.</summary>
public sealed class AccountLeaseLifecycleTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = BillingTestKit.Clock(BillingTestKit.Local(0, 10));
    private readonly GameAccountVault _vault = GameAccountVault.Ephemeral();
    private readonly Guid _zone = Guid.NewGuid(), _game = Guid.NewGuid();
    private readonly Guid[] _pcs;

    public AccountLeaseLifecycleTests()
    {
        using var db = _testDb.NewContext();
        db.Zones.Add(new ZoneEntity { Id = _zone, Name = "Z" });
        db.Tariffs.Add(new TariffEntity { Id = Guid.NewGuid(), Name = "T", PricePerHour = 60m, ZoneId = _zone, IsDefault = true });
        var devices = Enumerable.Range(1, 3).Select(i => new DeviceEntity { Id = Guid.NewGuid(), Hostname = $"PC-{i}", DeviceKey = "k" + i, Status = "approved", ZoneId = _zone }).ToList();
        db.Devices.AddRange(devices);
        db.Games.Add(new GameEntity { Id = _game, Name = "Paid", Category = "C", LaunchKind = "steam", SteamAppId = 1, AccountPlatform = "steam", Color = "#123456" });
        db.SaveChanges();
        _pcs = devices.Select(d => d.Id).ToArray();
        var service = Accounts(db);
        for (var i = 1; i <= 3; i++) Assert.True(service.AddAsync(new AddGameAccountRequest("steam", $"acc-{i}", "pw-" + i)).GetAwaiter().GetResult().Ok);
    }

    public void Dispose() => _testDb.Dispose();

    private GameAccountService Accounts(CmsDbContext db) => new(db, _vault, _clock, new AuditLogger(db, new HttpContextAccessor()));
    private SessionService Sessions(CmsDbContext db) => new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor()));

    private async Task<(Guid Session, long Lease)> PlayAsync(Guid pc, int minutes = 60)
    {
        await using var db = _testDb.NewContext();
        var s = await Sessions(db).StartAsync(new Controllers.StartSessionRequest(pc, null, null, minutes, PaymentMethod: PaymentMethods.Cash));
        Assert.True(s.Ok, s.Error?.Message);
        var lease = await Accounts(db).LeaseAsync(pc, _game);
        Assert.True(lease.Ok, lease.Error?.Message);
        return (s.Value!.Id, lease.Value!.LeaseId);
    }

    private async Task<GameAccountLeaseEntity> LeaseAsync(long id)
    {
        await using var db = _testDb.NewContext();
        return await db.GameAccountLeases.AsNoTracking().SingleAsync(l => l.Id == id);
    }

    [Fact]
    public async Task A_lease_ends_with_its_session_and_the_account_is_free_again()
    {
        // The end by staff (also the logout of a player ends the session this way)
        var (s1, l1) = await PlayAsync(_pcs[0]);
        await using (var db = _testDb.NewContext()) Assert.True((await Sessions(db).EndAsync(s1)).Ok);
        Assert.Equal("session_end", (await LeaseAsync(l1)).EndReason);

        // The expiry of a prepaid session
        var (_, l2) = await PlayAsync(_pcs[1], minutes: 30);
        _clock.Time().UtcNow = BillingTestKit.Local(0, 10, 31);
        await using (var db = _testDb.NewContext()) Assert.Equal(1, await Sessions(db).CloseExpiredAsync());
        Assert.NotNull((await LeaseAsync(l2)).EndedUtc);

        // A transfer to another PC: the lease of the old PC ends; the new PC can lease again
        var (s3, l3) = await PlayAsync(_pcs[0]);
        await using (var db = _testDb.NewContext())
            Assert.True((await Sessions(db).TransferAsync(s3, new Controllers.TransferSessionRequest(_pcs[2]))).Ok);
        Assert.Equal("transfer", (await LeaseAsync(l3)).EndReason);
        await using (var db = _testDb.NewContext())
        {
            var again = await Accounts(db).LeaseAsync(_pcs[2], _game);
            Assert.True(again.Ok, again.Error?.Message);
            // Recovery: a session that ended while the server was down (written by another tool)
            await db.Sessions.Where(x => x.Id == s3).ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "ended"));
            Assert.Equal(1, await GameAccountService.EndStaleAsync(db, _clock.UtcNow, "recovery"));
            Assert.Equal("recovery", (await LeaseAsync(again.Value!.LeaseId)).EndReason);
            Assert.Equal(0, await GameAccountService.EndStaleAsync(db, _clock.UtcNow, "recovery"));
            // A paused session gets no account
            var s4 = await Sessions(db).StartAsync(new Controllers.StartSessionRequest(_pcs[1], null, null, 60, PaymentMethod: PaymentMethods.Cash));
            Assert.True((await Sessions(db).PauseAsync(s4.Value!.Id)).Ok);
            Assert.Equal("account_needs_session", (await Accounts(db).LeaseAsync(_pcs[1], _game)).Error!.Code);
            Assert.Empty(await db.GameAccountLeases.AsNoTracking().Where(l => l.EndedUtc == null).ToListAsync());
        }
    }

    [Fact]
    public async Task Keys_that_cannot_read_the_secret_hand_out_nothing()
    {
        var (_, lease) = await PlayAsync(_pcs[0]);
        await using var db = _testDb.NewContext();
        var other = new GameAccountService(db, GameAccountVault.Ephemeral(), _clock); // other keys, like a lost key ring
        Assert.Equal("credential_unreadable", (await other.DeliverAsync(_pcs[0], lease)).Error!.Code);
        Assert.Null((await LeaseAsync(lease)).DeliveredUtc);                              // not marked: the right keys still work
        var right = await Accounts(db).DeliverAsync(_pcs[0], lease);
        Assert.True(right.Ok);
        Assert.StartsWith("pw-", right.Value!.Secret);
        Assert.Equal("de•••01", GameAccountService.Mask("demo-steam-01"));
        Assert.Equal("a•••", GameAccountService.Mask("abcd"));
        Assert.Equal("•••", GameAccountService.Mask("ab"));
    }
}
