using System.Collections.Concurrent;

namespace Cms.Server;

/// <summary>
/// Volatile per-device state that agents report: the lock state (every heartbeat of an agent 0.9.1 or newer, and the
/// acknowledgements of lock and unlock commands) and the state of the lock screen. Kept in memory; after a server restart
/// it is known again with the first heartbeat.
/// </summary>
public sealed class DeviceRuntimeState
{
    /// <summary>The values of the heartbeat field "lockScreen" (Cms.Client.LockScreenStates).</summary>
    public static readonly IReadOnlySet<string> LockScreenStates = new HashSet<string> { "ok", "staff_unlock", "silent", "unknown", "kiosk_signin_failed" };

    private readonly ConcurrentDictionary<Guid, bool> _locked = new();
    private readonly ConcurrentDictionary<Guid, string> _lockScreen = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSample = new();

    public bool? IsLocked(Guid deviceId) => _locked.TryGetValue(deviceId, out var locked) ? locked : null;

    public void SetLocked(Guid deviceId, bool locked) => _locked[deviceId] = locked;

    /// <summary>
    /// "ok", "staff_unlock" (staff PIN unlock on the screen), "silent" (no lock screen), "unknown", or "kiosk_signin_failed"
    /// (a kiosk PC without a signed-in user after 3 restarts of the agent in 60 min); null = not reported.
    /// </summary>
    public string? LockScreen(Guid deviceId) => _lockScreen.TryGetValue(deviceId, out var state) ? state : null;

    /// <summary>Keeps a known value of the heartbeat; anything else is ignored.</summary>
    public void SetLockScreen(Guid deviceId, string? state)
    {
        if (state != null && LockScreenStates.Contains(state)) _lockScreen[deviceId] = state;
    }

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

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _gameEvents = new();

    /// <summary>True when the PC may report one more game event (at most max per hour, in memory).</summary>
    public bool TakeGameEvent(Guid deviceId, DateTimeOffset now, int max)
    {
        var queue = _gameEvents.GetOrAdd(deviceId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1)) queue.Dequeue();
            if (queue.Count >= max) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    public void Clear()
    {
        _locked.Clear();
        _lockScreen.Clear();
        _lastSample.Clear();
        _gameEvents.Clear();
    }
}
