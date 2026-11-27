using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Devices;

public static class PlayerSessions
{
    /// <summary>The active session of a PC for its lock screen (heartbeat response); null when there is none.</summary>
    public static async Task<ClientSessionInfo?> InfoAsync(CmsDbContext db, Guid deviceId, CancellationToken ct = default)
    {
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == deviceId && x.Status == "active", ct);
        if (s == null) return null;
        var tariff = s.TariffId.HasValue ? await db.Tariffs.AsNoTracking().Where(t => t.Id == s.TariffId).Select(t => t.Name).FirstOrDefaultAsync(ct) : null;
        var package = s.PackageId.HasValue ? await db.Packages.AsNoTracking().Where(p => p.Id == s.PackageId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var user = s.UserId.HasValue ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == s.UserId, ct) : null;
        // The bar order of the session: open, or the last one closed in the last 15 minutes
        var orders = await db.BarOrders.AsNoTracking().Where(o => o.DeviceId == deviceId && o.SessionId == s.Id).OrderByDescending(o => o.Id).Take(1).ToListAsync(ct);
        var order = orders.FirstOrDefault(o => o.Status is BarOrderStatuses.New or BarOrderStatuses.Preparing ||
                                               DateTimeOffset.UtcNow - o.UpdatedUtc < TimeSpan.FromMinutes(15));
        var lines = order == null ? 0 : await db.BarOrderItems.AsNoTracking().Where(i => i.OrderId == order.Id).SumAsync(i => i.Quantity, ct);
        // The session of a player of an event: the lock screen shows the event and the common end
        var ev = await db.EventPlayers.AsNoTracking().Where(p => p.SessionId == s.Id)
            .Join(db.Events.AsNoTracking(), p => p.EventId, e => e.Id, (p, e) => new { e.Id, e.Name, e.EndUtc }).FirstOrDefaultAsync(ct);
        // The open game play of this session: a lock screen that started again shows "Now: <game>" and follows it
        // (sorted in memory: SQLite cannot order by DateTimeOffset)
        var game = (await db.GamePlays.AsNoTracking().Where(p => p.DeviceId == deviceId && p.SessionId == s.Id && p.EndUtc == null)
                .Join(db.Games.AsNoTracking(), p => p.GameId, g => g.Id, (p, g) => new { g.Id, g.Name, p.StartUtc }).ToListAsync(ct))
            .OrderByDescending(p => p.StartUtc).FirstOrDefault();
        return new ClientSessionInfo
        {
            Id = s.Id, StartUtc = s.StartUtc, EndUtc = s.EndUtc ?? s.BalanceEndUtc, Paused = s.PausedUtc.HasValue, Prepaid = s.IsPrepaid,
            FromBalance = s.LimitToBalance, Tariff = tariff, Package = package,
            Username = user?.Username, DisplayName = user?.DisplayName, Balance = user?.Balance, Bonus = user?.BonusPoints,
            Order = order == null ? null : new ClientOrderInfo { Id = order.Id, Status = order.Status, Payment = order.Payment, Total = order.Total, Items = lines },
            Event = ev == null ? null : new ClientEventInfo { Id = ev.Id, Name = ev.Name, EndUtc = ev.EndUtc },
            Game = game == null ? null : new ClientGameInfo { Id = game.Id, Name = game.Name }
        };
    }
}
