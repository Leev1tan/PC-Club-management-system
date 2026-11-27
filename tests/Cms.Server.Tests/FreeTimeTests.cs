using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Free PC time: Owner or Manager only, with a reason, no money, audited as session.free_time.</summary>
public sealed class FreeTimeTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public FreeTimeTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task Free_time_needs_a_manager_and_a_reason_and_is_audited_with_its_value()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Free hall " + Guid.NewGuid().ToString("N")[..4] }));
        var zoneId = Guid.Parse(zone.GetProperty("id").GetString()!);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Free tariff", pricePerHour = 60, isDefault = false, zoneId }));
        var pc = await AddDeviceAsync(zoneId);
        var player = await PlayerAsync(cashier);

        var byCashier = await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, userId = player, durationMinutes = 60, paymentMethod = "free", reason = "Турнір" });
        Assert.Equal((HttpStatusCode.Forbidden, "free_needs_manager"), (byCashier.StatusCode, await CodeAsync(byCashier)));
        Assert.Equal("reason_required", await CodeAsync(await manager.PostAsJsonAsync("/api/sessions", new { deviceId = pc, durationMinutes = 60, paymentMethod = "free" })));
        Assert.Equal("free_needs_prepaid", await CodeAsync(await manager.PostAsJsonAsync("/api/sessions", new { deviceId = pc, paymentMethod = "free", reason = "Турнір" })));

        var session = await JsonAsync(await manager.PostAsJsonAsync("/api/sessions",
            new { deviceId = pc, userId = player, durationMinutes = 60, paymentMethod = "free", reason = "Переможець турніру" }));
        Assert.Equal((0m, "free"), (Dec(session, "totalCost"), session.GetProperty("paymentMethod").GetString()));
        var id = session.GetProperty("id").GetString();

        // Extensions: paid by the cashier, free only by the manager with a reason
        Assert.Equal("free_needs_manager", await CodeAsync(await cashier.PostAsJsonAsync($"/api/sessions/{id}/extend", new { minutes = 30, paymentMethod = "free", reason = "ще" })));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/sessions/{id}/extend", new { minutes = 30, paymentMethod = "free", reason = "Збій ПК, компенсація" }));

        // No money moved: no ledger row for the player
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonElement>($"/api/users/{player}/ledger")).GetArrayLength());
        var free = (await owner.GetFromJsonAsync<JsonElement>($"/api/audit?action={AuditActions.SessionFreeTime}&take=50")).EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == id).ToList();
        Assert.Equal(2, free.Count);
        Assert.Contains("Збій ПК, компенсація", free[0].GetProperty("summary").GetString());
        Assert.Contains("worth 30", free[0].GetProperty("summary").GetString());
        Assert.Contains("Переможець турніру", free[1].GetProperty("summary").GetString());
        Assert.Contains("worth 60", free[1].GetProperty("summary").GetString());
        Assert.All(free, e => Assert.Equal("Manager", e.GetProperty("actorRole").GetString()));
    }
}
