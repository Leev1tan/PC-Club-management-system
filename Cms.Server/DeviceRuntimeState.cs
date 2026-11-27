using System.Collections.Concurrent;

namespace Cms.Server;

/// <summary>
/// Volatile per-device state that agents report through command acknowledgements
/// (currently: lock state). Kept in memory; after a server restart the state is
/// unknown (null) until the next lock/unlock command is acknowledged.
/// </summary>
public sealed class DeviceRuntimeState
{
    private readonly ConcurrentDictionary<Guid, bool> _locked = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSample = new();

    public bool? IsLocked(Guid deviceId) => _locked.TryGetValue(deviceId, out var locked) ? locked : null;

    public void SetLocked(Guid deviceId, bool locked) => _locked[deviceId] = locked;

    /// <summary>True when a heartbeat sample row is due for this PC (at most one per interval).</summary>
    public bool TakeHeartbeatSample(Guid deviceId, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) return true;
        var due = true;
        _lastSample.AddOrUpdate(deviceId, now, (_, last) =>
        {
            due = now - last >= interval;
            return due ? now : last;
        });
        return due;
    }

    public void Clear()
    {
        _locked.Clear();
        _lastSample.Clear();
    }
}
