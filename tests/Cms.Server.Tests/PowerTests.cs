using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Demo;
using Cms.Server.Devices;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Records the Wake-on-LAN packets instead of sending them.</summary>
public sealed class FakeWakeSender : IWakeOnLanSender
{
    public List<(Guid DeviceId, string Mac, string Target)> Sent { get; } = new();

    public Task SendAsync(Guid deviceId, byte[] mac, IPEndPoint target, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add((deviceId, Convert.ToHexString(mac), target.ToString()));
        return Task.CompletedTask;
    }
}

public class PowerFactory : CmsFactory
{
    public FakeWakeSender Wake { get; } = new();

    protected virtual string Broadcast => "10.3.3.255";

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Power:WakeBroadcast"] = Broadcast,
        ["Power:WakePort"] = "7",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IWakeOnLanSender>(Wake));
    }
}

public sealed class BadPowerFactory : PowerFactory
{
    protected override string Broadcast => "not-an-address";
}

/// <summary>Remote power (spec area 4): shutdown, reboot and Wake-on-LAN for Owner, Manager and Tech; audited.</summary>
public sealed class PowerTests : ApiTestBase, IClassFixture<PowerFactory>
{
    private readonly PowerFactory _power;

    public PowerTests(PowerFactory factory) : base(factory) => _power = factory;

    private async Task<JsonElement> DeviceAsync(HttpClient staff, TestPc pc) =>
        (await staff.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray().Single(d => d.GetProperty("id").GetString() == pc.Id.ToString());

    [Fact]
    public async Task The_agent_reports_its_mac_and_invalid_addresses_are_ignored()
    {
        var owner = await _factory.LoginOwnerAsync();
        var pc = await TestPc.AddAsync(_factory);
        Assert.Equal(JsonValueKind.Null, (await DeviceAsync(owner, pc)).GetProperty("macAddress").ValueKind);
        await pc.HeartbeatAsync("aa-bb-cc-dd-ee-0f");
        Assert.Equal("AA:BB:CC:DD:EE:0F", (await DeviceAsync(owner, pc)).GetProperty("macAddress").GetString());
        foreach (var bad in new[] { "01:00:5E:00:00:01", "00:00:00:00:00:00", "FF:FF:FF:FF:FF:FF", "zz:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE", "<script>" })
            await pc.HeartbeatAsync(bad);
        Assert.Equal("AA:BB:CC:DD:EE:0F", (await DeviceAsync(owner, pc)).GetProperty("macAddress").GetString());
    }

    [Fact]
    public async Task Owner_manager_and_tech_shut_down_and_reboot_the_cashier_cannot()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);
        var pc = await TestPc.AddAsync(_factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "shutdown" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "wake" })).StatusCode);
        var restart = await cashier.PostAsJsonAsync($"/api/devices/{pc.Id}/commands", new { type = "restart" });
        Assert.Equal(HttpStatusCode.Forbidden, restart.StatusCode);
        Assert.Equal("power_forbidden", await CodeAsync(restart));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/devices/{pc.Id}/commands", new { type = "shutdown" })).StatusCode); // only through /power
        Assert.Equal(HttpStatusCode.OK, (await cashier.PostAsJsonAsync($"/api/devices/{pc.Id}/commands", new { type = "lock" })).StatusCode);

        var shutdown = await JsonAsync(await tech.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "shutdown" }));
        Assert.False(shutdown.GetProperty("activeSession").GetBoolean());
        await JsonAsync(await manager.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "reboot" }));
        await JsonAsync(await owner.PostAsJsonAsync($"/api/devices/{pc.Id}/commands", new { type = "restart" }));
        Assert.Equal(new[] { "lock", "shutdown", "restart", "restart" }, (await pc.PollAsync()).Select(c => c.GetProperty("type").GetString()));

        // During a session the audit says that the session goes on
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "Power " + Guid.NewGuid().ToString("N")[..4], pricePerHour = 60, isDefault = true }));
        await JsonAsync(await owner.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 30, paymentMethod = "cash" }));
        Assert.True((await JsonAsync(await tech.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "reboot" }))).GetProperty("activeSession").GetBoolean());
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=device.power")).EnumerateArray()
            .Where(a => a.GetProperty("targetId").GetString() == pc.Id.ToString()).Select(a => a.GetProperty("summary").GetString()).ToList();
        Assert.Contains($"Shutdown sent to {pc.Hostname}", audit);
        Assert.Contains($"Reboot sent to {pc.Hostname}", audit);
        Assert.Contains($"Reboot sent to {pc.Hostname} during the session of a guest (the session goes on)", audit);
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=device.power")).EnumerateArray(),
            a => a.GetProperty("actorRole").GetString() == "Tech");
    }

    [Fact]
    public async Task Wake_on_lan_goes_to_the_reported_mac_and_the_configured_address()
    {
        var owner = await _factory.LoginOwnerAsync();
        var tech = await StaffAsync(StaffRoles.Tech);
        var pc = await TestPc.AddAsync(_factory);
        var noMac = await tech.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "wake" });
        Assert.Equal(HttpStatusCode.Conflict, noMac.StatusCode);
        Assert.Equal("no_mac", await CodeAsync(noMac));

        await pc.HeartbeatAsync("02:11:22:33:44:55");
        var view = await JsonAsync(await tech.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "wake" }));
        Assert.Equal(("02:11:22:33:44:55", "10.3.3.255:7"), (view.GetProperty("mac").GetString(), view.GetProperty("target").GetString()));
        lock (_power.Wake.Sent) Assert.Contains((pc.Id, "021122334455", "10.3.3.255:7"), _power.Wake.Sent);
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=device.power")).EnumerateArray(),
            a => a.GetProperty("summary").GetString() == $"Wake-on-LAN sent to {pc.Hostname} (02:11:22:33:44:55) via 10.3.3.255:7");

        Assert.Equal("bad_power_action", await CodeAsync(await tech.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "format" })));
        Assert.Equal(HttpStatusCode.NotFound, (await tech.PostAsJsonAsync($"/api/devices/{Guid.NewGuid()}/power", new { action = "wake" })).StatusCode);
        var pending = await TestPc.AddAsync(_factory, status: DeviceStatuses.Pending);
        Assert.Equal("device_not_approved", await CodeAsync(await tech.PostAsJsonAsync($"/api/devices/{pending.Id}/power", new { action = "shutdown" })));
    }
}

