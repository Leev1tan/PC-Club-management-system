using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cms.Server.Auth;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Club currency (spec area 12): an Owner setting that changes the display and the CSV headers, never the amounts.</summary>
public sealed class CurrencyTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public CurrencyTests(CmsFactory factory) : base(factory) { }

    private static async Task<string> CsvAsync(HttpClient client, string path)
    {
        var bytes = await (await client.GetAsync(path)).Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    [Fact]
    public async Task Owner_sets_the_currency_for_the_display_and_the_csv_but_amounts_stay_the_same()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);

        // Default: UAH for every staff member (also Tech, who has no billing access)
        foreach (var client in new[] { owner, manager, cashier, tech })
            Assert.Equal("UAH", (await client.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("currency").GetString());
        var billing = await cashier.GetFromJsonAsync<JsonElement>("/api/billing/settings");
        Assert.Equal(("₴", "UAH"), (billing.GetProperty("currency").GetString(), billing.GetProperty("currencyCode").GetString()));
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("UAH", settings.GetProperty("currency").GetProperty("code").GetString());
        Assert.Equal(new[] { "UAH", "EUR", "USD", "PLN" }, settings.GetProperty("currencies").EnumerateArray().Select(c => c.GetProperty("code").GetString()));

        var player = await PlayerAsync(cashier);
        var before = await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 150, method = "cash" }));

        // Only the owner changes it; unknown codes are refused
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync("/api/settings/currency", new { code = "EUR" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/settings/currency", new { code = "EUR" })).StatusCode);
        var bad = await owner.PutAsJsonAsync("/api/settings/currency", new { code = "GBP" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("bad_currency", await CodeAsync(bad));

        try
        {
            var set = await JsonAsync(await owner.PutAsJsonAsync("/api/settings/currency", new { code = "eur" }));
            Assert.Equal(("EUR", "€"), (set.GetProperty("code").GetString(), set.GetProperty("symbol").GetString()));
            Assert.Equal("EUR", (await cashier.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("currency").GetString());
            Assert.Equal("€", (await cashier.GetFromJsonAsync<JsonElement>("/api/billing/settings")).GetProperty("currency").GetString());

            // Amounts are not converted: the balance is the same number
            var players = await cashier.GetFromJsonAsync<JsonElement>("/api/users");
            var row = players.EnumerateArray().Single(u => u.GetProperty("id").GetString() == player);
            Assert.Equal(Dec(before, "balance"), Dec(row, "balance"));

            // New texts take the new symbol; the old row keeps its own
            await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 20, method = "card" }));
            var ledger = (await owner.GetFromJsonAsync<JsonElement>($"/api/users/{player}/ledger")).EnumerateArray()
                .Select(l => l.GetProperty("description").GetString()).ToList();
            Assert.Contains(ledger, d => d == "Поповнення балансу +20.00 €");
            Assert.Contains(ledger, d => d == "Поповнення балансу +150.00 ₴");

            // The change is audited with both currencies
            var audit = await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=settings.currency_changed");
            var entry = Assert.Single(audit.EnumerateArray());
            Assert.Contains("UAH (₴) -> EUR (€)", entry.GetProperty("summary").GetString());

            // CSV headers: the symbol in Ukrainian, the ISO code in English
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var uk = await CsvAsync(owner, $"/api/ledger/export.csv?from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}");
            Assert.Contains("Сума, €;", uk.Split("\r\n")[0]);
            var en = await CsvAsync(owner, $"/api/ledger/export.csv?from={today}&to={today}&lang=en&sep=comma");
            Assert.Contains("Amount, EUR", en.Split("\r\n")[0]);

            var shift = await JsonAsync(await cashier.PostAsJsonAsync("/api/shifts", new { openingFloat = 100 }));
            var shiftId = shift.GetProperty("id").GetInt64();
            await JsonAsync(await cashier.PostAsJsonAsync($"/api/shifts/{shiftId}/close", new { countedCash = 100 }));
            Assert.StartsWith("Розділ;Показник;Сума, €;Кількість", await CsvAsync(owner, $"/api/shifts/{shiftId}/report.csv"));
            Assert.StartsWith("Section,Item,\"Amount, EUR\",Count", await CsvAsync(owner, $"/api/shifts/{shiftId}/report.csv?lang=en&sep=comma"));

            // PLN and USD symbols
            Assert.Equal("zł", (await JsonAsync(await owner.PutAsJsonAsync("/api/settings/currency", new { code = "PLN" }))).GetProperty("symbol").GetString());
            Assert.Equal("$", (await JsonAsync(await owner.PutAsJsonAsync("/api/settings/currency", new { code = "USD" }))).GetProperty("symbol").GetString());
        }
        finally
        {
            await owner.PutAsJsonAsync("/api/settings/currency", new { code = "UAH" });
        }
        Assert.Equal("UAH", (await tech.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("currency").GetString());
    }
}

/// <summary>The public demo allows the currency setting (display only) but no other settings change.</summary>
public sealed class DemoCurrencyTests : IClassFixture<DemoFactory>
{
    private readonly DemoFactory _factory;

    public DemoCurrencyTests(DemoFactory factory) => _factory = factory;

    [Fact]
    public async Task Demo_owner_may_switch_the_currency_but_not_the_staff_pin()
    {
        var owner = await _factory.LoginAsync("demo-owner", "demo");
        var res = await owner.PutAsJsonAsync("/api/settings/currency", new { code = "PLN" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("PLN", (await owner.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("currency").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/settings/staff-pin", new { pin = "12345678abc" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/settings/currency", new { code = "UAH" })).StatusCode);
    }
}
