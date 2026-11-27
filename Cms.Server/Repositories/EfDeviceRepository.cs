using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Repositories;

public class EfDeviceRepository : IDeviceRepository
{
    private readonly CmsDbContext _db;
    private readonly DeviceRuntimeState? _runtime;
    private readonly DeviceEnrollmentOptions _enrollment;
    private readonly RetentionOptions _retention;

    /// <summary>Devices.LastSeenUtc is written at most this often (online = seen in the last 20 s).</summary>
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromSeconds(5);

    public EfDeviceRepository(CmsDbContext db, DeviceRuntimeState? runtime = null, DeviceEnrollmentOptions? enrollment = null,
        RetentionOptions? retention = null)
    {
        _db = db;
        _runtime = runtime;
        _enrollment = enrollment ?? new DeviceEnrollmentOptions();
        _retention = retention ?? new RetentionOptions();
    }

    /// <summary>
    /// Registration never hands out the key of an existing PC:
    ///  1. With the current device key: updates the PC and returns the same key (re-registration).
    ///  2. With the enrollment token: approved at once; a PC with the same host name gets a NEW key
    ///     (reinstall), which ends the old key.
    ///  3. Without both: a new "pending" PC that does nothing until staff approve it.
    /// </summary>
    public DeviceRegistrationResult Register(DeviceRegistrationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var hostname = (request.Hostname ?? string.Empty).Trim();
        if (hostname.Length is 0 or > 255) return DeviceRegistrationResult.Invalid("Hostname is required (at most 255 characters).");
        var os = Truncate(request.OsVersion, 255);
        var agentVersion = Truncate(request.AgentVersion, 50);

        if (!string.IsNullOrEmpty(request.DeviceKey))
        {
            var hash = DeviceKeys.Hash(request.DeviceKey);
            var known = _db.Devices.FirstOrDefault(d => d.DeviceKey == hash);
            if (known == null || known.Status == DeviceStatuses.Revoked || (request.DeviceId.HasValue && request.DeviceId != known.Id))
                return DeviceRegistrationResult.Unauthorized("Unknown or revoked device key.");
            known.OsVersion = os;
            known.AgentVersion = agentVersion;
            known.LastSeenUtc = now;
            _db.SaveChanges();
            return DeviceRegistrationResult.Ok(new DeviceRegistrationResponse(known.Id, request.DeviceKey, known.Status), DeviceRegistrationKind.ReRegistered);
        }

        var key = DeviceKeys.Generate();
        if (!string.IsNullOrEmpty(request.Token))
        {
            if (!_enrollment.TokenMatches(request.Token))
                return DeviceRegistrationResult.Unauthorized("Wrong enrollment token.");

            // Reinstall of a known PC: keep its zone, position and history, replace the key
            var existing = _db.Devices.Where(d => d.Hostname == hostname && d.Status != DeviceStatuses.Revoked)
                .AsEnumerable()
                .OrderBy(d => d.Status == DeviceStatuses.Approved ? 0 : 1)
                .FirstOrDefault();
            if (existing != null)
            {
                existing.DeviceKey = DeviceKeys.Hash(key);
                existing.Status = DeviceStatuses.Approved;
                existing.OsVersion = os;
                existing.AgentVersion = agentVersion;
                existing.LastSeenUtc = now;
                _db.SaveChanges();
                return DeviceRegistrationResult.Ok(new DeviceRegistrationResponse(existing.Id, key, existing.Status), DeviceRegistrationKind.ReEnrolled);
            }
            return DeviceRegistrationResult.Ok(Create(hostname, os, agentVersion, key, DeviceStatuses.Approved, now), DeviceRegistrationKind.Enrolled);
        }

        if (!_enrollment.AllowPendingRegistration)
            return DeviceRegistrationResult.Unauthorized("An enrollment token is required to register a PC.");
        if (_db.Devices.Count(d => d.Status == DeviceStatuses.Pending) >= _enrollment.MaxPendingDevices)
            return DeviceRegistrationResult.Unauthorized("Too many PCs wait for approval. Approve or reject them in the admin UI.");
        return DeviceRegistrationResult.Ok(Create(hostname, os, agentVersion, key, DeviceStatuses.Pending, now), DeviceRegistrationKind.Pending);
    }

