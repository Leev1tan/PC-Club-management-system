using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Devices;

/// <summary>Current game of a PC on the PC map.</summary>
public sealed record CurrentGame(Guid GameId, string Name, string Color, DateTimeOffset StartUtc);

/// <summary>One line of the "top games" of the analytics: plays started or running in the period and their hours in it.</summary>
public sealed record GameStat(Guid GameId, string Name, string Category, string Color, int Plays, decimal Hours, decimal? Share);

/// <summary>
/// Games played on the PCs, reported by the lock screen (game_started / game_stopped through the agent).
/// A play belongs to the active session of the PC; at most one play per PC is open. Plays are not
/// audited (they are not money or access), but only catalog entries are accepted.
/// </summary>
public static class GamePlays
{
    /// <summary>Starts a play of a catalog entry; closes another open play of the PC first. False: unknown or inactive game.</summary>
    public static async Task<bool> StartAsync(CmsDbContext db, Guid deviceId, Guid gameId, DateTimeOffset at, CancellationToken ct = default)
    {
        if (!await db.Games.AsNoTracking().AnyAsync(g => g.Id == gameId && g.IsActive, ct)) return false;
        var open = await db.GamePlays.Where(p => p.DeviceId == deviceId && p.EndUtc == null).ToListAsync(ct);
        var session = await db.Sessions.AsNoTracking().Where(s => s.DeviceId == deviceId && s.Status == "active")
            .Select(s => new { s.Id, s.UserId }).FirstOrDefaultAsync(ct);
        // The same game again (the agent sent the event twice): keep the running play
        if (open.Any(p => p.GameId == gameId && p.SessionId == session?.Id)) return true;
        foreach (var p in open) Close(p, at, GamePlayEndReasons.Switched);
        db.GamePlays.Add(new GamePlayEntity
        {
            Id = Guid.NewGuid(), DeviceId = deviceId, GameId = gameId, SessionId = session?.Id, UserId = session?.UserId, StartUtc = at
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Ends the open play of this game on the PC (nothing to do when there is none).</summary>
    public static async Task StopAsync(CmsDbContext db, Guid deviceId, Guid gameId, DateTimeOffset at, CancellationToken ct = default)
    {
        var open = await db.GamePlays.Where(p => p.DeviceId == deviceId && p.GameId == gameId && p.EndUtc == null).ToListAsync(ct);
        foreach (var p in open) Close(p, at, GamePlayEndReasons.Stopped);
        if (open.Count > 0) await db.SaveChangesAsync(ct);
    }

    private static void Close(GamePlayEntity play, DateTimeOffset at, string reason)
    {
        play.EndUtc = at < play.StartUtc ? play.StartUtc : at;
        play.EndReason = reason;
    }

    /// <summary>
    /// Closes open plays whose session is no longer the active session of the PC (the session ended, was
    /// moved, or the play had no session): at the end of the session, else now. Returns the number closed.
    /// </summary>
    public static async Task<int> CloseStaleAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var open = await db.GamePlays.Where(p => p.EndUtc == null).ToListAsync(ct);
        if (open.Count == 0) return 0;
        var deviceIds = open.Select(p => p.DeviceId).Distinct().ToList();
        var active = (await db.Sessions.AsNoTracking().Where(s => s.Status == "active" && deviceIds.Contains(s.DeviceId))
            .Select(s => new { s.DeviceId, s.Id }).ToListAsync(ct)).ToDictionary(s => s.DeviceId, s => s.Id);
        var stale = open.Where(p => p.SessionId == null || !active.TryGetValue(p.DeviceId, out var sid) || sid != p.SessionId).ToList();
        if (stale.Count == 0) return 0;
        var sessionIds = stale.Where(p => p.SessionId.HasValue).Select(p => p.SessionId!.Value).Distinct().ToList();
        var ends = (await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).Select(s => new { s.Id, s.EndUtc, s.Status }).ToListAsync(ct))
            .ToDictionary(s => s.Id);
        foreach (var p in stale)
        {
            var end = p.SessionId.HasValue && ends.TryGetValue(p.SessionId.Value, out var s) && s.Status == "ended" && s.EndUtc is { } e && e < now ? e : now;
            Close(p, end, GamePlayEndReasons.SessionEnded);
        }
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    /// <summary>The running game of each PC that has an active session with an open play.</summary>
    public static async Task<Dictionary<Guid, CurrentGame>> CurrentAsync(CmsDbContext db, IReadOnlyDictionary<Guid, Guid> activeSessionByDevice, CancellationToken ct = default)
    {
        var open = await db.GamePlays.AsNoTracking().Where(p => p.EndUtc == null).ToListAsync(ct);
        open = open.Where(p => p.SessionId.HasValue && activeSessionByDevice.TryGetValue(p.DeviceId, out var sid) && sid == p.SessionId).ToList();
        if (open.Count == 0) return new();
        var gameIds = open.Select(p => p.GameId).Distinct().ToList();
        var games = await db.Games.AsNoTracking().Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        return open.Where(p => games.ContainsKey(p.GameId))
            .GroupBy(p => p.DeviceId)
            .ToDictionary(g => g.Key, g =>
            {
                var p = g.OrderByDescending(x => x.StartUtc).First();
                var game = games[p.GameId];
                return new CurrentGame(game.Id, game.Name, game.Color, p.StartUtc);
            });
    }

    /// <summary>
    /// Top games of [fromUtc, endUtc): each play counts once if it overlaps the period; its hours are the
    /// part inside the period (an open play runs until now). Share = hours / hours of all games.
    /// SQLite cannot compare DateTimeOffset in SQL, so the plays are filtered in memory there.
    /// </summary>
    public static async Task<IReadOnlyList<GameStat>> TopAsync(CmsDbContext db, DateTimeOffset fromUtc, DateTimeOffset endUtc, DateTimeOffset now,
        int take = 10, CancellationToken ct = default)
    {
        List<GamePlayEntity> plays;
        if (db.Database.IsNpgsql())
            plays = await db.GamePlays.AsNoTracking().Where(p => p.StartUtc < endUtc && (p.EndUtc == null || p.EndUtc > fromUtc)).ToListAsync(ct);
        else
            plays = (await db.GamePlays.AsNoTracking().ToListAsync(ct)).Where(p => p.StartUtc < endUtc && (p.EndUtc == null || p.EndUtc > fromUtc)).ToList();
        if (plays.Count == 0) return Array.Empty<GameStat>();

        double Hours(GamePlayEntity p)
        {
            var start = p.StartUtc > fromUtc ? p.StartUtc : fromUtc;
            var end = p.EndUtc ?? now;
            if (end > endUtc) end = endUtc;
            return end > start ? (end - start).TotalHours : 0;
        }

        var gameIds = plays.Select(p => p.GameId).Distinct().ToList();
        var games = await db.Games.AsNoTracking().Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var byGame = plays.Where(p => games.ContainsKey(p.GameId)).GroupBy(p => p.GameId)
            .Select(g => (Game: games[g.Key], Plays: g.Count(), Hours: g.Sum(Hours))).ToList();
        var total = byGame.Sum(g => g.Hours);
        return byGame
            .OrderByDescending(g => g.Hours).ThenByDescending(g => g.Plays).ThenBy(g => g.Game.Name)
            .Take(take)
            .Select(g => new GameStat(g.Game.Id, g.Game.Name, g.Game.Category, g.Game.Color, g.Plays,
                Math.Round((decimal)g.Hours, 1), total > 0 ? Math.Round((decimal)(g.Hours / total * 100), 1) : null))
            .ToList();
    }
}
