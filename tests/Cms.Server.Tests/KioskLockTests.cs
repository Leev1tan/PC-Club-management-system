using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Shared;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Lock decision of Cms.Launcher (Cms.Shared): fails closed, staff PIN only.</summary>
public sealed class LockScreenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentLockSnapshot Locked = new(true, Now.AddMinutes(-10));
    private static readonly AgentLockSnapshot Unlocked = new(false, Now.AddMinutes(-10));

    [Fact]
    public void A_silent_agent_keeps_the_pc_locked()
    {
        var c = new LockScreenController();
        Assert.False(c.Evaluate(Unlocked, Now.AddSeconds(-2), Now).Locked);

        var stale = c.Evaluate(Unlocked, Now.AddSeconds(-16), Now);
        Assert.True(stale.Locked);
        Assert.Equal(LockReason.AgentSilent, stale.Reason);
        Assert.True(c.Evaluate(Unlocked, null, Now).Locked);              // no heartbeat file
        Assert.True(c.Evaluate(Unlocked, Now.AddMinutes(5), Now).Locked); // clock moved back
    }

    [Fact]
    public void Missing_state_keeps_the_pc_locked()
    {
        var d = new LockScreenController().Evaluate(null, Now, Now);
        Assert.True(d.Locked);
        Assert.Equal(LockReason.NoAgentState, d.Reason);
    }

    [Fact]
    public void Staff_unlock_lasts_until_the_next_lock_event()
    {
        var c = new LockScreenController();
        Assert.True(c.Evaluate(Locked, null, Now).Locked);
        c.BeginStaffOverride(Locked);
        Assert.False(c.Evaluate(Locked, null, Now).Locked);                  // agent silent: emergency unlock works
        Assert.False(c.Evaluate(Locked, Now, Now.AddSeconds(5)).Locked);     // agent back, same lock

        var newLock = Locked with { LockChangedUtc = Now.AddMinutes(1) };
        Assert.True(c.Evaluate(newLock, Now.AddMinutes(1), Now.AddMinutes(1)).Locked);
        Assert.False(c.StaffOverride);
    }

    [Fact]
    public void An_agent_unlock_ends_the_staff_override()
    {
        var c = new LockScreenController();
        c.BeginStaffOverride(Locked);
        Assert.False(c.Evaluate(Unlocked, Now, Now).Locked);
        Assert.False(c.StaffOverride);
        Assert.True(c.Evaluate(Locked, Now, Now).Locked);
    }

    [Fact]
    public void Pin_attempts_are_limited()
    {
        var limiter = new PinAttemptLimiter();
        for (var i = 0; i < PinAttemptLimiter.FreeAttempts - 1; i++)
        {
            limiter.RecordFailure(Now);
            Assert.Null(limiter.WaitTime(Now));
        }
        limiter.RecordFailure(Now);
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.WaitTime(Now));
        limiter.RecordFailure(Now.AddSeconds(31));
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.WaitTime(Now.AddSeconds(31)));
        limiter.RecordSuccess();
        Assert.Null(limiter.WaitTime(Now.AddSeconds(31)));
    }

    [Fact]
    public void Staff_pin_hash_is_salted_and_verifies()
    {
        var a = StaffPinHash.Create("8462-club");
        var b = StaffPinHash.Create("8462-club");
        Assert.NotEqual(a, b);
        Assert.StartsWith("pbkdf2-sha256$600000$", a);
        Assert.True(StaffPinHash.Verify("8462-club", a));
        Assert.False(StaffPinHash.Verify("8462-clu", a));
        Assert.False(StaffPinHash.Verify("1234", "pbkdf2-sha256$1$AAAA$AAAA")); // too few iterations
        Assert.NotNull(StaffPinHash.Validate("1234"));
        Assert.NotNull(StaffPinHash.Validate("11111111"));
        Assert.Null(StaffPinHash.Validate("8462-club"));
    }
}

public sealed class StaffPinDeliveryTests : IClassFixture<EnrollmentFactory>
{
    private readonly EnrollmentFactory _factory;

    public StaffPinDeliveryTests(EnrollmentFactory factory) => _factory = factory;

    private async Task<JsonElement> HeartbeatAsync(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/heartbeat")
        {
            Content = JsonContent.Create(new { cpuPercent = 1.0, memPercent = 2.0, ip = "10.3.3.150", uptime = "00:10:00" })
        };
        req.Headers.Add("X-Device-Key", key);
        var res = await _factory.NewClient(csrfHeader: false).SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(Guid Id, string Key)> RegisterAsync(string host, string? token)
    {
        var res = await _factory.NewClient(csrfHeader: false).PostAsJsonAsync("/api/devices/register",
            new { hostname = host, osVersion = "w", agentVersion = "2", token });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("deviceId").GetGuid(), body.GetProperty("deviceKey").GetString()!);
    }

    [Fact]
    public async Task Owner_sets_the_pin_and_only_approved_pcs_get_its_hash()
    {
        var approved = await RegisterAsync("PC-PIN-1", EnrollmentFactory.Token);
        var pending = await RegisterAsync("PC-PIN-2", null);
        var owner = await _factory.LoginOwnerAsync();

        Assert.Equal(JsonValueKind.Null, (await HeartbeatAsync(approved.Key)).GetProperty("staffPinHash").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/settings/staff-pin", new { pin = "1234" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/settings/staff-pin", new { pin = "73-club-91" })).StatusCode);

        var hash = (await HeartbeatAsync(approved.Key)).GetProperty("staffPinHash").GetString();
        Assert.True(StaffPinHash.Verify("73-club-91", hash));
        Assert.DoesNotContain("73-club-91", hash);
        Assert.Equal(JsonValueKind.Null, (await HeartbeatAsync(pending.Key)).GetProperty("staffPinHash").ValueKind);

        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(settings.GetProperty("staffPinConfigured").GetBoolean());
        Assert.True(settings.GetProperty("enrollmentTokenConfigured").GetBoolean());

        var cashier = await CreateCashierAsync(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/settings/staff-pin", new { pin = "99-hack-99" })).StatusCode);
    }

    private async Task<HttpClient> CreateCashierAsync(HttpClient owner)
    {
        await owner.PostAsJsonAsync("/api/staff", new { username = "pin-cashier", password = "cashier-pass-1", role = "Cashier" });
        return await _factory.LoginAsync("pin-cashier", "cashier-pass-1");
    }
}
