using System.Collections.Concurrent;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Controllers;

namespace Cms.Server.Repositories;

/// <summary>
/// In-memory repository for testing without a database. Not recommended for production.
/// </summary>
public class InMemoryDeviceRepository : IDeviceRepository
{
    private static readonly ConcurrentDictionary<Guid, DeviceRecord> _devices = new();
    private static readonly ConcurrentDictionary<string, Guid> _keyToId = new();
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<CommandRecord>> _commandQueues = new();

    private record DeviceRecord(Guid Id, string Hostname, string OsVersion, string AgentVersion, string DeviceKey)
    {
        public DateTimeOffset? LastSeenUtc { get; set; }
        public string? LastIp { get; set; }
    }

    private record CommandRecord(Guid Id, string Type, string? PayloadJson);

    /// <summary>Always creates a new pending device; never returns the key of an existing one.</summary>
    public DeviceRegistrationResult Register(DeviceRegistrationRequest request)
    {
        var key = DeviceKeys.Generate();
        var device = new DeviceRecord(Guid.NewGuid(), request.Hostname, request.OsVersion, request.AgentVersion, key);
        _devices[device.Id] = device;
        _keyToId[key] = device.Id;
        return DeviceRegistrationResult.Ok(new DeviceRegistrationResponse(device.Id, key, DeviceStatuses.Pending), DeviceRegistrationKind.Pending);
    }

    public string? Heartbeat(string deviceKey, DeviceHeartbeatRequest request)
    {
        if (!_keyToId.TryGetValue(deviceKey, out var deviceId)) return null;
        if (!_devices.TryGetValue(deviceId, out var device)) return null;
        device.LastSeenUtc = DateTimeOffset.UtcNow;
        device.LastIp = request.Ip;
        return DeviceStatuses.Pending;
    }

    public IEnumerable<DeviceView> List()
    {
        var now = DateTimeOffset.UtcNow;
        return _devices.Values.Select(d =>
        {
            var status = (d.LastSeenUtc.HasValue && (now - d.LastSeenUtc.Value) < TimeSpan.FromSeconds(20)) ? "online" : "offline";
            return new DeviceView(d.Id, d.Hostname, d.OsVersion, d.AgentVersion, d.LastSeenUtc, d.LastIp, status);
        });
    }

    public CommandView? EnqueueCommand(Guid deviceId, EnqueueCommandRequest request)
    {
        if (!_devices.ContainsKey(deviceId)) return null;
        var payloadJson = request.Payload != null ? JsonSerializer.Serialize(request.Payload) : null;
        var record = new CommandRecord(Guid.NewGuid(), request.Type, payloadJson);
        _commandQueues.GetOrAdd(deviceId, _ => new ConcurrentQueue<CommandRecord>()).Enqueue(record);

        object? payload = null;
        if (payloadJson != null)
        {
            try { payload = JsonSerializer.Deserialize<object>(payloadJson); }
            catch { payload = payloadJson; }
        }
        return new CommandView(record.Id, record.Type, payload);
    }

    public IEnumerable<CommandView> PollCommands(Guid deviceId, int max)
    {
        if (!_commandQueues.TryGetValue(deviceId, out var queue)) yield break;
        for (int i = 0; i < Math.Max(1, max); i++)
        {
            if (!queue.TryDequeue(out var record)) yield break;
            object? payload = null;
            if (record.PayloadJson != null)
            {
                try { payload = JsonSerializer.Deserialize<object>(record.PayloadJson); }
                catch { payload = record.PayloadJson; }
            }
            yield return new CommandView(record.Id, record.Type, payload);
        }
    }

    public bool AckCommand(Guid deviceId, Guid commandId, AckCommandRequest request)
    {
        return _devices.ContainsKey(deviceId);
    }
}