    private DeviceRegistrationResponse Create(string hostname, string os, string agentVersion, string key, string status, DateTimeOffset now)
    {
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            Hostname = hostname,
            OsVersion = os,
            AgentVersion = agentVersion,
            DeviceKey = DeviceKeys.Hash(key),
            Status = status,
            RegisteredUtc = now,
            LastSeenUtc = now
        };
        _db.Devices.Add(device);
        _db.SaveChanges();
        return new DeviceRegistrationResponse(device.Id, key, status);
    }

    private static string Truncate(string? value, int max)
    {
        var v = value?.Trim() ?? string.Empty;
        return v.Length <= max ? v : v[..max];
    }

    public string? Heartbeat(string deviceKey, DeviceHeartbeatRequest request)
    {
        var hash = DeviceKeys.Hash(deviceKey);
        var device = _db.Devices.FirstOrDefault(d => d.DeviceKey == hash);
        if (device == null || device.Status == DeviceStatuses.Revoked) return null;

        // A heartbeat only refreshes "last seen" (at most every 5 s). A metrics sample row is kept at
        // most once per Retention:HeartbeatSampleMinutes per PC (one row per second filled the disk).
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        if (device.LastSeenUtc is not { } seen || now - seen >= LastSeenResolution || device.LastIp != request.Ip)
        {
            device.LastSeenUtc = now;
            device.LastIp = request.Ip;
            changed = true;
        }
        // The MAC address for Wake-on-LAN; an invalid value is ignored (the last valid one stays)
        if (MacAddresses.Normalize(request.Mac) is { } mac && device.MacAddress != mac)
        {
            device.MacAddress = mac;
            changed = true;
        }
        if (_runtime == null || _runtime.TakeHeartbeatSample(device.Id, now, _retention.HeartbeatSampleInterval))
        {
            _db.Heartbeats.Add(new HeartbeatEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                CreatedUtc = now,
                CpuPercent = request.CpuPercent,
                MemPercent = request.MemPercent,
                ActiveUser = request.ActiveUser,
                Ip = request.Ip
            });
            changed = true;
        }
        if (changed) _db.SaveChanges();
        return device.Status;
    }

    public IEnumerable<DeviceView> List()
    {
        var now = DateTimeOffset.UtcNow;
        return _db.Devices.AsNoTracking().AsEnumerable().Select(d =>
        {
            var status = (d.LastSeenUtc.HasValue && (now - d.LastSeenUtc.Value) < TimeSpan.FromSeconds(20)) ? "online" : "offline";
            return new DeviceView(d.Id, d.Hostname, d.OsVersion, d.AgentVersion, d.LastSeenUtc, d.LastIp, status);
        });
    }

    public CommandView? EnqueueCommand(Guid deviceId, EnqueueCommandRequest request)
    {
        if (!_db.Devices.Any(d => d.Id == deviceId)) return null;

        var entity = new CommandEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedUtc = DateTimeOffset.UtcNow,
            Type = request.Type,
            PayloadJson = request.Payload != null ? JsonSerializer.Serialize(request.Payload) : null,
            Status = "pending"
        };
        _db.Commands.Add(entity);
        _db.SaveChanges();

        object? payload = null;
        if (entity.PayloadJson != null)
        {
            try { payload = JsonSerializer.Deserialize<object>(entity.PayloadJson); }
            catch { payload = entity.PayloadJson; }
        }
        return new CommandView(entity.Id, entity.Type, payload);
    }

    public IEnumerable<CommandView> PollCommands(Guid deviceId, int max)
    {
        var now = DateTimeOffset.UtcNow;
        var retryBefore = now.AddSeconds(-30);
        var expireBefore = now - _retention.CommandExpireAfter;
        var all = _db.Commands
            .Where(c => c.DeviceId == deviceId &&
                        (c.Status == "pending" || c.Status == "delivered"))
            .ToList();

        // Too old to run (for example an "unlock" queued while the PC was off)
        foreach (var stale in all.Where(c => c.CreatedUtc < expireBefore))
        {
            stale.Status = "expired";
            stale.Result = "Not delivered in time";
        }
        var candidates = all.Where(c => c.Status != "expired").ToList();

        var pending = candidates
            .Where(c => c.Status == "pending" || c.CreatedUtc <= retryBefore)
            .OrderBy(c => c.CreatedUtc)
            .Take(Math.Max(1, max))
            .ToList();

        foreach (var cmd in pending)
        {
            cmd.Status = "delivered";
        }
        if (pending.Count > 0 || candidates.Count != all.Count) _db.SaveChanges();

        return pending.Select(cmd =>
        {
            object? payload = null;
            if (cmd.PayloadJson != null)
            {
                try { payload = JsonSerializer.Deserialize<object>(cmd.PayloadJson); }
                catch { payload = cmd.PayloadJson; }
            }
            return new CommandView(cmd.Id, cmd.Type, payload);
        });
    }

    public bool AckCommand(Guid deviceId, Guid commandId, AckCommandRequest request)
    {
        var cmd = _db.Commands.FirstOrDefault(c => c.Id == commandId && c.DeviceId == deviceId);
        if (cmd == null) return false;
        cmd.Status = request.Status;
        cmd.Result = request.Result;
        _db.SaveChanges();

        // Remember the lock state the agent confirmed, so the admin UI can show it
        if (_runtime != null && string.Equals(request.Status, "done", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(cmd.Type, CommandTypes.Lock, StringComparison.OrdinalIgnoreCase))
                _runtime.SetLocked(deviceId, true);
            else if (string.Equals(cmd.Type, CommandTypes.Unlock, StringComparison.OrdinalIgnoreCase))
                _runtime.SetLocked(deviceId, false);
        }
        return true;
    }
}
