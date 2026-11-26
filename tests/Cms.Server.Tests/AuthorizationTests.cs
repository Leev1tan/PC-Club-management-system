using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Xunit;

namespace Cms.Server.Tests;

public sealed class AuthorizationTests : IClassFixture<CmsFactory>
{
    private readonly CmsFactory _factory;

    public AuthorizationTests(CmsFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateStaffAndLoginAsync(string role)
    {
        var owner = await _factory.LoginOwnerAsync();
        var username = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..20];
        var res = await owner.PostAsJsonAsync("/api/staff", new { username, displayName = role, password = "staff-pass-123", role });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await _factory.LoginAsync(username, "staff-pass-123");
    }

    [Theory]
    [InlineData("GET", "/api/devices")]
    [InlineData("GET", "/api/sessions")]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/tariffs")]
    [InlineData("GET", "/api/zones")]
    [InlineData("GET", "/api/staff")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("POST", "/api/tariffs")]
    [InlineData("POST", "/api/sessions")]
    public async Task Admin_endpoints_need_a_login(string method, string path)
    {
        var client = _factory.NewClient();
        var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { }) : null
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Public_endpoints_stay_open()
    {
        var client = _factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/demo")).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(status.GetProperty("setupRequired").GetBoolean());
        Assert.False(status.GetProperty("demoMode").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("demoAccounts").ValueKind);
    }

