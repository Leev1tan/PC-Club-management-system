using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The Z-report of a shift with every kind of money movement reconciles with the ledger; CSV exports.</summary>
[Collection(ShiftCollection.Name)]
public sealed class ZReportTests : ApiTestBase
{
    public ZReportTests(ShiftFactory factory) : base(factory) { }

    private static decimal D(JsonElement e, params string[] path)
    {
        foreach (var p in path) e = e.GetProperty(p);
        return e.GetDecimal();
    }

    [Fact]
    public async Task The_z_report_adds_up_and_reconciles_with_the_ledger_rows_of_the_shift()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var other = await StaffAsync(StaffRoles.Cashier);
        var current = await owner.GetFromJsonAsync<JsonElement>("/api/shifts/current");
        if (current.GetProperty("shift").ValueKind == JsonValueKind.Object)
            await JsonAsync(await owner.PostAsJsonAsync($"/api/shifts/{current.GetProperty("shift").GetProperty("id").GetInt64()}/close", new { countedCash = 0 }));

        var zone = await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Z hall " + Guid.NewGuid().ToString("N")[..4] }));
        var zoneId = Guid.Parse(zone.GetProperty("id").GetString()!);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Z tariff", pricePerHour = 60, isDefault = false, zoneId }));
        var pack = await JsonAsync(await owner.PostAsJsonAsync("/api/packages", new { name = "3 години", zoneId, price = 150, durationMinutes = 180 }));
        var pc1 = await AddDeviceAsync(zoneId);
        var pc2 = await AddDeviceAsync(zoneId);
        var pc3 = await AddDeviceAsync(zoneId);
        await JsonAsync(await manager.PutAsJsonAsync("/api/billing/settings", new { bonusTopUpRules = new[] { new { minAmount = 500, percent = 10 } } }));
        var monster = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", new { name = "Monster", category = "Енергетики", price = 75, trackStock = true, initialStock = 10 }))).GetProperty("id").GetString();
        var water = (await JsonAsync(await manager.PostAsJsonAsync("/api/products", new { name = "Вода", category = "Вода", price = 20, trackStock = false }))).GetProperty("id").GetString();

