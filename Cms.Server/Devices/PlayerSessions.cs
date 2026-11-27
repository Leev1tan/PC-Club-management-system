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
        return new ClientSessionInfo
        {
            Id = s.Id, StartUtc = s.StartUtc, EndUtc = s.EndUtc ?? s.BalanceEndUtc, Paused = s.PausedUtc.HasValue, Prepaid = s.IsPrepaid,
            FromBalance = s.LimitToBalance, Tariff = tariff, Package = package,
            Username = user?.Username, DisplayName = user?.DisplayName, Balance = user?.Balance, Bonus = user?.BonusPoints
        };
    }
}
