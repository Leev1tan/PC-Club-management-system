using System.Globalization;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Reservations;

public static class ReservationErrors
{
    private const int NotFound = StatusCodes.Status404NotFound;
    private const int Conflict = StatusCodes.Status409Conflict;

    public static readonly BillingError NotFoundError = new("reservation_not_found", "Reservation not found", NotFound);
    public static readonly BillingError BadTime = new("bad_reservation_time", "Choose a start in club time (date YYYY-MM-DD, time HH:mm), from now up to 60 days ahead.");
    public static readonly BillingError BadDuration = new("bad_reservation_duration", "The duration must be 30 minutes to 12 hours, in steps of 15 minutes.");
    public static readonly BillingError NeedsTarget = new("reservation_needs_pc_or_zone", "Choose a PC or a zone.");
    public static readonly BillingError NeedsGuest = new("reservation_needs_guest", "Choose a player or enter a name (at most 60 characters).");
    public static readonly BillingError NoteTooLong = new("reservation_note_too_long", "The note may have at most 200 characters.");
    public static readonly BillingError PcTaken = new("pc_already_reserved", "{0} is already reserved {1}-{2}.", Conflict);
    public static readonly BillingError ZoneFull = new("zone_full", "Zone {0} has no free PC for this time.", Conflict);
    public static readonly BillingError ZoneEmpty = new("zone_no_pcs", "The zone has no approved PC.", Conflict);
    public static readonly BillingError PcReserved = new("pc_reserved", "This PC is reserved for {0} at {1}. Check in the reservation, or a manager overrides the hold.", Conflict);
    public static readonly BillingError OverrideNeedsManager = new("override_needs_manager", "Only the owner or a manager can override a reservation hold.", StatusCodes.Status403Forbidden);
    public static readonly BillingError NotOpen = new("reservation_not_open", "The reservation is {0}; this is not possible now.", Conflict);
    public static readonly BillingError NotYet = new("reservation_not_yet", "Check-in starts at {0} ({1} min before the start).", Conflict);
    public static readonly BillingError TooLate = new("reservation_too_late", "The grace period ended at {0}: the reservation is a no-show.", Conflict);
    public static readonly BillingError PcFixed = new("reservation_pc_fixed", "This reservation is for {0}. Move it to another PC first.", Conflict);
    public static readonly BillingError OtherZone = new("reservation_other_zone", "The PC is not in the zone of the reservation.");
    public static readonly BillingError NoFreePc = new("reservation_no_free_pc", "No PC of the zone is free now.", Conflict);
    public static readonly BillingError DepositNeedsPlayer = new("deposit_needs_player", "A deposit is taken only from the balance of a registered player.");
    public static readonly BillingError BadDeposit = new("bad_deposit", "The deposit must be 0 to 10000.");
    public static readonly BillingError InsufficientDeposit = new("insufficient_balance_deposit", "Not enough real balance for the deposit: need {0}, have {1}.", Conflict);
    public static readonly BillingError BadSettings = new("bad_reservation_settings", "{0}");
    public static readonly BillingError BookingDisabled = new("booking_disabled", "Online booking is off.", StatusCodes.Status404NotFound);
    public static readonly BillingError SlotFull = new("slot_full", "This time is fully booked. Choose another time.", Conflict);
    public static readonly BillingError OpenLimit = new("booking_open_limit", "This phone number already has {0} open requests or reservations.", Conflict);
    public static readonly BillingError PhoneLimit = new("booking_phone_limit", "Too many requests for this phone number. Try again tomorrow or call the club.", StatusCodes.Status429TooManyRequests);
    public static readonly BillingError BadName = new("booking_bad_name", "Enter your name: 2 to 60 letters.");
    public static readonly BillingError BadPhone = new("booking_bad_phone", "Enter a phone number with 9 to 15 digits.");
}

public sealed record CreateReservationRequest(Guid? DeviceId, Guid? ZoneId, string? Date, string? Time, int DurationMinutes,
    Guid? UserId = null, string? GuestName = null, decimal Deposit = 0m, string? Note = null);
public sealed record UpdateReservationRequest(Guid? DeviceId, Guid? ZoneId, string? Date, string? Time, int DurationMinutes, string? Note = null);
/// <summary>
/// Mode: minutes (default: the reservation duration, or Minutes), package (PackageId), open (open-ended) or balance
/// (a player: time from the balance). PaymentMethod and UseBonus as for a session start. DeviceId: a PC of the zone
/// for an any-PC reservation (default: the held PC or a free one).
/// </summary>
public sealed record CheckInRequest(Guid? DeviceId = null, string? Mode = null, int? Minutes = null, Guid? PackageId = null,
    string? PaymentMethod = null, bool UseBonus = true);
public sealed record ReasonRequest(string? Reason = null);
public sealed record ConfirmReservationRequest(Guid? DeviceId = null);
public sealed record ReservationJobResult(int NoShows, int Expired, int Assigned, int Erased);

/// <summary>The hold of a PC: the confirmed reservation it is kept for now. Name = nickname or first name only.</summary>
public sealed record ReservationHold(long Id, Guid? UserId, string Name, DateTimeOffset StartUtc, string StartLocal);

