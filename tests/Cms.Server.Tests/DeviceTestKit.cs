using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Cms.Server.Tests;

/// <summary>A club PC for API tests: a device row with a known key, and agent requests with X-Device-Key.</summary>
public sealed class TestPc
{
    private readonly CmsFactory _factory;

    public Guid Id { get; }
    public string Key { get; }
    public string Hostname { get; }

    private TestPc(CmsFactory factory, Guid id, string key, string hostname)
    {
        _factory = factory;
        Id = id;
        Key = key;
        Hostname = hostname;
    }

    /// <summary>Adds an approved (or pending) PC in the zone and returns it.</summary>
    public static async Task<TestPc> AddAsync(CmsFactory factory, Guid? zoneId = null, string status = DeviceStatuses.Approved, string? hostname = null)
    {
        var key = DeviceKeys.Generate();
        hostname ??= "PC-" + Guid.NewGuid().ToString("N")[..6];
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(), Hostname = hostname, DeviceKey = DeviceKeys.Hash(key), Status = status, ZoneId = zoneId,
            RegisteredUtc = DateTimeOffset.UtcNow, OsVersion = "Windows 11", AgentVersion = "test"
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return new TestPc(factory, device.Id, key, hostname);
    }

    /// <summary>A request of the agent: no cookies, no CSRF header, only the device key.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, Key);
        if (body != null) req.Content = JsonContent.Create(body);
        return await _factory.NewClient(csrfHeader: false).SendAsync(req);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });
    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    /// <summary>Heartbeat with optional extra fields (for example the MAC address); returns the response JSON.</summary>
    public async Task<JsonElement> HeartbeatAsync(string? mac = null)
    {
        var res = await PostAsync("/api/devices/heartbeat",
            new { cpuPercent = 5.0, memPercent = 20.0, activeUser = "player", ip = "10.3.3.150", uptime = "01:00:00", mac });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Pending commands for the PC (marks them delivered, like the agent).</summary>
    public async Task<List<JsonElement>> PollAsync()
    {
        var res = await GetAsync($"/api/devices/{Id}/commands?max=50");
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }
}
