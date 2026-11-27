using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Billing;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Reservations through the API: roles, the hold on the lock screen and the PC map, the player login, the override.</summary>
public sealed class ReservationApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public ReservationApiTests(CmsFactory factory) : base(factory) { }

    private const string Pin = "2468";

    /// <summary>Club date and time ("yyyy-MM-dd", "HH:mm") a few minutes from now, on a whole minute.</summary>
    private static (string Date, string Time) ClubSoon(int minutes)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(minutes + 1), ClubTimeZone.Default);
        return (local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private async Task<(HttpClient Owner, Guid Zone)> ClubAsync()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = (await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Res " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "T " + Guid.NewGuid().ToString("N")[..4], pricePerHour = 60, zoneId = zone }));
        return (owner, zone);
    }

    private async Task<(string Id, string Username)> PlayerWithPinAsync(HttpClient owner, decimal topUp)
    {
        var username = "rp" + Guid.NewGuid().ToString("N")[..10];
        var player = await JsonAsync(await owner.PostAsJsonAsync("/api/users/register", new { username, password = Pin, displayName = "Тарас Шевченко" }));
        var id = player.GetProperty("id").GetString()!;
        await JsonAsync(await owner.PostAsJsonAsync($"/api/users/{id}/topup", new { amount = topUp, method = "cash" }));
        return (id, username);
    }

    [Fact]
    public async Task The_hold_shows_on_the_lock_screen_and_the_map_and_only_the_reserved_player_logs_in()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, zone);
        var (playerId, username) = await PlayerWithPinAsync(owner, 200);
        var (_, other) = await PlayerWithPinAsync(owner, 200);
        var cashier = await StaffAsync("Cashier");
        var (date, time) = ClubSoon(8);

        var created = await JsonAsync(await cashier.PostAsJsonAsync("/api/reservations",
            new { deviceId = pc.Id, date, time, durationMinutes = 60, userId = playerId, deposit = 40 }));
        var id = created.GetProperty("id").GetInt64();
        Assert.Equal(("confirmed", "held", true), (created.GetProperty("status").GetString(), created.GetProperty("depositStatus").GetString(), created.GetProperty("held").GetBoolean()));
        Assert.Equal(time, created.GetProperty("startLocal").GetString());

        // Lock screen: the nickname and the club time; the PC map: "reserved at"
        var heartbeat = await pc.HeartbeatAsync();
        Assert.Equal(username, heartbeat.GetProperty("reservation").GetProperty("name").GetString());
        Assert.Equal(time, heartbeat.GetProperty("reservation").GetProperty("startLocal").GetString());
        var map = (await cashier.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == pc.Id);
        Assert.True(map.GetProperty("reservation").GetProperty("held").GetBoolean());

        // Another session needs a manager override (audited)
        var start = new { deviceId = pc.Id, durationMinutes = 30, paymentMethod = "cash" };
        Assert.Equal("pc_reserved", await CodeAsync(await cashier.PostAsJsonAsync("/api/sessions", start)));
        Assert.Equal("override_needs_manager", await CodeAsync(await cashier.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 30, paymentMethod = "cash", overrideReservation = true })));

        // Another player cannot log in on the held PC; the reserved player checks in with the login
        var refused = await pc.PostAsync($"/api/devices/{pc.Id}/player-login", new { username = other, secret = Pin, offer = "minutes", minutes = 60 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("pc_reserved", await CodeAsync(refused));
        var login = await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/player-login", new { username, secret = Pin, offer = "minutes", minutes = 60 }));
        Assert.False(login.GetProperty("fromBalance").GetBoolean());
        var after = await JsonAsync(await cashier.GetAsync($"/api/reservations/{id}"));
        Assert.Equal(("checked_in", "applied"), (after.GetProperty("status").GetString(), after.GetProperty("depositStatus").GetString()));
        // 200 - 40 deposit + 40 applied - 60 for one hour
        var user = await JsonAsync(await owner.GetAsync($"/api/users/{playerId}"));
        Assert.Equal(140m, Dec(user, "balance"));
        Assert.Equal(JsonValueKind.Null, (await pc.HeartbeatAsync()).GetProperty("reservation").ValueKind);
    }

    [Fact]
    public async Task A_manager_overrides_the_hold_and_the_settings_need_a_manager()
    {
        var (owner, zone) = await ClubAsync();
        var pc = await TestPc.AddAsync(_factory, zone);
        var cashier = await StaffAsync("Cashier");
        var manager = await StaffAsync("Manager");
        var (date, time) = ClubSoon(5);
        await JsonAsync(await cashier.PostAsJsonAsync("/api/reservations", new { deviceId = pc.Id, date, time, durationMinutes = 90, guestName = "Оксана Литвиненко" }));
        Assert.Equal("Оксана", (await pc.HeartbeatAsync()).GetProperty("reservation").GetProperty("name").GetString());
        await JsonAsync(await manager.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 30, paymentMethod = "cash", overrideReservation = true }));
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=reservation.hold_overridden")).EnumerateArray().ToList();
        Assert.Single(audit);
        Assert.DoesNotContain("Оксана", audit[0].GetRawText());

        // Roles: Tech has no reservations; the settings are for Owner and Manager (audited)
        var tech = await StaffAsync("Tech");
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/reservations")).StatusCode);
        var settings = new { holdMinutes = 20, graceMinutes = 10, noShowKeepsDeposit = false };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/reservations/settings", settings)).StatusCode);
        var saved = await JsonAsync(await manager.PutAsJsonAsync("/api/reservations/settings", settings));
        Assert.Equal(20, saved.GetProperty("holdMinutes").GetInt32());
        Assert.Equal("bad_reservation_settings", await CodeAsync(await manager.PutAsJsonAsync("/api/reservations/settings", new { holdMinutes = 15, graceMinutes = 2 })));
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=reservation.settings_changed")).EnumerateArray(),
            a => a.GetProperty("summary").GetString()!.Contains("hold 15 -> 20 min"));
        await JsonAsync(await manager.PutAsJsonAsync("/api/reservations/settings", new { holdMinutes = 15, graceMinutes = 15, noShowKeepsDeposit = true }));

        // The day view: lanes of the zone, the reservation with its club times; Manager reads the deposit check
        var day = await JsonAsync(await cashier.GetAsync($"/api/reservations?date={date}"));
        Assert.Contains(day.GetProperty("zones").EnumerateArray(), z => z.GetProperty("id").GetString() == zone.ToString());
        Assert.Contains(day.GetProperty("reservations").EnumerateArray(), r => r.GetProperty("startLocal").GetString() == time);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/ledger/holds")).StatusCode);
        Assert.True((await JsonAsync(await manager.GetAsync("/api/ledger/holds"))).GetProperty("deposits").GetProperty("ok").GetBoolean());
    }
}
