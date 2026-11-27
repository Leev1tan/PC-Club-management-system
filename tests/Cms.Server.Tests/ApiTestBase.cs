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

/// <summary>HTTP helpers for API tests: staff of a role, players, devices, JSON and error codes.</summary>
public abstract class ApiTestBase
{
    protected readonly CmsFactory _factory;

    protected ApiTestBase(CmsFactory factory) => _factory = factory;

    protected async Task<HttpClient> StaffAsync(string role)
    {
        var owner = await _factory.LoginOwnerAsync();
        var username = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..20];
        var res = await owner.PostAsJsonAsync("/api/staff", new { username, displayName = role, password = "staff-pass-123", role });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await _factory.LoginAsync(username, "staff-pass-123");
    }

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    protected static async Task<string?> CodeAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    protected async Task<string> PlayerAsync(HttpClient client)
    {
        var player = await JsonAsync(await client.PostAsJsonAsync("/api/users/register",
            new { username = "pl-" + Guid.NewGuid().ToString("N")[..10], password = "1357" }));
        return player.GetProperty("id").GetString()!;
    }

    protected async Task<Guid> AddDeviceAsync(Guid? zoneId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var device = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = DeviceKeys.Hash(Guid.NewGuid().ToString()), Status = "approved", ZoneId = zoneId };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    protected static decimal Dec(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

}
