using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Auth;

/// <summary>
/// Events that a PC reports after the fact (agent inbox, see Cms.Shared.LauncherEvent). The staff PIN
/// unlock works offline on the PC, so the server learns about it from the agent, possibly later.
/// </summary>
public static class DeviceEvents
{
    public enum Result { Recorded, Duplicate, Invalid, TooMany }

    public const int MaxPerHour = 30;

    /// <summary>Game events per PC and hour (a fake flood of the inbox cannot fill the table).</summary>
    public const int MaxGameEventsPerHour = 120;

    public static async Task<Result> RecordAsync(CmsDbContext db, AuditLogger? audit, DeviceEntity device, DeviceEventRequest e,
        DateTimeOffset now, ClubClock? clock = null, CancellationToken ct = default, DeviceRuntimeState? runtime = null)
    {
        if (e.Id == Guid.Empty || e.OccurredUtc < now.AddDays(-30) || e.OccurredUtc > now.AddDays(1)) return Result.Invalid;
        if (e.Type is LauncherEvent.GameStarted or LauncherEvent.GameStopped)
            return await RecordGameAsync(db, device, e, now, runtime, ct);
        if (e.Type == LauncherEvent.SessionTeardown)
            return await RecordTeardownAsync(db, audit, device, e, now, clock, runtime, ct);
        if (!string.Equals(e.Type, LauncherEvent.StaffUnlock, StringComparison.Ordinal)) return Result.Invalid;
        if (audit == null) return Result.Invalid;

        var target = device.Id.ToString();
        var eventId = e.Id.ToString("N");
        if (await db.AuditLog.AnyAsync(a => a.Action == AuditActions.EmergencyUnlock && a.TargetId == target && a.DetailsJson!.Contains(eventId), ct))
            return Result.Duplicate;
        // SQLite cannot compare DateTimeOffset in SQL: look at the last entries of this PC in memory
        var recent = await db.AuditLog.AsNoTracking().Where(a => a.Action == AuditActions.EmergencyUnlock && a.TargetId == target)
            .OrderByDescending(a => a.Id).Take(MaxPerHour).Select(a => a.CreatedUtc).ToListAsync(ct);
        if (recent.Count >= MaxPerHour && recent.All(t => t > now.AddHours(-1))) return Result.TooMany;

        var reason = e.LockReason switch
        {
            nameof(LockReason.AgentSilent) => "the agent was not responding",
            nameof(LockReason.NoAgentState) => "the agent state was missing",
            nameof(LockReason.LockedByAgent) => "the PC was locked",
            _ => "lock state unknown"
        };
        var local = (clock ?? ClubClock.System()).ToLocal(e.OccurredUtc);
        await audit.WriteAsync(AuditActions.EmergencyUnlock, "device", device.Id,
            $"Emergency staff PIN unlock on {device.Hostname} at {local:yyyy-MM-dd HH:mm} ({reason})",
            new { eventId, device.Hostname, occurredUtc = e.OccurredUtc, reportedUtc = now, lockReason = e.LockReason,
                  windowsUser = e.WindowsUser is { Length: > 64 } w ? w[..64] : e.WindowsUser }, ct: ct);
        return Result.Recorded;
    }

    /// <summary>The teardown of an ended session: audit device.session_teardown (each event id once).</summary>
    private static async Task<Result> RecordTeardownAsync(CmsDbContext db, AuditLogger? audit, DeviceEntity device, DeviceEventRequest e,
        DateTimeOffset now, ClubClock? clock, DeviceRuntimeState? runtime, CancellationToken ct)
    {
        if (audit == null) return Result.Invalid;
        var target = device.Id.ToString();
        var eventId = e.Id.ToString("N");
        if (await db.AuditLog.AnyAsync(a => a.Action == AuditActions.SessionTeardown && a.TargetId == target && a.DetailsJson!.Contains(eventId), ct))
            return Result.Duplicate;
        if (runtime != null && !runtime.TakeGameEvent(device.Id, now, MaxGameEventsPerHour)) return Result.TooMany;

        static string Clip(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] : s;
        var killed = (e.Killed ?? Array.Empty<string>()).Take(30).Select(k => Clip(k, 60)).ToList();
        var removedPaths = (e.RemovedPaths ?? Array.Empty<string>()).Take(15).Select(p => Clip(p, 200)).ToList();
        var names = killed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var closed = names.Count == 0 ? "no program to close" : $"{killed.Count} program(s) closed ({string.Join(", ", names.Take(8))}{(names.Count > 8 ? ", ..." : "")})";
        var failed = e.KillFailed is > 0 ? $", {e.KillFailed} not closed" : "";
        var cleanup = e.CleanupEnabled == true
            ? $"cleanup removed {e.RemovedFiles ?? 0} file(s) and {e.RemovedDirectories ?? 0} folder(s) in {removedPaths.Count} path(s)" + (e.CleanupErrors is > 0 ? $", {e.CleanupErrors} error(s)" : "")
            : "cleanup off";
        var local = (clock ?? ClubClock.System()).ToLocal(e.OccurredUtc);
        await audit.WriteAsync(AuditActions.SessionTeardown, "device", device.Id,
            $"Session teardown on {device.Hostname} at {local:HH:mm}: {closed}{failed}; {cleanup}",
            new { eventId, device.Hostname, occurredUtc = e.OccurredUtc, killed, killFailed = e.KillFailed, cleanupEnabled = e.CleanupEnabled,
                  removedFiles = e.RemovedFiles, removedDirectories = e.RemovedDirectories, removedPaths, cleanupErrors = e.CleanupErrors,
                  windowsUser = Clip(e.WindowsUser, 64) }, ct: ct);
        return Result.Recorded;
    }

    /// <summary>
    /// Current game of the PC: a play opens or closes now (the event time only when it is in the last
    /// minutes, so a PC clock that is wrong cannot move plays into other days).
    /// </summary>
    private static async Task<Result> RecordGameAsync(CmsDbContext db, DeviceEntity device, DeviceEventRequest e, DateTimeOffset now,
        DeviceRuntimeState? runtime, CancellationToken ct)
    {
        if (e.GameId is not { } gameId || gameId == Guid.Empty) return Result.Invalid;
        if (runtime != null && !runtime.TakeGameEvent(device.Id, now, MaxGameEventsPerHour)) return Result.TooMany;
        var at = e.OccurredUtc <= now && e.OccurredUtc >= now.AddMinutes(-10) ? e.OccurredUtc : now;
        if (e.Type == LauncherEvent.GameStarted)
            return await Cms.Server.Devices.GamePlays.StartAsync(db, device.Id, gameId, at, ct) ? Result.Recorded : Result.Invalid;
        await Cms.Server.Devices.GamePlays.StopAsync(db, device.Id, gameId, at, ct);
        return Result.Recorded;
    }
}