    [Fact]
    public async Task Login_sets_a_secure_cookie_and_wrong_passwords_fail()
    {
        var client = _factory.NewClient();
        var bad = await client.PostAsJsonAsync("/api/auth/login", new { username = CmsFactory.OwnerUsername, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/auth/login", new { username = "OWNER ", password = CmsFactory.OwnerPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AuthConstants.CookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(StaffRoles.Owner, me.GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_need_the_csrf_header_and_the_same_origin()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = new { name = "CSRF zone", color = "#ffffff", sortOrder = 1 };

        // Same cookie, but no custom header (what a cross-site form would send)
        var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/zones") { Content = JsonContent.Create(zone) };
        noHeader.Headers.Remove(AuthConstants.CsrfHeader);
        owner.DefaultRequestHeaders.Remove(AuthConstants.CsrfHeader);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(noHeader)).StatusCode);
        owner.DefaultRequestHeaders.Add(AuthConstants.CsrfHeader, "1");

        var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/zones") { Content = JsonContent.Create(zone) };
        foreign.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(foreign)).StatusCode);

        var sameOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/zones") { Content = JsonContent.Create(zone) };
        sameOrigin.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(sameOrigin)).StatusCode);

        // Login without the header is refused too (login CSRF)
        var bare = _factory.NewClient(csrfHeader: false);
        var login = await bare.PostAsJsonAsync("/api/auth/login", new { username = CmsFactory.OwnerUsername, password = CmsFactory.OwnerPassword });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task Cors_allows_no_foreign_origin_by_default()
    {
        var client = _factory.NewClient();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/devices");
        preflight.Headers.Add("Origin", "http://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        var res = await client.SendAsync(preflight);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cashier_cannot_change_tariffs_delete_or_see_revenue_or_staff()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zoneRes = await owner.PostAsJsonAsync("/api/zones", new { name = "Hall", color = "#00ff00" });
        var zoneId = (await zoneRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var cashier = await CreateStaffAndLoginAsync(StaffRoles.Cashier);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/tariffs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/tariffs", new { name = "Cheap", pricePerHour = 1, isDefault = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.DeleteAsync($"/api/zones/{zoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/sessions/revenue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/staff")).StatusCode);
        // Front desk work is allowed (404: the device does not exist, but authorization passed)
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Tech_manages_devices_only()
    {
        var tech = await CreateStaffAndLoginAsync(StaffRoles.Tech);
        Assert.Equal(HttpStatusCode.OK, (await tech.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tech.GetAsync("/api/zones")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tech.PutAsJsonAsync($"/api/devices/{Guid.NewGuid()}/zone", new { zoneId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/tariffs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync("/api/zones", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Manager_changes_tariffs_but_not_staff()
    {
        var manager = await CreateStaffAndLoginAsync(StaffRoles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/tariffs", new { name = "Evening", pricePerHour = 50, isDefault = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/sessions/revenue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/staff")).StatusCode);
    }

    [Fact]
    public async Task Deactivating_or_demoting_an_account_ends_its_logins()
    {
        var owner = await _factory.LoginOwnerAsync();
        var res = await owner.PostAsJsonAsync("/api/staff", new { username = "leaving", password = "leaving-pass-1", role = "Manager" });
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var manager = await _factory.LoginAsync("leaving", "leaving-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/sessions/revenue")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/staff/{id}", new { role = "Cashier" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.GetAsync("/api/sessions/revenue")).StatusCode);

        var cashier = await _factory.LoginAsync("leaving", "leaving-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/sessions/revenue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/staff/{id}", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/sessions")).StatusCode);
        var again = await _factory.NewClient().PostAsJsonAsync("/api/auth/login", new { username = "leaving", password = "leaving-pass-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_removed()
    {
        var owner = await _factory.LoginOwnerAsync();
        var me = await owner.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var res = await owner.PutAsJsonAsync($"/api/staff/{me.GetProperty("id").GetString()}", new { role = "Manager" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode); // this fixture has exactly one Owner
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_login_for_a_while()
    {
        var owner = await _factory.LoginOwnerAsync();
        await owner.PostAsJsonAsync("/api/staff", new { username = "guessme", password = "guess-pass-123", role = "Cashier" });

        var attacker = _factory.NewClient();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsJsonAsync("/api/auth/login", new { username = "guessme", password = $"wrong-{i}" })).StatusCode);

        var locked = await attacker.PostAsJsonAsync("/api/auth/login", new { username = "guessme", password = "guess-pass-123" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Staff_passwords_are_salted_hashes()
    {
        var a = Passwords.Hash("same-password");
        var b = Passwords.Hash("same-password");
        Assert.NotEqual(a, b);
        Assert.Equal(PasswordCheck.Success, Passwords.Verify(a, "same-password"));
        Assert.Equal(PasswordCheck.Failed, Passwords.Verify(a, "other-password"));
        Assert.NotNull(Passwords.ValidateStaffPassword("short"));
        await Task.CompletedTask;
    }
}

public sealed class SetupScreenTests : IClassFixture<SetupFactory>
{
    private readonly SetupFactory _factory;

    public SetupScreenTests(SetupFactory factory) => _factory = factory;

    [Fact]
    public async Task Setup_needs_the_code_from_the_data_directory_and_works_only_once()
    {
        var client = _factory.NewClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("setupRequired").GetBoolean());

        var codeFile = Path.Combine(_factory.DataDirectory, "setup-code.txt");
        var code = (await File.ReadAllTextAsync(codeFile)).Trim();

        var wrong = await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = "AAAA-BBBB-CCCC", username = "boss", password = "boss-pass-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var weak = await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "boss", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code.ToLowerInvariant(), username = "boss", displayName = "Boss", password = "boss-pass-123" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Owner", me.GetProperty("role").GetString());
        Assert.False(File.Exists(codeFile));

        var again = await _factory.NewClient().PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "intruder", password = "intruder-pass-1" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(status.GetProperty("setupRequired").GetBoolean());
    }
}

public sealed class DemoLoginTests : IClassFixture<DemoFactory>
{
    private readonly DemoFactory _factory;

    public DemoLoginTests(DemoFactory factory) => _factory = factory;

    [Fact]
    public async Task Demo_mode_shows_and_accepts_the_demo_staff_accounts()
    {
        var client = _factory.NewClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("demoMode").GetBoolean());
        var accounts = status.GetProperty("demoAccounts").EnumerateArray().Select(a => a.GetProperty("username").GetString()).ToList();
        Assert.Contains("demo-owner", accounts);
        Assert.Contains("demo-cashier", accounts);

        var cashier = await _factory.LoginAsync("demo-cashier", "demo");
        Assert.Equal(20, (await cashier.GetFromJsonAsync<JsonElement>("/api/devices")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/tariffs", new { name = "x", pricePerHour = 1 })).StatusCode);

        // Visitors cannot lock each other out of the public demo
        var owner = await _factory.LoginAsync("demo-owner", "demo");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/staff", new { username = "x1", password = "xxxxxxxxxx", role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/auth/password", new { currentPassword = "demo", newPassword = "changed-pass" })).StatusCode);
    }
}