public static class ReservationHolds
{
    /// <summary>
    /// The reservation that holds the PC now: confirmed, for this PC (or any-PC and assigned to it), from HoldMinutes
    /// before the start until the grace period ends; the earliest when there are two.
    /// </summary>
    public static async Task<ReservationHold?> ActiveAsync(CmsDbContext db, Guid deviceId, ClubClock clock, ReservationSettings? settings = null,
        long? except = null, CancellationToken ct = default)
    {
        settings ??= await ReservationSettings.LoadAsync(db, ct);
        var now = clock.UtcNow;
        var today = ReservationService.DayOf(now, clock);
        var candidates = await db.Reservations.AsNoTracking()
            .Where(r => r.Status == ReservationStatuses.Confirmed && (r.DeviceId == deviceId || r.AssignedDeviceId == deviceId)
                        && r.StartDay >= today - 1 && r.StartDay <= today + 1)
            .ToListAsync(ct);
        var hold = candidates.Where(r => r.Id != except && IsHeld(r, now, settings)).OrderBy(r => r.StartUtc).FirstOrDefault();
        if (hold == null) return null;
        var username = hold.UserId.HasValue ? await db.Users.AsNoTracking().Where(u => u.Id == hold.UserId).Select(u => u.Username).FirstOrDefaultAsync(ct) : null;
        return new ReservationHold(hold.Id, hold.UserId, ReservationService.PublicName(hold, username), hold.StartUtc, clock.ToLocal(hold.StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    public static bool IsHeld(ReservationEntity r, DateTimeOffset now, ReservationSettings settings) =>
        r.Status == ReservationStatuses.Confirmed && now >= r.StartUtc.AddMinutes(-settings.HoldMinutes) && now < r.StartUtc.AddMinutes(settings.GraceMinutes);
}

/// <summary>
/// Reservations by staff (spec area 7). Every change runs in a transaction that holds the reservation lock
/// (PostgreSQL: a transaction advisory lock; SQLite: one writer at a time), so two cashiers at the same moment
/// cannot book the same PC or the last PC of a zone twice: the second one sees the first one's row.
/// Lock order: reservation lock, player row, open shift, audit chain (like the other services after the first).
/// Audit texts never contain a walk-in name or a phone number (the audit log cannot be erased).
/// </summary>
public sealed class ReservationService
{
    private const long LockKey = 0x43434D5352455356; // "CCMSRESV"
    public const int MinMinutes = 30;
    public const int MaxMinutes = 12 * 60;
    public const int StepMinutes = 15;
    public const int MaxDaysAhead = 60;
    public const decimal MaxDeposit = 10_000m;
    /// <summary>Staff may book a start a little in the past (a guest who is at the desk now).</summary>
    public static readonly TimeSpan PastTolerance = TimeSpan.FromMinutes(5);

    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;
    private readonly StaffUserEntity? _auditActor;

    public ReservationService(CmsDbContext db, IDeviceRepository devices, ClubClock? clock = null, AuditLogger? audit = null,
        StaffActor? actor = null, StaffUserEntity? auditActor = null)
    {
        _db = db;
        _devices = devices;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor ?? (auditActor == null ? null : new StaffActor(auditActor.Id, auditActor.Username));
        _auditActor = auditActor;
    }

    /// <summary>Owner or Manager at a check-in: may give free time like at a start.</summary>
    public bool CanGiveFreeTime { get; init; }

    public static int DayOf(DateTimeOffset utc, ClubClock clock) => DateOnly.FromDateTime(clock.ToLocal(utc).DateTime).DayNumber;

    /// <summary>What the lock screen and the public page may show: the nickname of a player or the first name of a guest.</summary>
    public static string PublicName(ReservationEntity r, string? username)
    {
        if (!string.IsNullOrWhiteSpace(username)) return username;
        var first = (r.GuestName ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first)) return "—";
        return first.Length > 20 ? first[..20] : first;
    }

    private Task AuditAsync(string action, long id, string summary, object details) =>
        _audit?.WriteAsync(action, "reservation", id, summary, details, _auditActor) ?? Task.CompletedTask;

    private async Task LockAsync()
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({LockKey})");
    }

    private string Local(DateTimeOffset utc, string format = "HH:mm") => _clock.ToLocal(utc).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Club-local date and time to UTC; null for bad input.</summary>
    public (DateTimeOffset Utc, int Day)? ParseStart(string? date, string? time)
    {
        if (!DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            !TimeOnly.TryParseExact(time?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        var utc = ClubTimeZone.LocalToUtc(d.ToDateTime(t), _clock.Zone);
        return (utc, DayOf(utc, _clock));
    }

    public static bool ValidDuration(int minutes) => minutes is >= MinMinutes and <= MaxMinutes && minutes % StepMinutes == 0;

    private sealed record Target(DeviceEntity? Pc, ZoneEntity? Zone);

    private async Task<BillingResult<Target>> TargetAsync(Guid? deviceId, Guid? zoneId)
    {
        if (deviceId.HasValue)
        {
            var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId.Value);
            if (device == null) return BillingErrors.DeviceNotFound;
            if (device.Status != DeviceStatuses.Approved) return BillingErrors.DeviceNotApproved;
            var zone = device.ZoneId.HasValue ? await _db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == device.ZoneId) : null;
            return BillingResult<Target>.Success(new Target(device, zone));
        }
        if (zoneId.HasValue)
        {
            var zone = await _db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId.Value);
            if (zone == null) return new BillingError("zone_not_found", "Zone not found", StatusCodes.Status404NotFound);
            if (!await _db.Devices.AnyAsync(d => d.ZoneId == zone.Id && d.Status == DeviceStatuses.Approved)) return ReservationErrors.ZoneEmpty;
            return BillingResult<Target>.Success(new Target(null, zone));
        }
        return ReservationErrors.NeedsTarget;
    }

