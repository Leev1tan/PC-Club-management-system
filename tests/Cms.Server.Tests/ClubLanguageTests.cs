using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Lock screen language (spec area 12): an Owner setting that the PCs get with the heartbeat, with the currency.</summary>
public sealed class ClubLanguageTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public ClubLanguageTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task Owner_sets_the_lock_screen_language_and_the_pcs_get_it_with_the_currency()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var pc = await TestPc.AddAsync(_factory);
        var pending = await TestPc.AddAsync(_factory, status: DeviceStatuses.Pending);

        // Default: Ukrainian and UAH
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("uk", settings.GetProperty("language").GetString());
        Assert.Equal(new[] { "uk", "en" }, settings.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        var hb = await pc.HeartbeatAsync();
        Assert.Equal("uk", hb.GetProperty("client").GetProperty("language").GetString());
        Assert.Equal("UAH", hb.GetProperty("client").GetProperty("currency").GetString());
        // A PC that waits for approval gets no club settings
        Assert.Equal(JsonValueKind.Null, (await pending.HeartbeatAsync()).GetProperty("client").ValueKind);

        // Only the owner; only uk or en
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync("/api/settings/language", new { code = "en" })).StatusCode);
        var bad = await owner.PutAsJsonAsync("/api/settings/language", new { code = "de" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("bad_language", await CodeAsync(bad));

        try
        {
            var set = await JsonAsync(await owner.PutAsJsonAsync("/api/settings/language", new { code = "EN" }));
            Assert.Equal("en", set.GetProperty("code").GetString());
            await JsonAsync(await owner.PutAsJsonAsync("/api/settings/currency", new { code = "EUR" }));

            hb = await pc.HeartbeatAsync();
            Assert.Equal("en", hb.GetProperty("client").GetProperty("language").GetString());
            Assert.Equal("EUR", hb.GetProperty("client").GetProperty("currency").GetString());

            // Saving the same language again writes no second audit row
            await JsonAsync(await owner.PutAsJsonAsync("/api/settings/language", new { code = "en" }));
            var audit = await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=settings.language_changed");
            var entry = Assert.Single(audit.EnumerateArray());
            Assert.Equal("Lock screen language changed: uk -> en", entry.GetProperty("summary").GetString());
        }
        finally
        {
            await owner.PutAsJsonAsync("/api/settings/language", new { code = "uk" });
            await owner.PutAsJsonAsync("/api/settings/currency", new { code = "UAH" });
        }
        Assert.Equal("uk", (await pc.HeartbeatAsync()).GetProperty("client").GetProperty("language").GetString());
    }
}

public sealed class DemoLanguageTests : IClassFixture<DemoFactory>
{
    private readonly DemoFactory _factory;

    public DemoLanguageTests(DemoFactory factory) => _factory = factory;

    [Fact]
    public async Task Demo_owner_may_switch_the_lock_screen_language()
    {
        var owner = await _factory.LoginAsync("demo-owner", "demo");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/settings/language", new { code = "en" })).StatusCode);
        Assert.Equal("en", (await owner.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("language").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/settings/language", new { code = "uk" })).StatusCode);
    }
}
