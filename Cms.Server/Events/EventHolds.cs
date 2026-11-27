using System.Globalization;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Events;

/// <summary>The open event that holds a PC now (from HoldMinutes before the start until the end).</summary>
public sealed record EventHold(long Id, string Name, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string StartLocal, string EndLocal, bool Running);

/// <summary>The next open event of a PC on the PC map (held now, or starting within 12 hours).</summary>
public sealed record PcEventView(long Id, string Name, DateTimeOffset StartUtc, string StartLocal, string EndLocal, bool Held, bool Running);

/// <summary>
/// Where the open events (scheduled or running) book their PCs: the hold of a PC (lock screen, session start,
/// player login), the next event of each PC (PC map), and the busy times for the reservation plan.
/// </summary>
public static class EventHolds
{
    /// <summary>Plan items of events have ids below this value (reservations have positive ids).</summary>
    public const long PlanIdBase = -2_000_000_000_000;

    public static long PlanId(long eventId, int index) => PlanIdBase - eventId * 1_000 - index;

    public static bool IsEventPlanId(long id) => id <= PlanIdBase;

    public static long EventOfPlanId(long id) => (PlanIdBase - id) / 1_000;

    private sealed record OpenEvent(EventEntity Event, List<Guid> Pcs);

    /// <summary>Open events that overlap [from, to) with their PCs (club days first, then exact times in memory: SQLite).</summary>
    private static async Task<List<OpenEvent>> OpenAsync(CmsDbContext db, ClubClock clock, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var lo = ReservationService.DayOf(from.AddMinutes(-EventLimits.MaxMinutes), clock);
        var hi = ReservationService.DayOf(to, clock);
        var events = (await db.Events.AsNoTracking()
                .Where(e => (e.Status == EventStatuses.Scheduled || e.Status == EventStatuses.Running) && e.StartDay >= lo && e.StartDay <= hi)
                .ToListAsync(ct))
            .Where(e => e.StartUtc < to && from < e.EndUtc).ToList();
        if (events.Count == 0) return new();
        var ids = events.Select(e => e.Id).ToList();
        var pcs = await db.EventDevices.AsNoTracking().Where(d => ids.Contains(d.EventId)).ToListAsync(ct);
        return events.Select(e => new OpenEvent(e, pcs.Where(p => p.EventId == e.Id).Select(p => p.DeviceId).ToList())).ToList();
    }

    /// <summary>Busy times of the event PCs in [from, to) for the reservation plan (fixed items on their PCs).</summary>
    public static async Task<List<PlanItem>> PlanItemsAsync(CmsDbContext db, ClubClock clock, DateTimeOffset from, DateTimeOffset to, long? exceptEvent = null,
        CancellationToken ct = default)
    {
        var items = new List<PlanItem>();
        foreach (var open in await OpenAsync(db, clock, from, to, ct))
        {
            if (open.Event.Id == exceptEvent) continue;
            items.AddRange(open.Pcs.Select((pc, i) => new PlanItem(PlanId(open.Event.Id, i), pc, open.Event.StartUtc, open.Event.EndUtc)));
        }
        return items;
    }

    public static bool IsHeld(EventEntity e, DateTimeOffset now, int holdMinutes) =>
        e.Status is EventStatuses.Scheduled or EventStatuses.Running && now >= e.StartUtc.AddMinutes(-holdMinutes) && now < e.EndUtc;

    /// <summary>The event that holds the PC now, or null.</summary>
    public static async Task<EventHold?> ActiveAsync(CmsDbContext db, Guid deviceId, ClubClock clock, CancellationToken ct = default)
    {
        var settings = await ReservationSettings.LoadAsync(db, ct);
        var now = clock.UtcNow;
        var ids = await db.EventDevices.AsNoTracking().Where(d => d.DeviceId == deviceId).Select(d => d.EventId).ToListAsync(ct);
        if (ids.Count == 0) return null;
        var today = ReservationService.DayOf(now, clock);
        var hold = (await db.Events.AsNoTracking()
                .Where(e => ids.Contains(e.Id) && (e.Status == EventStatuses.Scheduled || e.Status == EventStatuses.Running) && e.StartDay >= today - 1 && e.StartDay <= today + 1)
                .ToListAsync(ct))
            .Where(e => IsHeld(e, now, settings.HoldMinutes)).OrderBy(e => e.StartUtc).FirstOrDefault();
        return hold == null ? null : new EventHold(hold.Id, hold.Name, hold.StartUtc, hold.EndUtc, Local(clock, hold.StartUtc), Local(clock, hold.EndUtc),
            hold.Status == EventStatuses.Running);
    }

    /// <summary>The next open event of each PC: held now or starting within 12 hours.</summary>
    public static async Task<Dictionary<Guid, PcEventView>> NextByPcAsync(CmsDbContext db, ClubClock clock, CancellationToken ct = default)
    {
        var settings = await ReservationSettings.LoadAsync(db, ct);
        var now = clock.UtcNow;
        var result = new Dictionary<Guid, PcEventView>();
        foreach (var open in (await OpenAsync(db, clock, now, now + ReservationMap.LookAhead, ct)).OrderBy(o => o.Event.StartUtc))
        {
            var e = open.Event;
            var view = new PcEventView(e.Id, e.Name, e.StartUtc, Local(clock, e.StartUtc), Local(clock, e.EndUtc), IsHeld(e, now, settings.HoldMinutes),
                e.Status == EventStatuses.Running);
            foreach (var pc in open.Pcs) result.TryAdd(pc, view);
        }
        return result;
    }

    public static string Local(ClubClock clock, DateTimeOffset utc) => clock.ToLocal(utc).ToString("HH:mm", CultureInfo.InvariantCulture);
}
