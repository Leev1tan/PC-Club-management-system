using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// Known issue of the Windows test 2026-11-27: when the agent locked itself at its start, the server did not know it, so
/// the admin UI could show "unlocked"; a staff PIN unlock was not visible either. The agent reports its real lock state
/// and the state of the lock screen in every heartbeat, and the device list shows them.
/// </summary>
public sealed class DeviceLockStateTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public DeviceLockStateTests(CmsFactory factory) : base(factory) { }

    private static async Task HeartbeatAsync(TestPc pc, object body)
    {
        var res = await pc.PostAsync("/api/devices/heartbeat", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static object Beat(bool? isLocked, string? lockScreen) =>
        new { cpuPercent = 1.0, memPercent = 2.0, activeUser = "ClubUser", ip = "10.3.3.150", uptime = "00:10:00", isLocked, lockScreen };

    private static async Task<JsonElement> DeviceAsync(HttpClient staff, TestPc pc) =>
        (await staff.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray().Single(d => d.GetProperty("id").GetString() == pc.Id.ToString());

    [Fact]
    public async Task The_device_list_shows_the_lock_state_of_the_last_heartbeat()
    {
        var staff = await StaffAsync("Cashier");
        var pc = await TestPc.AddAsync(_factory);
        var before = await DeviceAsync(staff, pc);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("isLocked").ValueKind);       // nothing reported yet
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lockScreen").ValueKind);

        await HeartbeatAsync(pc, Beat(true, "ok"));                                          // locked at the agent start
        var locked = await DeviceAsync(staff, pc);
        Assert.True(locked.GetProperty("isLocked").GetBoolean());
        Assert.Equal("ok", locked.GetProperty("lockScreen").GetString());

        await HeartbeatAsync(pc, Beat(false, "staff_unlock"));                               // staff PIN unlock on the screen
        var staffUnlock = await DeviceAsync(staff, pc);
        Assert.False(staffUnlock.GetProperty("isLocked").GetBoolean());
        Assert.Equal("staff_unlock", staffUnlock.GetProperty("lockScreen").GetString());

        await HeartbeatAsync(pc, Beat(true, "silent"));                                      // the lock screen is gone
        Assert.Equal("silent", (await DeviceAsync(staff, pc)).GetProperty("lockScreen").GetString());
    }

    [Fact]
    public async Task An_older_agent_or_an_unknown_value_keeps_the_last_known_state()
    {
        var staff = await StaffAsync("Cashier");
        var pc = await TestPc.AddAsync(_factory);
        await HeartbeatAsync(pc, Beat(true, "ok"));
        await HeartbeatAsync(pc, Beat(null, "<script>"));                                    // no lock state, a bad value
        var device = await DeviceAsync(staff, pc);
        Assert.True(device.GetProperty("isLocked").GetBoolean());
        Assert.Equal("ok", device.GetProperty("lockScreen").GetString());
        await pc.HeartbeatAsync();                                                           // the heartbeat of an older agent
        Assert.True((await DeviceAsync(staff, pc)).GetProperty("isLocked").GetBoolean());
    }
}
