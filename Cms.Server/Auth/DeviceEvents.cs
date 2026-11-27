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

    public static async Task<Result> RecordAsync(CmsDbContext db, AuditLogger? audit, DeviceEntity device, DeviceEventRequest e,
        DateTimeOffset now, ClubClock? clock = null, CancellationToken ct = default)
    {
        if (e.Id == Guid.Empty || !string.Equals(e.Type, LauncherEvent.StaffUnlock, StringComparison.Ordinal)) return Result.Invalid;
        if (e.OccurredUtc < now.AddDays(-30) || e.OccurredUtc > now.AddDays(1)) return Result.Invalid;
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
}
