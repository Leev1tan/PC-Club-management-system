using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cms.Server.Controllers;

namespace Cms.Server.Repositories;

public class InMemoryDeviceRepository : IDeviceRepository
{
    private class Device
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Hostname { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string AgentVersion { get; set; } = string.Empty;
        public string DeviceKey { get; set; } = string.Empty; // plain for MVP
        public DateTimeOffset? LastSeenUtc { get; set; }
        public string? LastIp { get; set; }
        public ConcurrentQueue<Command> Queue { get; } = new();
    }

    private class Command
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Type { get; set; } = string.Empty;
        public object? Payload { get; set; }
    }

    private readonly ConcurrentDictionary<string, Device> _keyToDevice = new();
    private readonly ConcurrentDictionary<Guid, Device> _idToDevice = new();

    public DeviceRegistrationResponse Register(DeviceRegistrationRequest request)
    {
        // Generate a random device key (MVP plain string)
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var device = new Device
        {
            Hostname = request.Hostname,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            DeviceKey = key
        };
        _keyToDevice[key] = device;
        _idToDevice[device.Id] = device;
        return new DeviceRegistrationResponse(device.Id, key);
    }

    public bool Heartbeat(string deviceKey, DeviceHeartbeatRequest request)
    {
        if (!_keyToDevice.TryGetValue(deviceKey, out var device)) return false;
        device.LastSeenUtc = DateTimeOffset.UtcNow;
        device.LastIp = request.Ip;
        return true;
    }

    public IEnumerable<DeviceView> List()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var d in _idToDevice.Values)
        {
            var last = d.LastSeenUtc;
            var status = (last.HasValue && (now - last.Value) < TimeSpan.FromSeconds(20)) ? "online" : "offline";
            yield return new DeviceView(d.Id, d.Hostname, d.OsVersion, d.AgentVersion, d.LastSeenUtc, d.LastIp, status);
        }
    }

    public CommandView? EnqueueCommand(Guid deviceId, EnqueueCommandRequest request)
    {
        if (!_idToDevice.TryGetValue(deviceId, out var d)) return null;
        var cmd = new Command { Type = request.Type, Payload = request.Payload };
        d.Queue.Enqueue(cmd);
        return new CommandView(cmd.Id, cmd.Type, cmd.Payload);
    }

    public IEnumerable<CommandView> PollCommands(Guid deviceId, int max)
    {
        if (!_idToDevice.TryGetValue(deviceId, out var d)) yield break;
        for (int i = 0; i < Math.Max(1, max); i++)
        {
            if (!d.Queue.TryDequeue(out var cmd)) yield break;
            yield return new CommandView(cmd.Id, cmd.Type, cmd.Payload);
        }
    }

    public bool AckCommand(Guid deviceId, Guid commandId, AckCommandRequest request)
    {
        // In-memory no-op for MVP
        return _idToDevice.ContainsKey(deviceId);
    }
}

