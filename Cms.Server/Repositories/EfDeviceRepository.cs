using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Repositories;

public class EfDeviceRepository : IDeviceRepository
{
    private readonly CmsDbContext _db;
    private readonly ConcurrentDictionary<string, Guid> _keyToId = new();
    
    // Static so commands persist across scoped repository instances
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<CommandView>> _commandQueues = new();

    public EfDeviceRepository(CmsDbContext db)
    {
        _db = db;
        // Load existing keys into memory
        foreach (var d in _db.Devices.AsNoTracking())
        {
            _keyToId[d.DeviceKey] = d.Id;
        }
    }

    public DeviceRegistrationResponse Register(DeviceRegistrationRequest request)
    {
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
        _keyToId[key] = device.Id;
        return new DeviceRegistrationResponse(device.Id, key);
    }

    public bool Heartbeat(string deviceKey, DeviceHeartbeatRequest request)
    {
        if (!_keyToId.TryGetValue(deviceKey, out var deviceId)) return false;
        var device = _db.Devices.Find(deviceId);
        if (device == null) return false;

        device.LastSeenUtc = DateTimeOffset.UtcNow;
        device.LastIp = request.Ip;
        _db.Heartbeats.Add(new HeartbeatEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
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
        var cmd = new CommandView(Guid.NewGuid(), request.Type, request.Payload);
        _commandQueues.GetOrAdd(deviceId, _ => new ConcurrentQueue<CommandView>()).Enqueue(cmd);
        return cmd;
    }

    public IEnumerable<CommandView> PollCommands(Guid deviceId, int max)
    {
        if (!_commandQueues.TryGetValue(deviceId, out var queue)) yield break;
        for (int i = 0; i < Math.Max(1, max); i++)
        {
            if (!queue.TryDequeue(out var cmd)) yield break;
            yield return cmd;
        }
    }

    public bool AckCommand(Guid deviceId, Guid commandId, AckCommandRequest request)
    {
        return _db.Devices.Any(d => d.Id == deviceId);
    }
}

