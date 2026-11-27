using Cms.Server.Accounts;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>
/// Demo club accounts (spec area 8): only fake accounts ("demo-steam-01" ...) with random secrets made at every reset; they
/// belong to no platform and log in nowhere. Six Steam accounts for the games that need one (GTA V, Rust, EA SPORTS FC
/// 25), two Epic accounts (one without a secret, so the admin UI shows both states). The PCs that play such a game at the
/// reset get a lease, and the simulated agent fetches the credentials once and drops them.
/// </summary>
public static class DemoAccounts
{
    public static async Task SeedAsync(CmsDbContext db, GameAccountVault vault, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var clock = new ClubClock(new FixedTimeProvider(now), zone);
        var service = new GameAccountService(db, vault, clock, new AuditLogger(db, new HttpContextAccessor()), new StaffActor(null, "demo-owner"));
        for (var i = 1; i <= 6; i++)
            await service.AddAsync(new AddGameAccountRequest(AccountPlatforms.Steam, $"demo-steam-{i:00}", GameAccountVault.RandomSecret()));
        await service.AddAsync(new AddGameAccountRequest(AccountPlatforms.Epic, "demo-epic-01", GameAccountVault.RandomSecret()));
        await service.AddAsync(new AddGameAccountRequest(AccountPlatforms.Epic, "demo-epic-02", null));
        db.ChangeTracker.Clear();

        // The PCs that play a game that needs an account now get one (as if their agent asked for it)
        var games = await db.Games.AsNoTracking().Where(g => g.AccountPlatform != null).Select(g => g.Id).ToListAsync(ct);
        var plays = await db.GamePlays.AsNoTracking().Where(p => p.EndUtc == null && games.Contains(p.GameId)).ToListAsync(ct);
        foreach (var play in plays)
        {
            await LeaseAsync(db, vault, play.DeviceId, play.GameId, now, null, clock);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>The simulated agent: a lease for the PC and the game, then the credentials once (dropped at once).</summary>
    public static async Task LeaseAsync(CmsDbContext db, GameAccountVault? vault, Guid deviceId, Guid gameId, DateTimeOffset now, ILogger? logger,
        ClubClock? clock = null)
    {
        var service = new GameAccountService(db, vault ?? GameAccountVault.Ephemeral(), clock ?? new ClubClock(new FixedTimeProvider(now), ClubTimeZone.Default),
            new AuditLogger(db, new HttpContextAccessor()));
        var lease = await service.LeaseAsync(deviceId, gameId);
        if (!lease.Ok)
        {
            logger?.LogDebug("Demo: no club account for {Device}: {Code}", deviceId, lease.Error!.Code);
            return;
        }
        if (lease.Value!.Delivered || vault == null) return;
        db.ChangeTracker.Clear();
        var credential = await service.DeliverAsync(deviceId, lease.Value.LeaseId);
        logger?.LogDebug("Demo: club account {Mask} on {Device} ({Result})", lease.Value.LoginMask, deviceId, credential.Ok ? "handed out" : credential.Error!.Code);
    }
}
