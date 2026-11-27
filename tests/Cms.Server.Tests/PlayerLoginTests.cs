using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Player login on the lock screen (spec area 1) through the device endpoints, with lockout and audit.</summary>
public sealed class PlayerLoginTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public PlayerLoginTests(CmsFactory factory) : base(factory) { }

    private const string Pin = "4321";

    private async Task<(HttpClient Owner, string Zone)> ClubAsync()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = (await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Login " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetString()!;
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "T " + Guid.NewGuid().ToString("N")[..4], pricePerHour = 60, zoneId = zone }));
        return (owner, zone);
    }

    private async Task<string> PlayerAsync(HttpClient owner, decimal topUp)
    {
        var username = "pl" + Guid.NewGuid().ToString("N")[..10];
        var player = await JsonAsync(await owner.PostAsJsonAsync("/api/users/register", new { username, password = Pin, displayName = "Player " + username[..5] }));
        if (topUp > 0) await JsonAsync(await owner.PostAsJsonAsync($"/api/users/{player.GetProperty("id").GetString()}/topup", new { amount = topUp, method = "cash" }));
        return username;
    }

    private static Task<HttpResponseMessage> LoginAsync(TestPc pc, string username, string secret, object? offer = null) =>
        pc.PostAsync($"/api/devices/{pc.Id}/player-login", offer == null ? new { username, secret } :
            new Dictionary<string, object?>(((IEnumerable<KeyValuePair<string, object?>>)JsonSerializer.SerializeToElement(offer).EnumerateObject()
                .Select(p => new KeyValuePair<string, object?>(p.Name, p.Value))).Append(new("username", username)).Append(new("secret", secret))));

    private async Task<List<JsonElement>> AuditAsync(HttpClient owner, string action) =>
        (await owner.GetFromJsonAsync<JsonElement>($"/api/audit?action={action}&take=500")).EnumerateArray().ToList();

    [Fact]
    public async Task A_player_logs_in_on_the_pc_and_plays_from_the_balance()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var username = await PlayerAsync(owner, 100);

        var offers = await JsonAsync(await pc.GetAsync($"/api/devices/{pc.Id}/login-offers"));
        Assert.Equal(("UAH", 60m), (offers.GetProperty("currency").GetString(), Dec(offers.GetProperty("tariff"), "pricePerHourNow")));
        Assert.Equal(new[] { 60m, 120m, 180m }, offers.GetProperty("minutes").EnumerateArray().Select(m => Dec(m, "price")));

        var res = await LoginAsync(pc, username.ToUpperInvariant() + " ", Pin);
        var login = await JsonAsync(res);
        Assert.True(login.GetProperty("fromBalance").GetBoolean());
        var end = login.GetProperty("endUtc").GetDateTimeOffset();
        Assert.InRange((end - DateTimeOffset.UtcNow).TotalMinutes, 98, 101); // 100 ₴ at 60 ₴/h

        // The agent gets the end and an unlock; the heartbeat tells the lock screen who plays and the tariff
        var commands = await pc.PollAsync();
        Assert.Equal(new[] { "session_set", "unlock" }, commands.Select(c => c.GetProperty("type").GetString()));
        var info = (await pc.HeartbeatAsync()).GetProperty("session");
        Assert.Equal((username, true), (info.GetProperty("username").GetString(), info.GetProperty("fromBalance").GetBoolean()));
        Assert.StartsWith("T ", info.GetProperty("tariff").GetString());
        Assert.Equal(100m, Dec(info, "balance"));

        Assert.Contains(await AuditAsync(owner, "player.login"), a => a.GetProperty("summary").GetString()!.Contains($"@{username} logged in on {pc.Hostname}"));
        var started = (await AuditAsync(owner, "session.started")).First(a => a.GetProperty("summary").GetString()!.Contains(pc.Hostname));
        Assert.Contains("from the balance", started.GetProperty("summary").GetString());

        // The PC is busy now; the player ends the session on the PC, paid from the balance
        Assert.Equal(HttpStatusCode.Conflict, (await LoginAsync(pc, username, Pin)).StatusCode);
        var logout = await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/player-logout"));
        Assert.True(Dec(logout, "cost") >= 0m);
        Assert.Contains(await AuditAsync(owner, "player.logout"), a => a.GetProperty("summary").GetString()!.Contains($"@{username} ended the session"));
        Assert.True((await JsonAsync(await owner.GetAsync("/api/ledger/reconcile"))).GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await pc.HeartbeatAsync()).GetProperty("session").ValueKind);
    }

    [Fact]
    public async Task Prepaid_minutes_and_packages_are_paid_from_the_balance()
    {
        var (owner, zone) = await ClubAsync();
        var package = await JsonAsync(await owner.PostAsJsonAsync("/api/packages", new { name = "2 год", zoneId = zone, price = 99, durationMinutes = 120 }));
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var rich = await PlayerAsync(owner, 300);

        var offers = await JsonAsync(await pc.GetAsync($"/api/devices/{pc.Id}/login-offers"));
        Assert.Contains(offers.GetProperty("packages").EnumerateArray(), p => p.GetProperty("name").GetString() == "2 год" && Dec(p, "price") == 99m);

        var login = await JsonAsync(await LoginAsync(pc, rich, Pin, new { offer = "package", packageId = package.GetProperty("id").GetString() }));
        Assert.False(login.GetProperty("fromBalance").GetBoolean());
        Assert.Equal(201m, Dec(login, "balance"));
        await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/player-logout"));

        var poor = await PlayerAsync(owner, 30);
        var refused = await LoginAsync(pc, poor, Pin, new { offer = "minutes", minutes = 60 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("insufficient_balance", await CodeAsync(refused));
        Assert.Contains(await AuditAsync(owner, "player.login"), a => a.GetProperty("summary").GetString()!.EndsWith("no session: insufficient_balance"));
        Assert.Equal("bad_offer", await CodeAsync(await LoginAsync(pc, poor, Pin, new { offer = "minutes", minutes = 45 })));
        Assert.Equal("bad_offer", await CodeAsync(await LoginAsync(pc, poor, Pin, new { offer = "package" })));
        Assert.Equal(JsonValueKind.Null, (await pc.HeartbeatAsync()).GetProperty("session").ValueKind);

        // A session of the desk for a guest cannot be ended on the PC
        await JsonAsync(await owner.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 30, paymentMethod = "cash" }));
        var guest = await pc.PostAsync($"/api/devices/{pc.Id}/player-logout");
        Assert.Equal(HttpStatusCode.Conflict, guest.StatusCode);
        Assert.Equal("session_no_player", await CodeAsync(guest));
    }

    [Fact]
    public async Task A_player_plays_on_one_pc_at_a_time()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var second = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var username = await PlayerAsync(owner, 200);
        await JsonAsync(await LoginAsync(pc, username, Pin));
        var busy = await LoginAsync(second, username, Pin, new { offer = "minutes", minutes = 60 });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        var body = await busy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("player_busy", pc.Hostname), (body.GetProperty("code").GetString(), body.GetProperty("args")[0].GetString()));
        // A session of the desk for the same player elsewhere blocks a login too
        await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/player-logout"));
        var players = await owner.GetFromJsonAsync<JsonElement>("/api/users");
        var id = players.EnumerateArray().Single(u => u.GetProperty("username").GetString() == username).GetProperty("id").GetString();
        await JsonAsync(await owner.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, userId = id, durationMinutes = 30 }));
        Assert.Equal("player_busy", await CodeAsync(await LoginAsync(second, username, Pin)));
    }

    [Fact]
    public async Task Wrong_names_and_secrets_get_the_same_answer_and_lock_the_account_after_five_failures()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var other = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var username = await PlayerAsync(owner, 100);
        var ghost = "ghost" + Guid.NewGuid().ToString("N")[..6];

        var wrong = await LoginAsync(pc, username, "0000");
        var unknown = await LoginAsync(pc, ghost, "0000");
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (wrong.StatusCode, unknown.StatusCode));
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        var failed = await AuditAsync(owner, "player.login_failed");
        Assert.Contains(failed, a => a.GetProperty("summary").GetString() == $"Failed player login on {pc.Hostname} for @{username}");
        Assert.Contains(failed, a => a.GetProperty("summary").GetString() == $"Failed player login on {pc.Hostname} for an unknown user name");
        Assert.DoesNotContain(failed, a => a.GetRawText().Contains(ghost));

        for (var i = 0; i < 4; i++) await LoginAsync(other, username, "0000"); // 5 failures of the account, on two PCs
        for (var i = 0; i < 4; i++) await LoginAsync(other, ghost, "0000");
        var locked = await LoginAsync(pc, username, Pin); // the right PIN does not help now
        var lockedGhost = await LoginAsync(pc, ghost, "0000");
        Assert.Equal((HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests), (locked.StatusCode, lockedGhost.StatusCode));
        var lockedBody = await locked.Content.ReadAsStringAsync();
        Assert.Equal(lockedBody, await lockedGhost.Content.ReadAsStringAsync()); // the same for a name that does not exist
        var lockedJson = JsonDocument.Parse(lockedBody).RootElement;
        Assert.Equal("login_locked", lockedJson.GetProperty("code").GetString());
        Assert.Equal("15", lockedJson.GetProperty("args")[0].GetString());
        Assert.Contains(await AuditAsync(owner, "player.login_locked"), a => a.GetProperty("summary").GetString() == $"Player login locked for 15 min: account @{username}");

        // Another account still logs in on the same PC
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(pc, await PlayerAsync(owner, 50), Pin)).StatusCode);
    }

    [Fact]
    public async Task One_pc_is_locked_after_ten_failures_with_any_names()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var other = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(pc, $"spray{i}-{Guid.NewGuid():N}"[..14], "1111")).StatusCode);
        var username = await PlayerAsync(owner, 100);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(pc, username, Pin)).StatusCode);
        Assert.Contains(await AuditAsync(owner, "player.login_locked"), a => a.GetProperty("summary").GetString()!.EndsWith($"PC {pc.Hostname}"));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(other, username, Pin)).StatusCode);
    }

    [Fact]
    public async Task A_manager_clears_the_lockout_of_a_name_and_of_a_pc_and_it_is_audited()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var second = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var sprayed = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var username = await PlayerAsync(owner, 100);
        var ghost = "ghost" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 5; i++) await LoginAsync(pc, username, "0000");
        for (var i = 0; i < 5; i++) await LoginAsync(second, ghost, "0000");
        for (var i = 0; i < 10; i++) await LoginAsync(sprayed, $"spray{i}-{Guid.NewGuid():N}"[..14], "1111");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(sprayed, await PlayerAsync(owner, 10), Pin)).StatusCode);

        // A cashier and a technician may not see or clear lockouts
        var cashier = await StaffAsync("Cashier");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/player-lockouts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await StaffAsync("Tech")).PostAsJsonAsync("/api/player-lockouts/clear", new { username })).StatusCode);

        var manager = await StaffAsync("Manager");
        var list = await JsonAsync(await manager.GetAsync("/api/player-lockouts"));
        var accounts = list.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Contains(accounts, a => a.GetProperty("username").GetString() == username);
        // The name that is no account is listed only with its id (it can be a typed password)
        var limiter = _factory.Services.GetRequiredService<Cms.Server.Devices.PlayerLoginLimiter>();
        var unknown = Assert.Single(accounts, a => a.GetProperty("id").GetString() == limiter.AccountId(ghost));
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("username").ValueKind);
        Assert.DoesNotContain(ghost, list.GetRawText());
        Assert.Contains(list.GetProperty("devices").EnumerateArray(), d => d.GetProperty("hostname").GetString() == sprayed.Hostname);

        await JsonAsync(await manager.PostAsJsonAsync("/api/player-lockouts/clear", new { username = username.ToUpperInvariant() }));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(pc, username, Pin)).StatusCode);
        await JsonAsync(await manager.PostAsJsonAsync("/api/player-lockouts/clear", new { id = unknown.GetProperty("id").GetString() }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(second, ghost, "0000")).StatusCode); // counted again from zero
        await JsonAsync(await owner.PostAsJsonAsync("/api/player-lockouts/clear", new { deviceId = sprayed.Id }));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(sprayed, await PlayerAsync(owner, 10), Pin)).StatusCode);
        Assert.Equal("lockout_not_found", await CodeAsync(await owner.PostAsJsonAsync("/api/player-lockouts/clear", new { deviceId = sprayed.Id })));

        var cleared = await AuditAsync(owner, "player.login_lockout_cleared");
        Assert.Contains(cleared, a => a.GetProperty("summary").GetString() == $"Player login lockout cleared for account @{username}");
        Assert.Contains(cleared, a => a.GetProperty("summary").GetString() == "Player login lockout cleared for an unknown user name");
        Assert.Contains(cleared, a => a.GetProperty("summary").GetString() == $"Player login lockout cleared for PC {sprayed.Hostname}");
        Assert.DoesNotContain(cleared, a => a.GetRawText().Contains(ghost));
        // Nothing derived from the unknown name is in the audit log: not the opaque id, not a plain hash of it
        var unknownRow = Assert.Single(cleared, a => a.GetProperty("summary").GetString() == "Player login lockout cleared for an unknown user name");
        Assert.Equal(Cms.Server.Controllers.PlayerLockoutsController.UnknownNameTarget, unknownRow.GetProperty("targetId").GetString());
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ghost))).ToLowerInvariant();
        var all = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?take=500")).GetRawText().ToLowerInvariant();
        Assert.DoesNotContain(ghost, all);
        Assert.DoesNotContain(sha[..12], all);
        Assert.DoesNotContain(unknown.GetProperty("id").GetString()!, all);
    }

    [Fact]
    public void The_opaque_id_of_a_name_differs_between_server_processes()
    {
        var a = new Cms.Server.Devices.PlayerLoginLimiter();
        var b = new Cms.Server.Devices.PlayerLoginLimiter();
        Assert.Equal(a.AccountId("1234"), a.AccountId(" 1234 "));
        Assert.NotEqual(a.AccountId("1234"), b.AccountId("1234"));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("1234"))).ToLowerInvariant();
        Assert.DoesNotContain(a.AccountId("1234"), sha);
    }

    [Fact]
    public async Task Only_the_agent_of_an_approved_pc_logs_in_for_its_own_pc()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var other = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var pending = await TestPc.AddAsync(_factory, Guid.Parse(zone), DeviceStatuses.Pending);
        var username = await PlayerAsync(owner, 100);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/devices/{pc.Id}/player-login", new { username, secret = Pin })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/devices/{pc.Id}/login-offers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(pending, username, Pin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync($"/api/devices/{pc.Id}/player-login", new { username, secret = Pin })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.NewClient().PostAsJsonAsync($"/api/devices/{pc.Id}/player-login", new { username, secret = Pin })).StatusCode);

        // The PC is busy: no account is checked, nothing is counted
        await JsonAsync(await owner.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 30, paymentMethod = "cash" }));
        for (var i = 0; i < 6; i++) Assert.Equal("device_busy", await CodeAsync(await LoginAsync(pc, username, "0000")));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(other, username, Pin)).StatusCode);
    }
}
