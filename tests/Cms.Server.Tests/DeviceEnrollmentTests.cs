using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

public sealed class EnrollmentFactory : CmsFactory
{
    public const string Token = "test-enrollment-token-0123456789";

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Devices:EnrollmentToken"] = Token,
    };
}

public sealed class DeviceEnrollmentTests : IClassFixture<EnrollmentFactory>
{
    private readonly EnrollmentFactory _factory;

    public DeviceEnrollmentTests(EnrollmentFactory factory) => _factory = factory;

    private sealed record Registration(Guid DeviceId, string DeviceKey, string Status);

    /// <summary>An agent: no cookies, no CSRF header, only its device key.</summary>
    private HttpClient Agent() => _factory.NewClient(csrfHeader: false);

    private async Task<(HttpStatusCode Status, Registration? Body)> RegisterAsync(string hostname, string? token = null, Guid? deviceId = null, string? deviceKey = null)
    {
        var res = await Agent().PostAsJsonAsync("/api/devices/register",
            new { hostname, osVersion = "Windows 11", agentVersion = "2.0", token, deviceId, deviceKey });
        return (res.StatusCode, res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<Registration>() : null);
    }

    private async Task<HttpStatusCode> HeartbeatAsync(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/heartbeat")
        {
            Content = JsonContent.Create(new { cpuPercent = 1.0, memPercent = 2.0, activeUser = "kiosk", ip = "10.3.3.150", uptime = "01:00:00" })
        };
        req.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, key);
        return (await Agent().SendAsync(req)).StatusCode;
    }

    private async Task<HttpStatusCode> PollAsync(Guid deviceId, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/devices/{deviceId}/commands");
        req.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, key);
        return (await Agent().SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task A_new_pc_without_token_waits_for_approval()
    {
        var (status, pc) = await RegisterAsync("PC-PENDING");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(DeviceStatuses.Pending, pc!.Status);
        Assert.Equal(HttpStatusCode.OK, await HeartbeatAsync(pc.DeviceKey));
        Assert.Equal(HttpStatusCode.Forbidden, await PollAsync(pc.DeviceId, pc.DeviceKey));

        var tech = await _factory.LoginOwnerAsync();
        var list = await tech.GetFromJsonAsync<JsonElement>("/api/devices");
        Assert.Contains(list.EnumerateArray(), d => d.GetProperty("id").GetGuid() == pc.DeviceId && d.GetProperty("enrollment").GetString() == "pending");

        Assert.Equal(HttpStatusCode.OK, (await tech.PostAsync($"/api/devices/{pc.DeviceId}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await PollAsync(pc.DeviceId, pc.DeviceKey));
    }

    [Fact]
    public async Task Registering_a_known_hostname_never_returns_its_key()
    {
        var (_, original) = await RegisterAsync("PC-VICTIM", EnrollmentFactory.Token);
        Assert.Equal(DeviceStatuses.Approved, original!.Status);

        // An impostor with the same hostname but no key and no token
        var (status, impostor) = await RegisterAsync("PC-VICTIM");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(original.DeviceId, impostor!.DeviceId);
        Assert.NotEqual(original.DeviceKey, impostor.DeviceKey);
        Assert.Equal(DeviceStatuses.Pending, impostor.Status);
        Assert.Equal(HttpStatusCode.Forbidden, await PollAsync(impostor.DeviceId, impostor.DeviceKey));

        // Wrong token, and someone else's device id with the impostor key
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync("PC-VICTIM", "wrong-token")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync("PC-VICTIM", deviceId: original.DeviceId, deviceKey: impostor.DeviceKey + "x")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, await PollAsync(original.DeviceId, impostor.DeviceKey));
        Assert.Equal(HttpStatusCode.OK, await PollAsync(original.DeviceId, original.DeviceKey));
    }

    [Fact]
    public async Task Reregistration_needs_the_current_key_and_reinstall_needs_the_token()
    {
        var (_, pc) = await RegisterAsync("PC-REINSTALL", EnrollmentFactory.Token);

        var (status, same) = await RegisterAsync("PC-REINSTALL", deviceId: pc!.DeviceId, deviceKey: pc.DeviceKey);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(pc.DeviceId, same!.DeviceId);
        Assert.Equal(pc.DeviceKey, same.DeviceKey);

        // Windows reinstalled, the agent lost its key: the token keeps the record but issues a new key
        var (_, reinstalled) = await RegisterAsync("PC-REINSTALL", EnrollmentFactory.Token);
        Assert.Equal(pc.DeviceId, reinstalled!.DeviceId);
        Assert.NotEqual(pc.DeviceKey, reinstalled.DeviceKey);
        Assert.Equal(HttpStatusCode.Unauthorized, await HeartbeatAsync(pc.DeviceKey));
        Assert.Equal(HttpStatusCode.OK, await HeartbeatAsync(reinstalled.DeviceKey));
    }

    [Fact]
    public async Task Device_keys_and_staff_cookies_do_not_mix()
    {
        var (_, pc) = await RegisterAsync("PC-MIX", EnrollmentFactory.Token);

        var agentOnStaffApi = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        agentOnStaffApi.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, pc!.DeviceKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Agent().SendAsync(agentOnStaffApi)).StatusCode);

        var owner = await _factory.LoginOwnerAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync($"/api/devices/{pc.DeviceId}/commands")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await HeartbeatAsync("not-a-key"));
    }

    [Fact]
    public async Task Revoked_pc_loses_access_and_staff_cannot_send_session_time()
    {
        var (_, pc) = await RegisterAsync("PC-REVOKE", EnrollmentFactory.Token);
        var owner = await _factory.LoginOwnerAsync();

        var free = await owner.PostAsJsonAsync($"/api/devices/{pc!.DeviceId}/commands", new { type = "session_set", payload = new { endUtc = DateTimeOffset.UtcNow.AddDays(1) } });
        Assert.Equal(HttpStatusCode.BadRequest, free.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/devices/{pc.DeviceId}/commands", new { type = "lock" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/devices/{pc.DeviceId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await HeartbeatAsync(pc.DeviceKey));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync("PC-REVOKE", deviceId: pc.DeviceId, deviceKey: pc.DeviceKey)).Status);
    }

    [Fact]
    public async Task The_database_stores_only_key_hashes_and_legacy_keys_are_upgraded()
    {
        var (_, pc) = await RegisterAsync("PC-HASH", EnrollmentFactory.Token);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var stored = await db.Devices.SingleAsync(d => d.Id == pc!.DeviceId);
        Assert.Equal(DeviceKeys.Hash(pc!.DeviceKey), stored.DeviceKey);
        Assert.DoesNotContain(pc.DeviceKey, stored.DeviceKey);

        // A database of an earlier version stored the plain key; an old agent keeps working after the upgrade
        var legacy = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-OLD", DeviceKey = "legacy-plain-key", Status = DeviceStatuses.Approved };
        db.Devices.Add(legacy);
        await db.SaveChangesAsync();
        Assert.Equal(1, await DeviceKeys.HashLegacyKeysAsync(db));
        Assert.Equal(HttpStatusCode.OK, await HeartbeatAsync("legacy-plain-key"));
        Assert.Equal(HttpStatusCode.OK, await PollAsync(legacy.Id, "legacy-plain-key"));
    }
}

public sealed class TokenOnlyFactory : CmsFactory
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Devices:EnrollmentToken"] = EnrollmentFactory.Token,
        ["Devices:AllowPendingRegistration"] = "false",
    };
}

public sealed class TokenOnlyEnrollmentTests : IClassFixture<TokenOnlyFactory>
{
    private readonly TokenOnlyFactory _factory;

    public TokenOnlyEnrollmentTests(TokenOnlyFactory factory) => _factory = factory;

    [Fact]
    public async Task Without_the_approval_queue_a_token_is_required()
    {
        var client = _factory.NewClient(csrfHeader: false);
        var res = await client.PostAsJsonAsync("/api/devices/register", new { hostname = "PC-X", osVersion = "w", agentVersion = "1" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        res = await client.PostAsJsonAsync("/api/devices/register", new { hostname = "PC-X", osVersion = "w", agentVersion = "1", token = EnrollmentFactory.Token });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
