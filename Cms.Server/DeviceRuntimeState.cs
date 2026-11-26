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

    public bool? IsLocked(Guid deviceId) => _locked.TryGetValue(deviceId, out var locked) ? locked : null;

    public void SetLocked(Guid deviceId, bool locked) => _locked[deviceId] = locked;

    public void Clear() => _locked.Clear();
}