public sealed class BadPowerConfigTests : ApiTestBase, IClassFixture<BadPowerFactory>
{
    public BadPowerConfigTests(BadPowerFactory factory) : base(factory) { }

    [Fact]
    public async Task A_wrong_broadcast_address_turns_wake_on_lan_off_with_a_clear_answer()
    {
        var owner = await _factory.LoginOwnerAsync();
        var pc = await TestPc.AddAsync(_factory);
        await pc.HeartbeatAsync("02:11:22:33:44:66");
        var res = await owner.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "wake" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("wake_not_configured", await CodeAsync(res));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/devices/{pc.Id}/power", new { action = "shutdown" })).StatusCode);
    }
}

public sealed class WakeOnLanPacketTests
{
    [Fact]
    public async Task The_udp_sender_sends_the_magic_packet_three_times()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var mac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
        await new UdpWakeOnLanSender().SendAsync(Guid.NewGuid(), mac, new IPEndPoint(IPAddress.Loopback, port));
        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var packet = (await listener.ReceiveAsync(cts.Token)).Buffer;
            Assert.Equal(102, packet.Length);
            Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
            for (var k = 0; k < 16; k++) Assert.Equal(mac, packet[(6 + k * 6)..(12 + k * 6)]);
        }
    }

    [Theory]
    [InlineData("255.255.255.255", null, "255.255.255.255:9")]
    [InlineData("10.3.3.255", "7", "10.3.3.255:7")]
    public void The_target_comes_from_the_settings(string broadcast, string? port, string target)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Power:WakeBroadcast"] = broadcast, ["Power:WakePort"] = port
        }).Build();
        var options = PowerOptions.FromConfiguration(config);
        Assert.Null(options.Error);
        Assert.Equal(target, options.Target.ToString());
    }

    [Theory]
    [InlineData("::1", null)]
    [InlineData("example.com", null)]
    [InlineData("10.3.3.255", "70000")]
    [InlineData("10.3.3.255", "x")]
    public void Wrong_settings_are_reported(string broadcast, string? port)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Power:WakeBroadcast"] = broadcast, ["Power:WakePort"] = port
        }).Build();
        Assert.NotNull(PowerOptions.FromConfiguration(config).Error);
    }
}

/// <summary>The demo simulates shutdown and Wake-on-LAN (no network traffic).</summary>
public sealed class DemoPowerTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly DeviceRuntimeState _runtime = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task A_shut_down_pc_is_offline_until_a_wake_and_the_offline_pc_wakes_up()
    {
        var now = DateTimeOffset.UtcNow;
        DemoSeedResult seed;
        await using (var db = _testDb.NewContext()) seed = await DemoSeeder.ResetAsync(db, now, new Random(42));
        var sim = new DemoSimulator(NullLogger<DemoSimulator>.Instance, _runtime, new Random(7));
        sim.Load(seed, now);
        async Task Tick(DateTimeOffset at)
        {
            await using var db = _testDb.NewContext();
            await sim.TickAsync(db, new EfDeviceRepository(db, _runtime), at);
        }

        Assert.All(seed.Pcs, p => Assert.NotNull(MacAddresses.Normalize(p.Mac)));
        var pc = seed.Pcs.First(p => !p.AlwaysOffline);
        await using (var db = _testDb.NewContext())
            new EfDeviceRepository(db, _runtime).EnqueueCommand(pc.Id, new Cms.Server.Controllers.EnqueueCommandRequest(CommandTypes.Shutdown, null));
        await Tick(now.AddSeconds(1));
        await Tick(now.AddSeconds(3));
        Assert.False(sim.IsOnline(pc.Id, now.AddSeconds(4)));
        Assert.False(sim.IsOnline(pc.Id, now.AddSeconds(60)));
        sim.Wake(pc.Id);
        await Tick(now.AddSeconds(61));
        Assert.False(sim.IsOnline(pc.Id, now.AddSeconds(62))); // it boots
        Assert.True(sim.IsOnline(pc.Id, now.AddSeconds(90)));

        var offline = seed.Pcs.Single(p => p.AlwaysOffline);
        Assert.False(sim.IsOnline(offline.Id, now.AddSeconds(90)));
        sim.Wake(offline.Id);
        await Tick(now.AddSeconds(91));
        Assert.True(sim.IsOnline(offline.Id, now.AddSeconds(120)));
        Assert.False(sim.IsOnline(offline.Id, now.AddMinutes(13))); // back to its offline role after 10 minutes

        // A shut-down PC that nobody wakes comes back by itself (the public demo stays whole)
        var other = seed.Pcs.Where(p => !p.AlwaysOffline).Skip(1).First();
        await using (var db = _testDb.NewContext())
            new EfDeviceRepository(db, _runtime).EnqueueCommand(other.Id, new Cms.Server.Controllers.EnqueueCommandRequest(CommandTypes.Shutdown, null));
        await Tick(now.AddSeconds(121));
        await Tick(now.AddSeconds(123));
        Assert.False(sim.IsOnline(other.Id, now.AddSeconds(200)));
        Assert.True(sim.IsOnline(other.Id, now.AddMinutes(6)));
    }
}