        var shift = await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat = 1000 }));
        var id = shift.GetProperty("id").GetInt64();
        var player = await PlayerAsync(cashier);
        var debtor = await PlayerAsync(cashier);

        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 500, method = "cash" }));   // +50 bonus
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 200, method = "card" }));
        var guest = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc1, durationMinutes = 60, paymentMethod = "cash" }));
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc2, userId = player, packageId = pack.GetProperty("id").GetString() })); // 50 bonus + 100 balance
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = monster, quantity = 2 } }, paymentMethod = "cash" }));
        await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new[] { new { productId = water, quantity = 2 } }, userId = player }));
        var card = await JsonAsync(await cashier.PostAsJsonAsync("/api/sales", new { items = new object[] { new { productId = monster, quantity = 1 }, new { productId = water, quantity = 1 } }, paymentMethod = "card" }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/sales/{card.GetProperty("id").GetInt64()}/refund", new { reason = "Помилка касира" }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/refund", new { amount = 100, method = "cash", reason = "Повернення частини" }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{player}/adjust", new { amount = 30, reason = "Компенсація збою" }));

        // Debt: 60 minutes post-paid, nothing on the balance, 20 ₴ paid back in cash
        var open = await JsonAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc3, userId = debtor }));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var sid = Guid.Parse(open.GetProperty("id").GetString()!);
            var start = DateTimeOffset.UtcNow.AddMinutes(-60);
            await db.Sessions.Where(s => s.Id == sid).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartUtc, start));
            await db.SessionSegments.Where(s => s.SessionId == sid).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartUtc, start));
        }
        var ended = await JsonAsync(await cashier.PostAsJsonAsync($"/api/sessions/{open.GetProperty("id").GetString()}/end", new { deskMethod = "debt" }));
        var debt = D(ended, "debtRecorded");
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{debtor}/debt/pay", new { amount = 20, method = "cash" }));
        await JsonAsync(await manager.PostAsJsonAsync($"/api/shifts/{id}/payout", new { amount = 100, reason = "Інкасація" }));

        // Blind close: no report for the cashier while the shift is open; the manager sees it so far
        var blind = await cashier.GetAsync($"/api/shifts/{id}/report");
        Assert.Equal((HttpStatusCode.Forbidden, "shift_report_after_close"), (blind.StatusCode, await CodeAsync(blind)));
        Assert.True((await JsonAsync(await manager.GetAsync($"/api/shifts/{id}/report"))).GetProperty("ledger").GetProperty("reconciled").GetBoolean());

        var guestCash = D(guest, "totalCost");
        var expected = 1000m + 500m + guestCash + 150m - 100m + 20m - 100m;
        var closed = await JsonAsync(await cashier.PostAsJsonAsync($"/api/shifts/{id}/close", new { countedCash = expected - 5m }));
        Assert.Equal((expected, -5m), (D(closed, "expectedCash"), D(closed, "variance")));

        var z = await JsonAsync(await cashier.GetAsync($"/api/shifts/{id}/report"));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/shifts/{id}/report")).StatusCode);

        // Sections
        Assert.Equal((guestCash + 150m + debt, guestCash, 0m, 100m, 50m, debt), (D(z, "time", "total"), D(z, "time", "byMethod", "cash"),
            D(z, "time", "byMethod", "card"), D(z, "time", "byMethod", "balance"), D(z, "time", "byMethod", "bonus"), D(z, "time", "byMethod", "debt")));
        Assert.Equal((150m, 95m, 40m), (D(z, "bar", "sales", "cash"), D(z, "bar", "sales", "card"), D(z, "bar", "sales", "balance")));
        Assert.Equal((95m, 190m, 3, 1), (D(z, "bar", "refunds", "card"), D(z, "bar", "net"), z.GetProperty("bar").GetProperty("receipts").GetInt32(),
            z.GetProperty("bar").GetProperty("refundReceipts").GetInt32()));
        var items = z.GetProperty("bar").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => (i.GetProperty("quantity").GetInt32(), D(i, "amount")));
        Assert.Equal((2, 150m), items["Monster"]);
        Assert.Equal((2, 40m), items["Вода"]);
        Assert.Equal((500m, 200m, 2), (D(z, "topUps", "byMethod", "cash"), D(z, "topUps", "byMethod", "card"), z.GetProperty("topUps").GetProperty("count").GetInt32()));
        Assert.Equal(100m, D(z, "balanceRefunds", "byMethod", "cash"));
        Assert.Equal((50m, 50m), (D(z, "bonus", "issued"), D(z, "bonus", "spent")));
        Assert.Equal((debt, 20m), (D(z, "debt", "recorded"), D(z, "debt", "paid", "cash")));
        Assert.Equal(100m, D(z, "payouts", "total"));
        Assert.Equal(30m, D(z, "adjustments", "balancePlus"));
        Assert.Equal((1000m, expected, expected - 5m, -5m), (D(z, "cash", "opening"), D(z, "cash", "expected"), D(z, "cash", "counted"), D(z, "cash", "variance")));
        Assert.True(z.GetProperty("ledger").GetProperty("reconciled").GetBoolean());

        // Against the ledger itself
        var rows = (await owner.GetFromJsonAsync<JsonElement>("/api/ledger?take=500")).EnumerateArray()
            .Where(r => r.TryGetProperty("shiftId", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt64() == id).ToList();
        Assert.Equal(rows.Count, z.GetProperty("ledger").GetProperty("rows").GetInt32());
        Assert.Equal(rows.Where(r => r.GetProperty("method").GetString() == "cash").Sum(r => D(r, "deskAmount")), D(z, "cash", "net"));
        Assert.Equal(rows.Where(r => r.GetProperty("method").GetString() == "card").Sum(r => D(r, "deskAmount")), D(z, "card", "net"));
        Assert.Equal(rows.Where(r => r.GetProperty("type").GetString() is "session_charge" or "package_purchase").Sum(r => D(r, "amount")), D(z, "time", "total"));
        var staff = z.GetProperty("byStaff").EnumerateArray().ToList();
        Assert.Equal(D(z, "cash", "net"), staff.Sum(s => D(s, "cashNet")));
        Assert.Equal(rows.Count, staff.Sum(s => s.GetProperty("rows").GetInt32()));
        Assert.Equal(2, staff.Count); // cashier and manager

        // CSV for Excel: UTF-8 BOM, Ukrainian text, ';' and decimal comma by default; English with ',' and '.'
        var uk = await cashier.GetAsync($"/api/shifts/{id}/report.csv");
        Assert.Equal("text/csv", uk.Content.Headers.ContentType!.MediaType);
        var bytes = await uk.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("Виручка за час", text);
        Assert.Contains($"Готівка в касі;Розбіжність;-5,00", text);
        Assert.Contains("Журнал операцій;Збігається з журналом;так", text);
        var en = Encoding.UTF8.GetString(await (await manager.GetAsync($"/api/shifts/{id}/report.csv?lang=en&sep=comma")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Cash in the drawer,Variance,-5.00", en);
        Assert.Contains("Bar,Bar revenue (net),190.00", en);
    }

    [Fact]
    public async Task The_ledger_export_covers_a_date_range_for_owner_and_manager_and_never_runs_formulas()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var formula = await JsonAsync(await cashier.PostAsJsonAsync("/api/users/register", new { username = "=2+3*" + Guid.NewGuid().ToString("N")[..4], password = "1357" }));
        var formulaId = formula.GetProperty("id").GetString();
        await JsonAsync(await manager.PostAsJsonAsync($"/api/users/{formulaId}/adjust", new { amount = 15, reason = "=HYPERLINK(\"x\")" }));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/ledger/export.csv?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"/api/ledger/export.csv?from={today:yyyy-MM-dd}&to={today.AddDays(-2):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/ledger/export.csv?from=2025-01-01&to=2026-06-01")).StatusCode);

        var res = await manager.GetAsync($"/api/ledger/export.csv?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("ledger-", res.Content.Headers.ContentDisposition!.FileName);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Id;Час (клуб);Тип;Код типу", lines[0]);
        var row = Assert.Single(lines, l => l.Contains("adjustment") && l.Contains(formula.GetProperty("username").GetString()!));
        Assert.Contains(";'=2+3*", row);                   // the user name is text, not a formula
        Assert.Contains("'=HYPERLINK(\"\"x\"\")", row);    // quoted and escaped
        Assert.Contains(";15,00;15,00;0,00;0,00;0,00;15,00;", row); // amount, balance change ... with a decimal comma

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var all = await db.Ledger.CountAsync();
        Assert.Equal(all + 1, lines.Length); // header + every row of these days
        var past = Encoding.UTF8.GetString(await (await owner.GetAsync("/api/ledger/export.csv?from=2020-01-01&to=2020-12-31&lang=en&sep=comma")).Content.ReadAsByteArrayAsync());
        Assert.Single(past.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)); // header only
        Assert.StartsWith("﻿Id,Time (club),Type,Type code", past);
    }
}
