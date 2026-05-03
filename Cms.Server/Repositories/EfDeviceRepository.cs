using System.Security.Cryptography;
using System.Text.Json;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Repositories;

public class EfDeviceRepository : IDeviceRepository
{
    private readonly CmsDbContext _db;

    public EfDeviceRepository(CmsDbContext db)
    {
        _db = db;
    }

    public DeviceRegistrationResponse Register(DeviceRegistrationRequest request)
    {
        // Check if device with same hostname already exists — re-register
        var existing = _db.Devices.FirstOrDefault(d => d.Hostname == request.Hostname);
        if (existing != null)
        {
            existing.OsVersion = request.OsVersion;
            existing.AgentVersion = request.AgentVersion;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            _db.SaveChanges();
            return new DeviceRegistrationResponse(existing.Id, existing.DeviceKey);
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            Hostname = request.Hostname,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            DeviceKey = key
        };
        _db.Devices.Add(device);
        _db.SaveChanges();
        return new DeviceRegistrationResponse(device.Id, key);
    }

    public bool Heartbeat(string deviceKey, DeviceHeartbeatRequest request)
    {
        var device = _db.Devices.FirstOrDefault(d => d.DeviceKey == deviceKey);
        if (device == null) return false;

        device.LastSeenUtc = DateTimeOffset.UtcNow;
        device.LastIp = request.Ip;
        _db.Heartbeats.Add(new HeartbeatEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
            CpuPercent = request.CpuPercent,
            MemPercent = request.MemPercent,
            ActiveUser = request.ActiveUser,
            Ip = request.Ip
        });
        _db.SaveChanges();
        return true;
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
        var retryBefore = DateTimeOffset.UtcNow.AddSeconds(-30);
        var candidates = _db.Commands
            .Where(c => c.DeviceId == deviceId &&
                        (c.Status == "pending" || c.Status == "delivered"))
            .ToList();

        var pending = candidates
            .Where(c => c.Status == "pending" || c.CreatedUtc <= retryBefore)
            .OrderBy(c => c.CreatedUtc)
            .Take(Math.Max(1, max))
            .ToList();

        foreach (var cmd in pending)
        {
            cmd.Status = "delivered";
        }
        if (pending.Count > 0) _db.SaveChanges();

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
        return true;
    }
}
