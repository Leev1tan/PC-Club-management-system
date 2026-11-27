using System.Globalization;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Reservations;

/// <summary>The next reservation of a PC on the PC map: start and end in club time, held now or not, the short name.</summary>
public sealed record PcReservationView(long Id, string Name, DateTimeOffset StartUtc, string StartLocal, string EndLocal, bool Held);

public static class ReservationMap
{
    public static readonly TimeSpan LookAhead = TimeSpan.FromHours(12);

    public static async Task<Dictionary<Guid, PcReservationView>> NextByPcAsync(CmsDbContext db, ClubClock clock, CancellationToken ct = default)
    {
        var settings = await ReservationSettings.LoadAsync(db, ct);
        var now = clock.UtcNow;
        var today = ReservationService.DayOf(now, clock);
        var rows = (await db.Reservations.AsNoTracking()
                .Where(r => r.Status == ReservationStatuses.Confirmed && r.StartDay >= today - 1 && r.StartDay <= today + 1 && (r.DeviceId != null || r.AssignedDeviceId != null))
                .ToListAsync(ct))
            .Where(r => r.StartUtc.AddMinutes(settings.GraceMinutes) > now && r.StartUtc < now + LookAhead).ToList();
        var userIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        return rows.GroupBy(r => (r.DeviceId ?? r.AssignedDeviceId)!.Value)
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.OrderBy(x => x.StartUtc).First();
                return new PcReservationView(r.Id, ReservationService.PublicName(r, r.UserId is { } u && users.TryGetValue(u, out var n) ? n : null),
                    r.StartUtc, clock.ToLocal(r.StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture),
                    clock.ToLocal(r.EndUtc).ToString("HH:mm", CultureInfo.InvariantCulture), ReservationHolds.IsHeld(r, now, settings));
            });
    }
}
