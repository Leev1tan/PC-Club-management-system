using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cms.Server.Billing;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>A club with a high per-IP limit, so the other limits can be tested one by one.</summary>
public sealed class BookingFactory : CmsFactory
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Booking:RequestsPerIpPer10Minutes"] = "1000",
    };
}

/// <summary>The public booking page (spec area 7): off by default, only the free capacity, every abuse limit, no personal data in the audit log.</summary>
public sealed class PublicBookingTests : ApiTestBase, IClassFixture<BookingFactory>
{
    public PublicBookingTests(BookingFactory factory) : base(factory) { }

    private HttpClient Visitor() => _factory.NewClient(csrfHeader: false);

    /// <summary>Tomorrow in club time ("yyyy-MM-dd").</summary>
    private static string Tomorrow() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ClubTimeZone.Default).Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<(HttpClient Owner, Guid Zone, List<Guid> Pcs)> ClubAsync(int pcs, bool enable = true)
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = (await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "Book " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = "T " + Guid.NewGuid().ToString("N")[..4], pricePerHour = 60, zoneId = zone }));
        var ids = new List<Guid>();
        for (var i = 0; i < pcs; i++) ids.Add(await AddDeviceAsync(zone));
        if (enable) await JsonAsync(await owner.PutAsJsonAsync("/api/reservations/settings", new { holdMinutes = 15, graceMinutes = 15, noShowKeepsDeposit = true, publicBooking = true, eraseAfterDays = 30, maxOpenPerPhone = 2 }));
        return (owner, zone, ids);
    }

    private static object Request(Guid zone, string time, string phone, string name = "Остап Вишняк", int minutes = 120, string? website = null) =>
        new { zoneId = zone, date = Tomorrow(), time, durationMinutes = minutes, name, phone, website };

    private async Task<int> FreeAsync(Guid zone, string time, int minutes = 120)
    {
        var a = await JsonAsync(await Visitor().GetAsync($"/api/public/booking/availability?date={Tomorrow()}&duration={minutes}"));
        return a.GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("id").GetGuid() == zone)
            .GetProperty("slots").EnumerateArray().Single(s => s.GetProperty("time").GetString() == time).GetProperty("free").GetInt32();
    }

    [Fact]
    public async Task The_page_is_off_by_default_and_shows_only_the_free_capacity()
    {
        var (owner, zone, pcs) = await ClubAsync(2, enable: false);
        var off = await JsonAsync(await Visitor().GetAsync("/api/public/booking"));
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Empty(off.GetProperty("zones").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().GetAsync($"/api/public/booking/availability?date={Tomorrow()}&duration=120")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "18:00", "+447700900111"))).StatusCode);

        await JsonAsync(await owner.PutAsJsonAsync("/api/reservations/settings", new { holdMinutes = 15, graceMinutes = 15, noShowKeepsDeposit = true, publicBooking = true, eraseAfterDays = 30, maxOpenPerPhone = 2 }));
        var info = await JsonAsync(await Visitor().GetAsync("/api/public/booking"));
        var z = info.GetProperty("zones").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == zone);
        Assert.Equal((2, 60m), (z.GetProperty("pcs").GetInt32(), z.GetProperty("pricePerHourNow").GetDecimal()));
        Assert.Equal(2, await FreeAsync(zone, "18:00"));

        // A confirmed reservation takes one PC; a request takes nothing; the answer never names anybody
        var cashier = await StaffAsync("Cashier");
        await JsonAsync(await cashier.PostAsJsonAsync("/api/reservations", new { deviceId = pcs[0], date = Tomorrow(), time = "18:30", durationMinutes = 60, guestName = "Ірина Секретна" }));
        Assert.Equal(1, await FreeAsync(zone, "18:00"));
        Assert.Equal(2, await FreeAsync(zone, "15:00"));
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "18:00", "+447700900112"))).StatusCode);
        Assert.Equal(1, await FreeAsync(zone, "18:00"));
        var raw = await (await Visitor().GetAsync($"/api/public/booking/availability?date={Tomorrow()}&duration=120")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ірина", raw);
        Assert.DoesNotContain("Остап", raw);
        Assert.DoesNotContain("7700", raw);
        Assert.Equal(new[] { "date", "duration", "zones" }, JsonDocument.Parse(raw).RootElement.EnumerateObject().Select(p => p.Name));

        // Staff see the request with the name and the phone; the audit row has neither (and no IP)
        var day = await JsonAsync(await cashier.GetAsync($"/api/reservations?date={Tomorrow()}"));
        var req = day.GetProperty("requests").EnumerateArray().Single(r => r.GetProperty("phone").GetString() == "+447700900112");
        Assert.Equal(("Остап Вишняк", "public", "requested"), (req.GetProperty("guestName").GetString(), req.GetProperty("source").GetString(), req.GetProperty("status").GetString()));
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=reservation.requested")).EnumerateArray().ToList();
        var row = Assert.Single(audit, a => a.GetProperty("targetId").GetString() == req.GetProperty("id").GetInt64().ToString());
        Assert.DoesNotContain("Остап", row.GetRawText());
        Assert.DoesNotContain("7700", row.GetRawText());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("ip").ValueKind);

        // Confirmed by the staff it holds a PC
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/reservations/{req.GetProperty("id").GetInt64()}/confirm", new { }));
        Assert.Equal(0, await FreeAsync(zone, "18:00"));
        Assert.Equal("slot_full", await CodeAsync(await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "18:00", "+447700900113"))));
    }

    [Fact]
    public async Task Bad_fields_a_honeypot_and_too_many_requests_per_phone_are_refused()
    {
        var (owner, zone, _) = await ClubAsync(3);
        var cashier = await StaffAsync("Cashier");
        async Task<string?> Code(object body) => await CodeAsync(await Visitor().PostAsJsonAsync("/api/public/booking", body));

        Assert.Equal("booking_bad_name", await Code(Request(zone, "10:00", "+447700900201", name: "X")));
        Assert.Equal("booking_bad_name", await Code(Request(zone, "10:00", "+447700900201", name: "Bot 2000")));
        Assert.Equal("booking_bad_name", await Code(Request(zone, "10:00", "+447700900201", name: new string('я', 61))));
        Assert.Equal("booking_bad_phone", await Code(Request(zone, "10:00", "call me")));
        Assert.Equal("booking_bad_phone", await Code(Request(zone, "10:00", "+44 77")));
        Assert.Equal("bad_reservation_time", await Code(Request(zone, "10:10", "+447700900201")));
        Assert.Equal("bad_reservation_time", await Code(Request(zone, "10:00", "+447700900201", minutes: 75)));
        Assert.Equal("bad_reservation_time", await Code(new { zoneId = zone, date = DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd"), time = "10:00", durationMinutes = 60, name = "Олег", phone = "+447700900201" }));

        // The honeypot: the same answer, nothing stored
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "10:00", "+447700900299", website: "http://spam"))).StatusCode);
        Assert.DoesNotContain("+447700900299", (await cashier.GetAsync($"/api/reservations?date={Tomorrow()}")).Content.ReadAsStringAsync().Result);

        // A body over 2 KB is refused before it is read
        var big = new StringContent(JsonSerializer.Serialize(Request(zone, "10:00", "+447700900202", name: "Олег" + new string(' ', 3000))), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Visitor().PostAsync("/api/public/booking", big)).StatusCode);

        // One phone (in any format): 2 open requests at most, 3 requests a day
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "11:00", "+44 7700 900301"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "14:00", "07700-900301"))).StatusCode);
        Assert.Equal("booking_open_limit", await Code(Request(zone, "17:00", "(0)7700 900 301")));
        var open = (await JsonAsync(await cashier.GetAsync($"/api/reservations?date={Tomorrow()}"))).GetProperty("requests").EnumerateArray()
            .First(r => r.GetProperty("phone").GetString()!.EndsWith("7700900301"));
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/reservations/{open.GetProperty("id").GetInt64()}/decline", new { reason = "немає місць" }));
        var limited = await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "20:00", "+447700900301"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("booking_phone_limit", await CodeAsync(limited));
        // Another phone still works
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync("/api/public/booking", Request(zone, "20:00", "+447700900302"))).StatusCode);
    }
}

/// <summary>The per-IP limit of the booking page (default: 5 requests in 10 minutes).</summary>
public sealed class PublicBookingIpLimitTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public PublicBookingIpLimitTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task The_sixth_request_from_one_ip_in_ten_minutes_is_refused()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = (await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = "IP " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await AddDeviceAsync(zone);
        await JsonAsync(await owner.PutAsJsonAsync("/api/reservations/settings", new { holdMinutes = 15, graceMinutes = 15, noShowKeepsDeposit = true, publicBooking = true, eraseAfterDays = 30, maxOpenPerPhone = 2 }));
        var visitor = _factory.NewClient(csrfHeader: false);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add((await visitor.PostAsJsonAsync("/api/public/booking", new { zoneId = zone, date = "2000-01-01", time = "10:00", durationMinutes = 60, name = "Олег", phone = $"+4477009004{i:00}" })).StatusCode);
        Assert.Equal(Enumerable.Repeat(HttpStatusCode.BadRequest, 5).Append(HttpStatusCode.TooManyRequests), codes);
    }
}
