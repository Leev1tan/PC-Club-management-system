using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Shared;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The agent reports an emergency staff PIN unlock (checked offline on the PC) into the audit log.</summary>
public sealed class EmergencyUnlockTests : IClassFixture<EnrollmentFactory>
{
    private readonly EnrollmentFactory _factory;

    public EmergencyUnlockTests(EnrollmentFactory factory) => _factory = factory;

    private sealed record Registration(Guid DeviceId, string DeviceKey, string Status);

    private async Task<Registration> RegisterAsync(string hostname, bool token = true)
    {
        var res = await _factory.NewClient(csrfHeader: false).PostAsJsonAsync("/api/devices/register",
            new { hostname, osVersion = "Windows 11", agentVersion = "2.0", token = token ? EnrollmentFactory.Token : null });
        return (await res.Content.ReadFromJsonAsync<Registration>())!;
    }

    private async Task<HttpResponseMessage> ReportAsync(Guid pathDeviceId, string key, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{pathDeviceId}/events") { Content = JsonContent.Create(body) };
        req.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, key);
        return await _factory.NewClient(csrfHeader: false).SendAsync(req);
    }

    /// <summary>What the launcher writes into the inbox, as the agent reads and sends it.</summary>
    private static object Event(Guid id, DateTimeOffset? at = null, string type = LauncherEvent.StaffUnlock)
    {
        var json = JsonSerializer.Serialize(new LauncherEvent { Id = id, Type = type, OccurredUtc = at ?? DateTimeOffset.UtcNow.AddMinutes(-7), LockReason = nameof(LockReason.AgentSilent), WindowsUser = "player" });
        return JsonSerializer.Deserialize<LauncherEvent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task An_emergency_unlock_reported_by_the_agent_is_audited_once()
    {
        var owner = await _factory.LoginOwnerAsync();
        var pc = await RegisterAsync("STD-07");
        var other = await RegisterAsync("STD-08");
        var pending = await RegisterAsync("STD-09", token: false);
        var id = Guid.NewGuid();

        var first = await ReportAsync(pc.DeviceId, pc.DeviceKey, Event(id));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());
        var again = await ReportAsync(pc.DeviceId, pc.DeviceKey, Event(id)); // the agent sends it again after a lost answer
        Assert.False((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await ReportAsync(pc.DeviceId, other.DeviceKey, Event(Guid.NewGuid()))).StatusCode);   // another PC's key
        Assert.Equal(HttpStatusCode.Forbidden, (await ReportAsync(pending.DeviceId, pending.DeviceKey, Event(Guid.NewGuid()))).StatusCode); // not approved
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync($"/api/devices/{pc.DeviceId}/events", Event(Guid.NewGuid()))).StatusCode); // staff cookie
        Assert.Equal(HttpStatusCode.BadRequest, (await ReportAsync(pc.DeviceId, pc.DeviceKey, Event(Guid.NewGuid(), type: "format_c"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReportAsync(pc.DeviceId, pc.DeviceKey, Event(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-40)))).StatusCode);

        var entries = (await owner.GetFromJsonAsync<JsonElement>($"/api/audit?action={AuditActions.EmergencyUnlock}&take=50")).EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == pc.DeviceId.ToString()).ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("actorUsername").ValueKind);
        Assert.Contains("STD-07", entry.GetProperty("summary").GetString());
        Assert.Contains("agent was not responding", entry.GetProperty("summary").GetString());
        Assert.Contains(id.ToString("N"), entry.GetProperty("detailsJson").GetString());
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/audit/verify")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_flood_of_planted_notes_is_capped_per_pc_and_hour()
    {
        var pc = await RegisterAsync("STD-10");
        for (var i = 0; i < DeviceEvents.MaxPerHour; i++)
            Assert.Equal(HttpStatusCode.OK, (await ReportAsync(pc.DeviceId, pc.DeviceKey, Event(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ReportAsync(pc.DeviceId, pc.DeviceKey, Event(Guid.NewGuid()))).StatusCode);
    }
}
