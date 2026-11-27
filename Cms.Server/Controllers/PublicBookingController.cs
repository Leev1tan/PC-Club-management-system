using System.Globalization;
using System.Text.RegularExpressions;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// The public booking page (/book, no login; spec area 7). A visitor picks a zone, a date, a start and a duration,
/// sees only the free capacity (never other bookings), and sends a request with a name and a phone number; staff
/// confirm or decline it. Off by default (setting PublicBooking; on in the demo). Abuse limits: per IP (rate
/// limiter), per phone (3 a day, MaxOpenPerPhone open), a honeypot field, a size limit on every field and on the
/// body. The audit row of a request has no name, no phone and no IP; the name and the phone are erased
/// EraseAfterDays after the slot.
/// </summary>
[ApiController]
[Route("api/public/booking")]
[AllowAnonymous]
public partial class PublicBookingController : ControllerBase
{
    public static readonly int[] Durations = { 60, 90, 120, 180, 240, 300 };
    public const int SlotMinutes = 30;
    public const int MaxDaysAhead = 14;
    public const int LeadMinutes = 30;
    public const int MaxBodyBytes = 2048;

    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly ClubClock _clock;
    private readonly DemoOptions _demo;
    private readonly BookingPhoneLimiter _phones;
    private readonly ILogger<PublicBookingController> _logger;

    public PublicBookingController(CmsDbContext db, IDeviceRepository devices, ClubClock clock, DemoOptions demo, BookingPhoneLimiter phones,
        ILogger<PublicBookingController> logger)
    {
        _db = db;
        _devices = devices;
        _clock = clock;
        _demo = demo;
        _phones = phones;
        _logger = logger;
    }

    // An audit logger without the HTTP context: the audit row of a request gets no IP address
    private ReservationService Service => new(_db, _devices, _clock, new AuditLogger(_db, new HttpContextAccessor()));

    private DateOnly Today => DateOnly.FromDateTime(_clock.ToLocal(_clock.UtcNow).DateTime);

