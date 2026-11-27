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

/// <summary>Post-paid debt: recorded on the player account when the player cannot pay, paid off or written off later.</summary>
public sealed class DebtApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public DebtApiTests(CmsFactory factory) : base(factory) { }

    /// <summary>Moves the start of an active open-ended session back (the session table is not append-only).</summary>
    private async Task PlayedForAsync(string sessionId, int minutes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var id = Guid.Parse(sessionId);
        var start = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartUtc, start));
        await db.SessionSegments.Where(s => s.SessionId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartUtc, start));
    }

    [Fact]
    public async Task Unpaid_post_paid_time_becomes_debt_that_is_paid_off_or_written_off_later()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Debt hall " + Guid.NewGuid().ToString("N")[..4] }));
        var zoneId = Guid.Parse(zone.GetProperty("id").GetString()!);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Debt tariff", pricePerHour = 60, isDefault = false, zoneId }));
        var pc = await AddDeviceAsync(zoneId);
        var pc2 = await AddDeviceAsync(zoneId);
        var player = await PlayerAsync(cashier);
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 20 }));

        // 90 minutes post-paid = 90 ₴; the balance pays 20, the player cannot pay the other 70
        var session = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, userId = player }));
        var sessionId = session.GetProperty("id").GetString()!;
        await PlayedForAsync(sessionId, 90);
        var ended = await JsonAsync(await cashier.PostAsJsonAsync($"/api/sessions/{sessionId}/end", new { paymentMethod = "balance", deskMethod = "debt" }));
        Assert.Equal((90m, 20m, 0m, 70m), (Dec(ended, "totalCost"), Dec(ended, "paidFromBalance"), Dec(ended, "amountDue"), Dec(ended, "debtRecorded")));
        Assert.Equal("balance+debt", ended.GetProperty("paymentMethod").GetString());
        var user = await cashier.GetFromJsonAsync<JsonElement>($"/api/users/{player}");
        Assert.Equal((0m, 70m), (Dec(user, "balance"), Dec(user, "debt")));
        var row = (await cashier.GetFromJsonAsync<JsonElement>($"/api/users/{player}/ledger")).EnumerateArray().First();
        Assert.Equal(("debt", 70m, 0m, 70m), (row.GetProperty("method").GetString(), Dec(row, "debtDelta"), Dec(row, "deskAmount"), Dec(row, "debtAfter")));
        var recorded = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=player.debt_recorded&take=50")).EnumerateArray()
            .Single(e => e.GetProperty("targetId").GetString() == player);
        Assert.Contains("70", recorded.GetProperty("summary").GetString());

        // While the player owes money: no new open-ended session; a guest cannot owe money
        var blocked = await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, userId = player });
        Assert.Equal((HttpStatusCode.Conflict, "player_has_debt"), (blocked.StatusCode, await CodeAsync(blocked)));
        var guest = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc2 }));
        var guestId = guest.GetProperty("id").GetString();
        Assert.Equal("debt_needs_player", await CodeAsync(await cashier.PostAsJsonAsync($"/api/sessions/{guestId}/end", new { deskMethod = "debt" })));
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/sessions/{guestId}/end", new { deskMethod = "card" }));

        // Paid off: cash at the desk, then from the balance; never more than the debt
        Assert.Equal(60m, Dec(await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/debt/pay", new { amount = 10, method = "cash" })), "debt"));
        var tooMuch = await (await cashier.PostAsJsonAsync($"/api/users/{player}/debt/pay", new { amount = 100 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("debt_too_high", "60"), (tooMuch.GetProperty("code").GetString(), tooMuch.GetProperty("args")[0].GetString()));
        Assert.Equal("balance_too_low", await CodeAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/debt/pay", new { amount = 50, method = "balance" })));
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 50, method = "card" }));
        var paid = await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/debt/pay", new { amount = 50, method = "balance" }));
        Assert.Equal((0m, 10m), (Dec(paid, "balance"), Dec(paid, "debt")));

        // Written off: Owner or Manager, with a reason
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/users/{player}/debt/writeoff", new { amount = 10, reason = "Постійний гість" })).StatusCode);
        Assert.Equal("reason_required", await CodeAsync(await manager.PostAsJsonAsync($"/api/users/{player}/debt/writeoff", new { amount = 10 })));
        Assert.Equal(0m, Dec(await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/debt/writeoff", new { amount = 10, reason = "Постійний гість" })), "debt"));

        // The ledger explains the debt; the chain of DebtAfter holds; open-ended sessions work again
        var rows = (await cashier.GetFromJsonAsync<JsonElement>($"/api/users/{player}/ledger")).EnumerateArray().Reverse().ToList();
        Assert.Equal(new[] { "topup", "session_charge", "session_charge", "debt_payment", "topup", "debt_payment", "debt_writeoff" }, rows.Select(r => r.GetProperty("type").GetString()));
        Assert.Equal(new[] { 0m, 0m, 70m, -10m, 0m, -50m, -10m }, rows.Select(r => Dec(r, "debtDelta")));
        Assert.Equal(new[] { 20m, 0m, 0m, 10m, 50m, 0m, 0m }, rows.Select(r => Dec(r, "deskAmount")));
        var check = await cashier.GetFromJsonAsync<JsonElement>($"/api/users/{player}/ledger/check");
        Assert.True(check.GetProperty("ok").GetBoolean(), check.ToString());
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/ledger/reconcile")).GetProperty("ok").GetBoolean());
        var actions = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=player.debt&take=50")).EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == player).Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(new[] { AuditActions.PlayerDebtWrittenOff, AuditActions.PlayerDebtPaid, AuditActions.PlayerDebtPaid, AuditActions.PlayerDebtRecorded }, actions);
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc, userId = player }));
    }

    [Fact]
    public async Task Reconciliation_finds_a_debt_that_the_ledger_does_not_explain()
    {
        var owner = await _factory.LoginOwnerAsync();
        var id = Guid.Parse(await PlayerAsync(owner));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Debt, 15m)); // behind the ledger's back
        var mismatch = Assert.Single(await Ledger.ReconcileAsync(db, id));
        Assert.Equal((15m, 0m), (mismatch.Debt, mismatch.LedgerDebt));
        var check = await owner.GetFromJsonAsync<JsonElement>($"/api/users/{id}/ledger/check");
        Assert.False(check.GetProperty("ok").GetBoolean());
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Debt, 0m)); // the other test checks all players
    }
}