    private string Where(Target t) => t.Pc != null ? t.Pc.Hostname : $"any PC of {t.Zone?.Name ?? "?"}";

    /// <summary>Reservations that occupy PCs in [from, to): confirmed and checked in (a checked-in one where its session runs, until the session ended).</summary>
    private async Task<List<PlanItem>> OccupyingAsync(DateTimeOffset from, DateTimeOffset to, long? except)
    {
        var lo = DayOf(from.AddMinutes(-MaxMinutes), _clock);
        var hi = DayOf(to, _clock);
        var rows = await _db.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatuses.Confirmed || r.Status == ReservationStatuses.CheckedIn) && r.StartDay >= lo && r.StartDay <= hi)
            .ToListAsync();
        var sessionIds = rows.Where(r => r.SessionId.HasValue).Select(r => r.SessionId!.Value).ToList();
        var sessions = sessionIds.Count == 0 ? new Dictionary<Guid, SessionEntity>()
            : await _db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var items = new List<PlanItem>();
        foreach (var r in rows.Where(r => r.Id != except))
        {
            var pc = r.DeviceId ?? r.AssignedDeviceId;
            var end = r.EndUtc;
            if (r.Status == ReservationStatuses.CheckedIn && r.SessionId is { } sid && sessions.TryGetValue(sid, out var s))
            {
                pc = s.DeviceId; // a transfer moves the reservation with the session
                if (s.Status == "ended" && s.EndUtc is { } ended && ended < end) end = ended;
            }
            if (r.StartUtc < to && from < end) items.Add(new PlanItem(r.Id, pc, r.StartUtc, end));
        }
        return items;
    }

    /// <summary>Null when the reservation [start, end) fits: no overlap on its PC, and the zone keeps a PC for every any-PC reservation.</summary>
    private async Task<BillingError?> CheckFitAsync(long self, Guid? pcId, Guid? zoneId, DateTimeOffset start, DateTimeOffset end)
    {
        var occupying = await OccupyingAsync(start, end, self);
        if (pcId.HasValue)
        {
            var clash = occupying.Where(i => i.Pc == pcId).OrderBy(i => i.Start).FirstOrDefault();
            if (clash != null)
            {
                var host = await _db.Devices.AsNoTracking().Where(d => d.Id == pcId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
                return ReservationErrors.PcTaken.With(host, Local(clash.Start), Local(clash.End));
            }
        }
        if (zoneId.HasValue)
        {
            var pcs = await ZonePcsAsync(zoneId.Value);
            // Items on the PCs of this zone, and the any-PC items of this zone (not those of other zones)
            var floating = occupying.Where(i => !i.Pc.HasValue).Select(i => i.Id).ToList();
            var floatingIds = (await _db.Reservations.AsNoTracking().Where(r => floating.Contains(r.Id) && r.ZoneId == zoneId).Select(r => r.Id).ToListAsync()).ToHashSet();
            var zoneItems = occupying.Where(i => i.Pc.HasValue ? pcs.Contains(i.Pc.Value) : floatingIds.Contains(i.Id)).ToList();
            zoneItems.Add(new PlanItem(self == 0 ? long.MaxValue : self, pcId, start, end));
            if (ReservationPlan.Assign(pcs, zoneItems) == null)
            {
                var name = await _db.Zones.AsNoTracking().Where(z => z.Id == zoneId).Select(z => z.Name).FirstOrDefaultAsync() ?? "?";
                return ReservationErrors.ZoneFull.With(name);
            }
        }
        return null;
    }

    private async Task<List<Guid>> ZonePcsAsync(Guid zoneId) =>
        (await _db.Devices.AsNoTracking().Where(d => d.ZoneId == zoneId && d.Status == DeviceStatuses.Approved).ToListAsync())
        .OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).Select(d => d.Id).ToList();

    private static string? CleanName(string? name)
    {
        var n = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return n.Length == 0 ? null : n;
    }

    private string Who(UserEntity? user, string source) => user != null ? "@" + user.Username : source == "public" ? "a public request" : "a walk-in guest";

    // ===== Create =====

    public async Task<BillingResult<ReservationEntity>> CreateAsync(CreateReservationRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        if (!ValidDuration(req.DurationMinutes)) return ReservationErrors.BadDuration;
        var start = ParseStart(req.Date, req.Time);
        var now = _clock.UtcNow;
        if (start == null || start.Value.Utc < now - PastTolerance || start.Value.Utc > now.AddDays(MaxDaysAhead)) return ReservationErrors.BadTime;
        var target = await TargetAsync(req.DeviceId, req.ZoneId);
        if (!target.Ok) return target.Error!;
        UserEntity? user = null;
        if (req.UserId.HasValue)
        {
            user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId.Value);
            if (user == null) return BillingErrors.UserNotFound;
        }
        var guest = user == null ? CleanName(req.GuestName) : null;
        if (user == null && (guest == null || guest.Length > ReservationLimits.NameLength)) return ReservationErrors.NeedsGuest;
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is { Length: > ReservationLimits.NoteLength }) return ReservationErrors.NoteTooLong;
        if (req.Deposit < 0 || req.Deposit > MaxDeposit) return ReservationErrors.BadDeposit;
        if (req.Deposit > 0 && user == null) return ReservationErrors.DepositNeedsPlayer;
        var deposit = TariffCalculator.Round(req.Deposit);
        var end = start.Value.Utc.AddMinutes(req.DurationMinutes);

        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        var fit = await CheckFitAsync(0, target.Value!.Pc?.Id, target.Value.Pc?.ZoneId ?? target.Value.Zone?.Id, start.Value.Utc, end);
        if (fit != null) return fit;
        var r = new ReservationEntity
        {
            Status = ReservationStatuses.Confirmed, Source = "staff", DeviceId = target.Value.Pc?.Id, ZoneId = target.Value.Pc?.ZoneId ?? target.Value.Zone?.Id,
            StartUtc = start.Value.Utc, EndUtc = end, DurationMinutes = req.DurationMinutes, StartDay = start.Value.Day,
            UserId = user?.Id, GuestName = guest, Deposit = deposit, DepositStatus = deposit > 0 ? DepositStatuses.Held : DepositStatuses.None,
            Note = note, CreatedUtc = now, UpdatedUtc = now, CreatedById = _actor?.Id, CreatedByUsername = _actor?.Username
        };
        _db.Reservations.Add(r);
        await _db.SaveChangesAsync();
        if (deposit > 0)
        {
            // From the real balance only (bonus is not money that can be held or returned)
            if (await Wallet.TryChargeAsync(_db, user!.Id, deposit, SpendPurpose.Bar) == null)
            {
                var have = await Wallet.GetAsync(_db, user.Id);
                return ReservationErrors.InsufficientDeposit.With(deposit, have?.Balance ?? 0m);
            }
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = user.Id, Type = LedgerTypes.DepositHold, Method = PaymentMethods.Balance, Amount = deposit, BalanceDelta = -deposit,
                ReservationId = r.Id, DeviceId = r.DeviceId,
                Description = $"Депозит броні №{r.Id} ({Where(target.Value)}, {Local(r.StartUtc, "dd.MM HH:mm")}) — {deposit} {cur}"
            }, _actor, now);
        }
        await AuditAsync(AuditActions.ReservationCreated, r.Id,
            $"Reservation #{r.Id}: {Where(target.Value)}, {Local(r.StartUtc, "dd.MM HH:mm")}-{Local(r.EndUtc)} for {Who(user, r.Source)}" +
            (deposit > 0 ? $", deposit {deposit} {cur} held" : ""),
            new { id = r.Id, device = target.Value.Pc?.Hostname, zone = target.Value.Zone?.Name, startUtc = r.StartUtc, minutes = r.DurationMinutes, player = user?.Username, deposit });
        await tx.CommitAsync();
        return BillingResult<ReservationEntity>.Success(r);
    }

    // ===== Move =====

    public async Task<BillingResult<ReservationEntity>> UpdateAsync(long id, UpdateReservationRequest req)
    {
        if (!ValidDuration(req.DurationMinutes)) return ReservationErrors.BadDuration;
        var start = ParseStart(req.Date, req.Time);
        var now = _clock.UtcNow;
        if (start == null || start.Value.Utc < now - PastTolerance || start.Value.Utc > now.AddDays(MaxDaysAhead)) return ReservationErrors.BadTime;
        var targetResult = await TargetAsync(req.DeviceId, req.ZoneId);
        if (!targetResult.Ok) return targetResult.Error!;
        var target = targetResult.Value!;
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is { Length: > ReservationLimits.NoteLength }) return ReservationErrors.NoteTooLong;
        var end = start.Value.Utc.AddMinutes(req.DurationMinutes);

        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return ReservationErrors.NotFoundError;
        if (r.Status is not (ReservationStatuses.Confirmed or ReservationStatuses.Requested)) return ReservationErrors.NotOpen.With(r.Status);
        var zoneId = target.Pc?.ZoneId ?? target.Zone?.Id;
        if (r.Status == ReservationStatuses.Confirmed)
        {
            var fit = await CheckFitAsync(r.Id, target.Pc?.Id, zoneId, start.Value.Utc, end);
            if (fit != null) return fit;
        }
        var before = $"{(r.DeviceId.HasValue ? await HostAsync(r.DeviceId.Value) : "zone")} {Local(r.StartUtc, "dd.MM HH:mm")}-{Local(r.EndUtc)}";
        var changedPlace = r.DeviceId != target.Pc?.Id || r.ZoneId != zoneId;
        r.DeviceId = target.Pc?.Id;
        r.ZoneId = zoneId;
        if (changedPlace || r.StartUtc != start.Value.Utc) r.AssignedDeviceId = null;
        r.StartUtc = start.Value.Utc;
        r.EndUtc = end;
        r.DurationMinutes = req.DurationMinutes;
        r.StartDay = start.Value.Day;
        r.Note = note;
        r.UpdatedUtc = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return BillingErrors.SessionChanged;
        }
        await AuditAsync(AuditActions.ReservationUpdated, r.Id,
            $"Reservation #{r.Id} moved: {before} -> {Where(target)} {Local(r.StartUtc, "dd.MM HH:mm")}-{Local(r.EndUtc)}",
            new { id = r.Id, device = target.Pc?.Hostname, zone = target.Zone?.Name, startUtc = r.StartUtc, minutes = r.DurationMinutes });
        await tx.CommitAsync();
        return BillingResult<ReservationEntity>.Success(r);
    }

    private async Task<string> HostAsync(Guid id) =>
        await _db.Devices.AsNoTracking().Where(d => d.Id == id).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";

    // ===== Public requests =====

    /// <summary>
    /// A request of the public booking page for any PC of a zone: status requested (holds no PC until staff confirm
    /// it), name and phone stored for staff only. The caller checked the limits and the free capacity; the audit row
    /// has no name, no phone and no IP.
    /// </summary>
    public async Task<BillingResult<ReservationEntity>> RequestAsync(Guid zoneId, DateTimeOffset startUtc, int minutes, string name, string phone, int maxOpenPerPhone)
    {
        var now = _clock.UtcNow;
        var zone = await _db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId);
        if (zone == null) return new BillingError("zone_not_found", "Zone not found", StatusCodes.Status404NotFound);
        var r = new ReservationEntity
        {
            Status = ReservationStatuses.Requested, Source = "public", ZoneId = zone.Id, StartUtc = startUtc, EndUtc = startUtc.AddMinutes(minutes),
            DurationMinutes = minutes, StartDay = DayOf(startUtc, _clock), GuestName = name, Phone = phone, CreatedUtc = now, UpdatedUtc = now
        };
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        // Open requests and reservations of this phone number (under the lock: two requests at once count both)
        var key = PhoneKey(phone);
        var open = (await _db.Reservations.AsNoTracking()
                .Where(x => x.Phone != null && (x.Status == ReservationStatuses.Requested || x.Status == ReservationStatuses.Confirmed))
                .Select(x => new { x.Phone, x.EndUtc }).ToListAsync())
            .Count(x => x.EndUtc > now && PhoneKey(x.Phone) == key);
        if (open >= maxOpenPerPhone) return ReservationErrors.OpenLimit.With(maxOpenPerPhone);
        var free = await FreeCapacityAsync(new[] { zone.Id }, new[] { (startUtc, r.EndUtc) });
        if (free[zone.Id][0] <= 0) return ReservationErrors.SlotFull;
        _db.Reservations.Add(r);
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.ReservationRequested, r.Id,
            $"Reservation request #{r.Id} from the booking page: any PC of {zone.Name}, {Local(r.StartUtc, "dd.MM HH:mm")}-{Local(r.EndUtc)}",
            new { id = r.Id, zone = zone.Name, startUtc = r.StartUtc, minutes });
        await tx.CommitAsync();
        return BillingResult<ReservationEntity>.Success(r);
    }

    /// <summary>The last 9 digits of a phone number: "+380 67 ...", "067 ..." and "380-67..." are one number for the limits.</summary>
    public static string PhoneKey(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 9 ? digits[^9..] : digits;
    }

    /// <summary>
    /// How many more any-PC reservations each zone takes in each slot: the plan of the confirmed and checked-in
    /// reservations, and the running sessions (until their planned end; open-ended ones count one hour; never into a
    /// reservation of the PC). Requests hold nothing, so they do not count.
    /// </summary>
    public async Task<Dictionary<Guid, int[]>> FreeCapacityAsync(IReadOnlyList<Guid> zoneIds, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> slots)
    {
        var result = new Dictionary<Guid, int[]>();
        if (slots.Count == 0) { foreach (var z in zoneIds) result[z] = Array.Empty<int>(); return result; }
        var from = slots.Min(x => x.Start);
        var to = slots.Max(x => x.End);
        var now = _clock.UtcNow;
        var occupying = await OccupyingAsync(from < now ? from : now, to, null);
        var floatingIds = occupying.Where(i => !i.Pc.HasValue).Select(i => i.Id).ToList();
        var floatingZone = await _db.Reservations.AsNoTracking().Where(r => floatingIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.ZoneId);
        var sessions = await _db.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync();
        foreach (var zoneId in zoneIds)
        {
            var pcs = await ZonePcsAsync(zoneId);
            var items = occupying.Where(i => i.Pc.HasValue ? pcs.Contains(i.Pc.Value) : floatingZone.GetValueOrDefault(i.Id) == zoneId).ToList();
            var n = 0;
            foreach (var s in sessions.Where(s => pcs.Contains(s.DeviceId)))
            {
                var end = s.PausedUtc == null && s.IsPrepaid && s.EndUtc is { } planned ? planned : now.AddHours(1);
                var next = items.Where(i => i.Pc == s.DeviceId && i.Start >= now).Select(i => i.Start).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                if (next < end) end = next;
                if (items.Any(i => i.Pc == s.DeviceId && i.Overlaps(now, end))) continue;
                if (end > now) items.Add(new PlanItem(-1_000_000 - n++, s.DeviceId, now, end));
            }
            // Per slot only the items that overlap it (the same scope as the check of a new reservation): a conflict at
            // another time of the day never closes this slot
            result[zoneId] = slots.Select(slot =>
                ReservationPlan.FreeCapacity(pcs, items.Where(i => i.Overlaps(slot.Start, slot.End)).ToList(), slot.Start, slot.End)).ToArray();
        }
        return result;
    }

    /// <summary>
    /// Erases the name, the phone and the note of reservations whose slot ended EraseAfterDays ago (the row stays for
    /// the statistics). One audit row with the ids, never with the data.
    /// </summary>
    public async Task<int> ErasePersonalDataAsync(ReservationSettings settings, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var limit = now.AddDays(-settings.EraseAfterDays);
        var maxDay = DayOf(limit, _clock) + 1;
        var candidates = (await _db.Reservations
                .Where(r => r.ErasedUtc == null && (r.GuestName != null || r.Phone != null) && r.StartDay <= maxDay).ToListAsync(ct))
            .Where(r => r.EndUtc <= limit && r.Status is not (ReservationStatuses.Confirmed or ReservationStatuses.Requested)).ToList();
        if (candidates.Count == 0) return 0;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var r in candidates)
        {
            r.GuestName = null;
            r.Phone = null;
            if (r.Source == "public") r.Note = null;
            r.ErasedUtc = now;
            r.UpdatedUtc = now;
        }
        await _db.SaveChangesAsync(ct);
        if (_audit != null)
            await _audit.WriteAsync(AuditActions.ReservationDataErased, "reservations", null,
                $"Names and phone numbers of {candidates.Count} reservation(s) erased: slots ended {settings.EraseAfterDays} days ago or earlier",
                new { ids = candidates.Select(r => r.Id).Take(500), eraseAfterDays = settings.EraseAfterDays }, _auditActor, ct);
        await tx.CommitAsync(ct);
        return candidates.Count;
    }

    // ===== Requests: confirm, decline =====

    /// <summary>A request becomes a confirmed reservation (it holds a PC then); the PC or the zone must have room.</summary>
    public async Task<BillingResult<ReservationEntity>> ConfirmAsync(long id, ConfirmReservationRequest req)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return ReservationErrors.NotFoundError;
        if (r.Status != ReservationStatuses.Requested) return ReservationErrors.NotOpen.With(r.Status);
        if (r.EndUtc <= now) return ReservationErrors.BadTime;
        Target target;
        if (req.DeviceId.HasValue)
        {
            var t = await TargetAsync(req.DeviceId, null);
            if (!t.Ok) return t.Error!;
            if (r.ZoneId.HasValue && t.Value!.Pc!.ZoneId != r.ZoneId) return ReservationErrors.OtherZone;
            target = t.Value!;
        }
        else
        {
            var t = await TargetAsync(r.DeviceId, r.ZoneId);
            if (!t.Ok) return t.Error!;
            target = t.Value!;
        }
        var fit = await CheckFitAsync(r.Id, target.Pc?.Id, target.Pc?.ZoneId ?? target.Zone?.Id, r.StartUtc, r.EndUtc);
        if (fit != null) return fit;
        r.Status = ReservationStatuses.Confirmed;
        r.DeviceId = target.Pc?.Id;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.ReservationConfirmed, r.Id,
            $"Reservation request #{r.Id} confirmed: {Where(target)}, {Local(r.StartUtc, "dd.MM HH:mm")}-{Local(r.EndUtc)}",
            new { id = r.Id, device = target.Pc?.Hostname, zone = target.Zone?.Name, startUtc = r.StartUtc, minutes = r.DurationMinutes });
        await tx.CommitAsync();
        return BillingResult<ReservationEntity>.Success(r);
    }

    public async Task<BillingResult<ReservationEntity>> DeclineAsync(long id, string? reason)
    {
        var now = _clock.UtcNow;
        var why = Clip(reason);
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return ReservationErrors.NotFoundError;
        if (r.Status != ReservationStatuses.Requested) return ReservationErrors.NotOpen.With(r.Status);
        r.Status = ReservationStatuses.Declined;
        r.CancelReason = why;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.ReservationDeclined, r.Id, $"Reservation request #{r.Id} declined" + (why != null ? $": {why}" : ""), new { id = r.Id, reason = why });
        await tx.CommitAsync();
        return BillingResult<ReservationEntity>.Success(r);
    }

    private static string? Clip(string? reason)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) return null;
        return why.Length > 200 ? why[..200] : why;
    }

    // ===== Cancel =====

    /// <summary>Staff cancel a confirmed reservation (or a request); a held deposit goes back to the player's balance.</summary>
    public async Task<BillingResult<ReservationEntity>> CancelAsync(long id, string? reason)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        var why = Clip(reason);
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return ReservationErrors.NotFoundError;
        if (r.Status is not (ReservationStatuses.Confirmed or ReservationStatuses.Requested)) return ReservationErrors.NotOpen.With(r.Status);
        var returned = await ReleaseDepositAsync(r, DepositStatuses.Returned, $"Депозит броні №{r.Id} повернуто на баланс: бронь скасовано — {r.Deposit} {cur}", now);
        r.Status = ReservationStatuses.Cancelled;
        r.CancelReason = why;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.ReservationCancelled, r.Id,
            $"Reservation #{r.Id} cancelled" + (returned > 0 ? $", deposit {returned} {cur} returned to the balance" : "") + (why != null ? $": {why}" : ""),
            new { id = r.Id, deposit = returned, reason = why });
        await tx.CommitAsync();
        return BillingResult<ReservationEntity>.Success(r);
    }

    /// <summary>A held deposit back on the real balance (deposit_release). Returns the amount (0 when none was held).</summary>
    private async Task<decimal> ReleaseDepositAsync(ReservationEntity r, string status, string description, DateTimeOffset now)
    {
        if (r.DepositStatus != DepositStatuses.Held || r.Deposit <= 0 || !r.UserId.HasValue) return 0m;
        await Wallet.CreditAsync(_db, r.UserId.Value, r.Deposit);
        await Ledger.AppendAsync(_db, new LedgerEntryEntity
        {
            UserId = r.UserId, Type = LedgerTypes.DepositRelease, Method = PaymentMethods.Balance, Amount = r.Deposit, BalanceDelta = r.Deposit,
            ReservationId = r.Id, DeviceId = r.DeviceId ?? r.AssignedDeviceId, Description = description
        }, _actor, now);
        r.DepositStatus = status;
        return r.Deposit;
    }

    // ===== Check-in =====

    /// <summary>
    /// Starts the session of the reservation by the normal billing rules, from the hold until the grace period
    /// ends. A held deposit goes back on the player's balance in the same transaction, so the session start pays
    /// from it. When the start fails, nothing changes (the deposit stays held).
    /// </summary>
    public async Task<BillingResult<(ReservationEntity Reservation, SessionEntity Session)>> CheckInAsync(long id, CheckInRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        var settings = await ReservationSettings.LoadAsync(_db);
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return ReservationErrors.NotFoundError;
        if (r.Status != ReservationStatuses.Confirmed) return ReservationErrors.NotOpen.With(r.Status);
        var holdStart = r.StartUtc.AddMinutes(-settings.HoldMinutes);
        if (now < holdStart) return ReservationErrors.NotYet.With(Local(holdStart), settings.HoldMinutes);
        if (now >= r.StartUtc.AddMinutes(settings.GraceMinutes)) return ReservationErrors.TooLate.With(Local(r.StartUtc.AddMinutes(settings.GraceMinutes)));

        Guid pc;
        if (r.DeviceId.HasValue)
        {
            if (req.DeviceId.HasValue && req.DeviceId != r.DeviceId) return ReservationErrors.PcFixed.With(await HostAsync(r.DeviceId.Value));
            pc = r.DeviceId.Value;
        }
        else
        {
            var chosen = await PickPcAsync(r, req.DeviceId ?? r.AssignedDeviceId, now, settings, strict: req.DeviceId.HasValue);
            if (!chosen.Ok) return chosen.Error!;
            pc = chosen.Value;
        }

        var applied = await ReleaseDepositAsync(r, DepositStatuses.Applied, $"Депозит броні №{r.Id} зараховано на сесію (вхід за бронню) — {r.Deposit} {cur}", now);
        var mode = (req.Mode ?? "minutes").Trim().ToLowerInvariant();
        var start = mode switch
        {
            "package" => new StartSessionRequest(pc, null, r.UserId, null, req.PackageId, req.PaymentMethod, req.UseBonus),
            "open" => new StartSessionRequest(pc, null, r.UserId, null, null, req.PaymentMethod, req.UseBonus),
            "balance" => new StartSessionRequest(pc, null, r.UserId, null, null, PaymentMethods.Balance, req.UseBonus, LimitToBalance: true),
            _ => new StartSessionRequest(pc, null, r.UserId, req.Minutes ?? r.DurationMinutes, null, req.PaymentMethod, req.UseBonus)
        };
        var sessions = new SessionService(_db, _devices, _clock, _audit, _actor) { CheckInReservationId = r.Id, CanGiveFreeTime = CanGiveFreeTime };
        var started = await sessions.StartAsync(start);
        if (!started.Ok) return started.Error!; // rolled back: the deposit stays held
        r.Status = ReservationStatuses.CheckedIn;
        r.SessionId = started.Value!.Id;
        r.AssignedDeviceId = pc;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        var host = await HostAsync(pc);
        await AuditAsync(AuditActions.ReservationCheckedIn, r.Id,
            $"Reservation #{r.Id} checked in on {host}" + (applied > 0 ? $", deposit {applied} {cur} applied to the session" : ""),
            new { id = r.Id, device = host, sessionId = r.SessionId, deposit = applied, mode, minutesLate = (int)Math.Round((now - r.StartUtc).TotalMinutes) });
        await tx.CommitAsync();
        return BillingResult<(ReservationEntity, SessionEntity)>.Success((r, started.Value));
    }

    /// <summary>A PC of the zone for an any-PC reservation: free now, not held for another reservation, and the zone plan still works.</summary>
    private async Task<BillingResult<Guid>> PickPcAsync(ReservationEntity r, Guid? wanted, DateTimeOffset now, ReservationSettings settings, bool strict)
    {
        if (!r.ZoneId.HasValue) return ReservationErrors.NeedsTarget;
        var pcs = await ZonePcsAsync(r.ZoneId.Value);
        if (wanted.HasValue && !pcs.Contains(wanted.Value))
        {
            if (strict) return ReservationErrors.OtherZone;
            wanted = null;
        }
        var busy = (await _db.Sessions.AsNoTracking().Where(s => s.Status == "active").Select(s => s.DeviceId).ToListAsync()).ToHashSet();
        var order = (wanted.HasValue ? new[] { wanted.Value } : Array.Empty<Guid>()).Concat(pcs.Where(p => p != wanted)).ToList();
        foreach (var pc in order)
        {
            if (busy.Contains(pc) || await ReservationHolds.ActiveAsync(_db, pc, _clock, settings, except: r.Id) != null ||
                await CheckFitAsync(r.Id, pc, r.ZoneId, now < r.StartUtc ? now : r.StartUtc, r.EndUtc) != null)
            {
                if (strict && pc == wanted) return BillingErrors.DeviceBusy;
                continue;
            }
            return BillingResult<Guid>.Success(pc);
        }
        return ReservationErrors.NoFreePc;
    }

    // ===== Background jobs =====

    /// <summary>
    /// Every 15 s: confirmed reservations without check-in become no-shows when the grace period ends (the deposit
    /// is kept or returned by the club setting), requests that nobody confirmed expire at their start, an any-PC
    /// reservation gets a PC when its hold begins, and personal data of old slots is erased.
    /// </summary>
    public async Task<ReservationJobResult> RunJobsAsync(CancellationToken ct = default)
    {
        var settings = await ReservationSettings.LoadAsync(_db, ct);
        var now = _clock.UtcNow;
        var today = DayOf(now, _clock);
        var open = await _db.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatuses.Confirmed || r.Status == ReservationStatuses.Requested) && r.StartDay <= today + 1)
            .ToListAsync(ct);
        int noShows = 0, expired = 0, assigned = 0;
        foreach (var r in open.OrderBy(r => r.StartUtc))
        {
            if (r.Status == ReservationStatuses.Confirmed && now >= r.StartUtc.AddMinutes(settings.GraceMinutes))
                noShows += await NoShowAsync(r.Id, settings, ct) ? 1 : 0;
            else if (r.Status == ReservationStatuses.Requested && now >= r.StartUtc)
                expired += await ExpireAsync(r.Id, ct) ? 1 : 0;
            else if (r.Status == ReservationStatuses.Confirmed && r.DeviceId == null && r.AssignedDeviceId == null && now >= r.StartUtc.AddMinutes(-settings.HoldMinutes))
                assigned += await AssignAsync(r.Id, settings, ct) ? 1 : 0;
        }
        var erased = await ErasePersonalDataAsync(settings, ct);
        return new ReservationJobResult(noShows, expired, assigned, erased);
    }

    private async Task<bool> NoShowAsync(long id, ReservationSettings settings, CancellationToken ct)
    {
        var cur = await _db.CurrencySymbolAsync(ct);
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r == null || r.Status != ReservationStatuses.Confirmed || now < r.StartUtc.AddMinutes(settings.GraceMinutes)) return false;
        decimal kept = 0, returned = 0;
        if (r.DepositStatus == DepositStatuses.Held && r.Deposit > 0 && r.UserId.HasValue)
        {
            if (settings.NoShowKeepsDeposit)
            {
                await Ledger.AppendAsync(_db, new LedgerEntryEntity
                {
                    UserId = r.UserId, Type = LedgerTypes.DepositKept, Method = PaymentMethods.Balance, Amount = r.Deposit,
                    ReservationId = r.Id, DeviceId = r.DeviceId ?? r.AssignedDeviceId,
                    Description = $"Депозит броні №{r.Id} утримано: гравець не прийшов — {r.Deposit} {cur}"
                }, _actor, now, ct);
                r.DepositStatus = DepositStatuses.Kept;
                kept = r.Deposit;
            }
            else
            {
                returned = await ReleaseDepositAsync(r, DepositStatuses.Returned, $"Депозит броні №{r.Id} повернуто на баланс: гравець не прийшов — {r.Deposit} {cur}", now);
            }
        }
        r.Status = ReservationStatuses.NoShow;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(AuditActions.ReservationNoShow, r.Id,
            $"Reservation #{r.Id} cancelled automatically: no check-in {settings.GraceMinutes} min after the start ({Local(r.StartUtc, "dd.MM HH:mm")})" +
            (kept > 0 ? $", deposit {kept} {cur} kept" : returned > 0 ? $", deposit {returned} {cur} returned to the balance" : ""),
            new { id = r.Id, graceMinutes = settings.GraceMinutes, depositKept = kept, depositReturned = returned });
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<bool> ExpireAsync(long id, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r == null || r.Status != ReservationStatuses.Requested || now < r.StartUtc) return false;
        r.Status = ReservationStatuses.Expired;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(AuditActions.ReservationExpired, r.Id, $"Reservation request #{r.Id} expired: nobody confirmed it before {Local(r.StartUtc, "dd.MM HH:mm")}", new { id = r.Id });
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<bool> AssignAsync(long id, ReservationSettings settings, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync();
        var r = await _db.Reservations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r == null || r.Status != ReservationStatuses.Confirmed || r.DeviceId != null || r.AssignedDeviceId != null) return false;
        var pc = await PickPcAsync(r, null, now, settings, strict: false);
        if (!pc.Ok) return false; // all PCs of the zone are busy now: tried again in 15 s, the check-in picks one too
        r.AssignedDeviceId = pc.Value;
        r.UpdatedUtc = now;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