    private async Task<List<(ZoneEntity Zone, int Pcs)>> ZonesAsync()
    {
        var zones = await _db.Zones.AsNoTracking().OrderBy(z => z.SortOrder).ThenBy(z => z.Name).ToListAsync();
        var counts = (await _db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatuses.Approved && d.ZoneId != null).Select(d => d.ZoneId!.Value).ToListAsync())
            .GroupBy(z => z).ToDictionary(g => g.Key, g => g.Count());
        return zones.Where(z => counts.ContainsKey(z.Id)).Select(z => (z, counts[z.Id])).ToList();
    }

    [HttpGet]
    [EnableRateLimiting(AuthConstants.BookingReadPolicy)]
    public async Task<ActionResult<BookingInfoView>> Info()
    {
        var settings = await ReservationSettings.LoadAsync(_db);
        if (!settings.PublicBooking) return Ok(new BookingInfoView(false, _demo.Enabled));
        var now = _clock.UtcNow;
        var currency = await _db.CurrencyAsync();
        var zones = new List<BookingZoneView>();
        foreach (var (zone, pcs) in await ZonesAsync())
        {
            var tariff = await SessionService.ZoneTariffAsync(_db, zone.Id);
            decimal? price = null;
            if (tariff != null)
                price = TariffCalculator.PriceAt(TariffPricing.From(tariff, await _db.TariffRules.AsNoTracking().Where(r => r.TariffId == tariff.Id).ToListAsync()),
                    _clock.ToLocal(now)).PricePerHour;
            zones.Add(new BookingZoneView(zone.Id, zone.Name, zone.Color, pcs, price));
        }
        return Ok(new BookingInfoView(true, _demo.Enabled)
        {
            TimeZone = ClubTimeZone.IanaId(_clock.Zone), Today = Today.ToString("yyyy-MM-dd"), MaxDaysAhead = MaxDaysAhead, Durations = Durations,
            SlotMinutes = SlotMinutes, LeadMinutes = LeadMinutes, EraseAfterDays = settings.EraseAfterDays, CurrencySymbol = currency.Symbol, Zones = zones
        });
    }

    /// <summary>Free capacity of every zone for every start of the day (30-minute steps) with this duration; nothing else.</summary>
    [HttpGet("availability")]
    [EnableRateLimiting(AuthConstants.BookingReadPolicy)]
    public async Task<ActionResult<AvailabilityView>> Availability([FromQuery] string? date, [FromQuery] int duration)
    {
        var settings = await ReservationSettings.LoadAsync(_db);
        if (!settings.PublicBooking) return NotFound();
        if (!TryDay(date, out var day) || !Durations.Contains(duration)) return ReservationErrors.BadTime.ToActionResult();
        var starts = Starts(day);
        var zones = await ZonesAsync();
        var free = await Service.FreeCapacityAsync(zones.Select(z => z.Zone.Id).ToList(), starts.Select(s => (s.Utc, s.Utc.AddMinutes(duration))).ToList());
        return Ok(new AvailabilityView(day.ToString("yyyy-MM-dd"), duration, zones.Select(z => new ZoneAvailabilityView(z.Zone.Id,
            starts.Select((s, i) => new SlotView(s.Local, free[z.Zone.Id][i])).ToList())).ToList()));
    }

    private bool TryDay(string? date, out DateOnly day) =>
        DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) &&
        day >= Today && day.DayNumber - Today.DayNumber < MaxDaysAhead;

    /// <summary>Start times of the club day in 30-minute steps, not before now + 30 minutes.</summary>
    private List<(string Local, DateTimeOffset Utc)> Starts(DateOnly day)
    {
        var earliest = _clock.UtcNow.AddMinutes(LeadMinutes);
        var list = new List<(string, DateTimeOffset)>();
        for (var m = 0; m < 24 * 60; m += SlotMinutes)
        {
            var local = day.ToDateTime(TimeOnly.MinValue).AddMinutes(m);
            var utc = ClubTimeZone.LocalToUtc(local, _clock.Zone);
            if (utc >= earliest && list.All(x => x.Item2 != utc)) list.Add((local.ToString("HH:mm", CultureInfo.InvariantCulture), utc));
        }
        return list;
    }

    [GeneratedRegex(@"^[\p{L}\p{M}][\p{L}\p{M} .'’\-]{1,59}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\+?[0-9 ()\-]{9,24}$")]
    private static partial Regex PhonePattern();

    /// <summary>A request (202): staff confirm or decline it. A filled honeypot field also gets 202, but nothing is stored.</summary>
    [HttpPost]
    [EnableRateLimiting(AuthConstants.BookingWritePolicy)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Send([FromBody] BookingRequest req)
    {
        var settings = await ReservationSettings.LoadAsync(_db);
        if (!settings.PublicBooking) return NotFound();
        if (!string.IsNullOrEmpty(req.Website))
        {
            _logger.LogInformation("Booking page: a request with the honeypot field filled was dropped");
            return Accepted(new { status = ReservationStatuses.Requested });
        }
        var name = string.Join(' ', (req.Name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!NamePattern().IsMatch(name)) return ReservationErrors.BadName.ToActionResult();
        var rawPhone = (req.Phone ?? string.Empty).Trim();
        var digits = new string(rawPhone.Where(char.IsAsciiDigit).ToArray());
        if (rawPhone.Length > 24 || !PhonePattern().IsMatch(rawPhone) || digits.Length is < 9 or > 15) return ReservationErrors.BadPhone.ToActionResult();
        var phone = (rawPhone.StartsWith('+') ? "+" : "") + digits;
        if (!TryDay(req.Date, out var day) || !Durations.Contains(req.DurationMinutes)) return ReservationErrors.BadTime.ToActionResult();
        var start = Starts(day).FirstOrDefault(s => s.Local == req.Time?.Trim());
        if (start.Local == null) return ReservationErrors.BadTime.ToActionResult();
        if (!(await ZonesAsync()).Any(z => z.Zone.Id == req.ZoneId)) return ReservationErrors.ZoneEmpty.ToActionResult();
        if (!_phones.TryRecord(ReservationService.PhoneKey(phone), _clock.UtcNow)) return ReservationErrors.PhoneLimit.ToActionResult();

        var result = await Service.RequestAsync(req.ZoneId, start.Utc, req.DurationMinutes, name, phone, settings.MaxOpenPerPhone);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Accepted(new { status = ReservationStatuses.Requested });
    }
}

/// <summary>Website: the honeypot field (hidden on the page; people leave it empty).</summary>
public record BookingRequest(Guid ZoneId, string? Date, string? Time, int DurationMinutes, string? Name, string? Phone, string? Website = null);
public record BookingZoneView(Guid Id, string Name, string Color, int Pcs, decimal? PricePerHourNow);
public record BookingInfoView(bool Enabled, bool Demo)
{
    public string? TimeZone { get; init; }
    public string? Today { get; init; }
    public int MaxDaysAhead { get; init; }
    public IReadOnlyList<int> Durations { get; init; } = Array.Empty<int>();
    public int SlotMinutes { get; init; }
    public int LeadMinutes { get; init; }
    public int EraseAfterDays { get; init; }
    public string? CurrencySymbol { get; init; }
    public IReadOnlyList<BookingZoneView> Zones { get; init; } = Array.Empty<BookingZoneView>();
}
public record SlotView(string Time, int Free);
public record ZoneAvailabilityView(Guid Id, IReadOnlyList<SlotView> Slots);
public record AvailabilityView(string Date, int Duration, IReadOnlyList<ZoneAvailabilityView> Zones);
