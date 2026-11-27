using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Tariff rules, packages and the front-desk session flow through the HTTP API, with the role checks.</summary>
public sealed class SessionApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public SessionApiTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task Packages_and_tariff_rules_are_managed_by_owner_and_manager_only()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);
        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Pkg zone " + Guid.NewGuid().ToString("N")[..4] }));
        var zoneId = zone.GetProperty("id").GetString();

        var package = new { name = "Нічний", zoneId, price = 250, windowStart = "22:00", windowEnd = "08:00" };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/packages", package)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/packages", new { name = "No zone", price = 100, durationMinutes = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/packages", new { name = "Nothing", zoneId, price = 100 })).StatusCode);
        var created = await JsonAsync(await manager.PostAsJsonAsync("/api/packages", package));
        Assert.Equal(("22:00", "08:00"), (created.GetProperty("windowStart").GetString(), created.GetProperty("windowEnd").GetString()));
        var list = await cashier.GetFromJsonAsync<JsonElement>($"/api/packages?zoneId={zoneId}");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/packages")).StatusCode);

        var tariff = new
        {
            name = "Rules " + Guid.NewGuid().ToString("N")[..4], pricePerHour = 60, isDefault = false, zoneId, billingStepMinutes = 5, minimumCharge = 20,
            rules = new[]
            {
                new { name = "Будні вдень", days = new[] { 1, 2, 3, 4 }, start = "08:00", end = "17:00", pricePerHour = 45 },
                new { name = "Ніч", days = new[] { 1, 2, 3, 4, 5, 6, 7 }, start = "22:00", end = "08:00", pricePerHour = 40 },
            }
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/tariffs", tariff)).StatusCode);
        var bad = tariff with { rules = new[] { new { name = "Bad", days = Array.Empty<int>(), start = "08:00", end = "17:00", pricePerHour = 45 } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/tariffs", bad)).StatusCode);
        var t = await JsonAsync(await manager.PostAsJsonAsync("/api/tariffs", tariff));
        Assert.Equal((2, 5), (t.GetProperty("rules").GetArrayLength(), t.GetProperty("billingStepMinutes").GetInt32()));

        // PUT with rules replaces them; cashier may quote but not change
        var tid = t.GetProperty("id").GetString();
        var one = tariff with { rules = new[] { tariff.rules[1] } };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/tariffs/{tid}", one)).StatusCode);
        Assert.Equal(1, (await JsonAsync(await manager.PutAsJsonAsync($"/api/tariffs/{tid}", one))).GetProperty("rules").GetArrayLength());
        var quote = await JsonAsync(await cashier.PostAsJsonAsync("/api/tariffs/quote", new { minutes = 60, tariffId = tid }));
        Assert.True(Dec(quote, "total") >= 20m);

        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=package.&take=50")).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == AuditActions.PackageCreated);
    }

    [Fact]
    public async Task Front_desk_runs_sessions_with_packages_pause_and_transfer_and_tech_cannot()
    {
        var owner = await _factory.LoginOwnerAsync();
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);
        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Hall " + Guid.NewGuid().ToString("N")[..4] }));
        var zoneId = Guid.Parse(zone.GetProperty("id").GetString()!);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Hall tariff", pricePerHour = 60, isDefault = false, zoneId }));
        var pack = await JsonAsync(await owner.PostAsJsonAsync("/api/packages", new { name = "3 години", zoneId, price = 150, durationMinutes = 180 }));
        var pc1 = await AddDeviceAsync(zoneId);
        var pc2 = await AddDeviceAsync(zoneId);
        var player = await PlayerAsync(cashier);
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 200 }));

        var started = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions",
            new { deviceId = pc1, userId = player, packageId = pack.GetProperty("id").GetString() }));
        var id = started.GetProperty("id").GetString();
        Assert.Equal("3 години", started.GetProperty("packageName").GetString());
        Assert.Equal(150m, Dec(started, "totalCost"));

        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync($"/api/sessions/{id}/pause", null)).StatusCode);
        var paused = await JsonAsync(await cashier.PostAsync($"/api/sessions/{id}/pause", null));
        Assert.NotEqual(JsonValueKind.Null, paused.GetProperty("pausedUtc").ValueKind);
        var devices = await cashier.GetFromJsonAsync<JsonElement>("/api/devices");
        var pcView = devices.EnumerateArray().Single(d => d.GetProperty("id").GetString() == pc1.ToString());
        Assert.NotEqual(JsonValueKind.Null, pcView.GetProperty("sessionPausedUtc").ValueKind);
        Assert.Equal("3 години", pcView.GetProperty("sessionLabel").GetString());

        var quote = await JsonAsync(await cashier.GetAsync($"/api/sessions/{id}/transfer-quote?targetDeviceId={pc2}"));
        Assert.Equal(0m, Dec(quote, "surcharge"));
        var moved = await JsonAsync(await cashier.PostAsJsonAsync($"/api/sessions/{id}/transfer", new { targetDeviceId = pc2 }));
        Assert.Equal(pc2.ToString(), moved.GetProperty("deviceId").GetString());
        await JsonAsync(await cashier.PostAsync($"/api/sessions/{id}/resume", null));
        var conflict = await cashier.PostAsync($"/api/sessions/{id}/resume", null);
        Assert.Equal((HttpStatusCode.Conflict, "session_not_paused"), (conflict.StatusCode, await CodeAsync(conflict)));
        var ended = await JsonAsync(await cashier.PostAsync($"/api/sessions/{id}/end", null));
        Assert.Equal("ended", ended.GetProperty("status").GetString());

        var actions = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=session.&take=200")).EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == id).Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(new[] { AuditActions.SessionEnded, AuditActions.SessionResumed, AuditActions.SessionTransferred, AuditActions.SessionPaused, AuditActions.SessionStarted }, actions);

        // Ledger of the whole club: Owner and Manager only
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/ledger")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync($"/api/users/{player}/ledger")).StatusCode);
        var rows = await owner.GetFromJsonAsync<JsonElement>($"/api/ledger?userId={player}");
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("type").GetString() == LedgerTypes.PackagePurchase);
    }
}
