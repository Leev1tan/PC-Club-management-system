using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>
/// Demo reservations (spec area 7). The seed books, through the real service as the demo cashier, PCs for today
/// (soon held, later today) and for tomorrow: players with and without a deposit from the balance, walk-in names
/// (invented), and "any PC of VIP", and two requests of the public booking page (which the demo turns on). The
/// simulator checks most of them in around their start and lets some become no-shows (the background job cancels
/// them after the grace period), and books new ones now and then.
/// </summary>
public static class DemoReservations
{
    /// <summary>Invented walk-in names (no real person).</summary>
    public static readonly string[] WalkInNames =
    {
        "Тарас Коваленко", "Ірина Мельничук", "Богдан Шевчишин", "Олена Бондарук", "Максим Ткачишин", "Софія Кравчук", "Назар Гнатюк", "Дарина Литвин"
    };

    private sealed record Slot(TimeSpan FromNow, int Minutes, string? Zone = null, bool Player = true, decimal Deposit = 0m);

    public static async Task SeedAsync(CmsDbContext db, DateTimeOffset now, TimeZoneInfo zone, Random rng, CancellationToken ct = default)
    {
        var clock = new ClubClock(new FixedTimeProvider(now), zone);
        var cashier = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Username == "demo-cashier", ct);
        if (cashier == null) return;
        // The demo shows the public booking page (off by default in a club)
        await ReservationSettings.SaveAsync(db, ReservationSettings.Default with { PublicBooking = true }, "demo", now, ct);
        var local = clock.ToLocal(now);
        var next = local.AddMinutes(15 - local.Minute % 15).AddSeconds(-local.Second).AddMilliseconds(-local.Millisecond);
        var tomorrow = local.Date.AddDays(1);
        TimeSpan At(int hour, int minute) => new DateTimeOffset(tomorrow.AddHours(hour).AddMinutes(minute), zone.GetUtcOffset(tomorrow.AddHours(hour))) - local;
        var slots = new List<Slot>
        {
            new(next - local + TimeSpan.FromMinutes(10), 120, Deposit: 100m),          // held within minutes
            new(next - local + TimeSpan.FromMinutes(25), 90, Player: false),
            new(next - local + TimeSpan.FromMinutes(40), 180, "VIP"),                   // any PC of VIP
            new(next - local + TimeSpan.FromMinutes(70), 120, Deposit: 50m),
            new(next - local + TimeSpan.FromMinutes(120), 60, Player: false),
            new(next - local + TimeSpan.FromMinutes(180), 150, Deposit: 100m),
            new(next - local + TimeSpan.FromMinutes(240), 120, "VIP", Player: false),
            new(At(10, 0), 180, Player: false), new(At(12, 0), 120, Deposit: 50m), new(At(14, 30), 90),
            new(At(17, 0), 180, "VIP", Deposit: 150m), new(At(18, 0), 120, Player: false), new(At(19, 30), 240, Deposit: 100m),
            new(At(20, 0), 120, "Bootcamp / Pro"), new(At(21, 0), 180, Player: false),
        };

        var audit = new AuditLogger(db, new HttpContextAccessor());
        var service = new ReservationService(db, new EfDeviceRepository(db), clock, audit, auditActor: cashier);
        var players = (await db.Users.AsNoTracking().Where(u => u.Username != DemoOptions.PlayerLogin).ToListAsync(ct)).OrderBy(u => u.Username).ToList();
        var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
        var used = new HashSet<Guid>();
        var walkIn = 0;
        foreach (var slot in slots)
        {
            var start = now + slot.FromNow;
            var startLocal = clock.ToLocal(start);
            var pc = slot.Zone == null ? await FreePcAsync(db, clock, start, slot.Minutes, rng, ct) : null;
            var zoneId = slot.Zone != null ? zones.FirstOrDefault(z => z.Name == slot.Zone)?.Id : null;
            if (pc == null && zoneId == null) continue;
            UserEntity? player = null;
            if (slot.Player)
            {
                player = players.Where(p => !used.Contains(p.Id) && p.Balance >= slot.Deposit + 50m).OrderBy(_ => rng.Next()).FirstOrDefault();
                if (player != null) used.Add(player.Id);
            }
            var result = await service.CreateAsync(new CreateReservationRequest(pc, zoneId, startLocal.ToString("yyyy-MM-dd"), startLocal.ToString("HH:mm"), slot.Minutes,
                player?.Id, player == null ? WalkInNames[walkIn++ % WalkInNames.Length] : null, player != null ? slot.Deposit : 0m,
                player == null ? "Дзвінок на рецепцію" : null));
            db.ChangeTracker.Clear();
            if (!result.Ok && player != null) used.Remove(player.Id);
        }

        // Two requests from the booking page that wait for the staff (invented names, phone numbers of the
        // UK range reserved for drama, +44 7700 900000-900999: no real person)
        var requests = new[] { ("Остап Вишняк", "+447700900123", "Standard", At(16, 0), 120), ("Леся Гордієнко", "+447700900456", "VIP", At(19, 0), 180) };
        foreach (var (name, phone, zoneName, fromNow, minutes) in requests)
        {
            var zoneId = zones.FirstOrDefault(z => z.Name == zoneName)?.Id;
            if (zoneId == null) continue;
            await service.RequestAsync(zoneId.Value, now + fromNow, minutes, name, phone, ReservationSettings.Default.MaxOpenPerPhone);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>An approved, online-looking PC without a session that runs into the slot and without a reservation then.</summary>
    public static async Task<Guid?> FreePcAsync(CmsDbContext db, ClubClock clock, DateTimeOffset start, int minutes, Random rng, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var end = start.AddMinutes(minutes);
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync(ct);
        var busy = sessions.Where(s => s.EndUtc == null || s.EndUtc > start.AddMinutes(-30) || s.PausedUtc != null).Select(s => s.DeviceId).ToHashSet();
        var lo = ReservationService.DayOf(start.AddDays(-1), clock);
        var reserved = (await db.Reservations.AsNoTracking()
                .Where(r => (r.Status == ReservationStatuses.Confirmed || r.Status == ReservationStatuses.CheckedIn) && r.StartDay >= lo).ToListAsync(ct))
            .Where(r => r.StartUtc < end.AddMinutes(30) && start.AddMinutes(-30) < r.EndUtc)
            .Select(r => r.DeviceId ?? r.AssignedDeviceId).Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
        var pcs = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatuses.Approved && d.Hostname != "STD-12").Select(d => d.Id).ToListAsync(ct);
        var free = pcs.Where(p => (start - now > TimeSpan.FromHours(6) || !busy.Contains(p)) && !reserved.Contains(p)).ToList();
        return free.Count == 0 ? null : free[rng.Next(free.Count)];
    }
}
