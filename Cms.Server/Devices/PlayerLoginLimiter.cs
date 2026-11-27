using System.Collections.Concurrent;

namespace Cms.Server.Devices;

/// <summary>
/// Lockout of the player login on the lock screens, in memory (like the staff login throttle):
///  - per account (the user name as typed, also names that do not exist, so a locked answer does not
///    tell which names exist): 5 failures in 15 minutes lock the name for 15 minutes on every PC;
///  - per PC: 10 failures in 15 minutes lock the login of that PC for 15 minutes (one PC cannot try
///    many accounts). The club PCs share one IP behind the router, so there is no per-IP limit here.
/// A successful login clears both counters.
/// </summary>
public sealed class PlayerLoginLimiter
{
    public const int AccountMaxFailures = 5;
    public const int DeviceMaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private sealed class Counter
    {
        public int Failures;
        public DateTimeOffset WindowStartUtc;
        public DateTimeOffset? LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, Counter> _accounts = new();
    private readonly ConcurrentDictionary<Guid, Counter> _devices = new();

    public static string AccountKey(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>How long the login stays locked for this account or PC (the longer of both); null = not locked.</summary>
    public TimeSpan? LockedFor(Guid deviceId, string username, DateTimeOffset now)
    {
        TimeSpan? Left<TKey>(ConcurrentDictionary<TKey, Counter> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var c)) return null;
            lock (c) return c.LockedUntilUtc is { } until && now < until ? until - now : null;
        }
        var account = Left(_accounts, AccountKey(username));
        var device = Left(_devices, deviceId);
        return account > device || device == null ? account : device;
    }

    /// <summary>Counts a failed login. Returns which lockouts start with this failure.</summary>
    public (bool AccountLocked, bool DeviceLocked) RecordFailure(Guid deviceId, string username, DateTimeOffset now)
    {
        if (_accounts.Count > 10_000) Prune(_accounts, now);
        if (_devices.Count > 10_000) Prune(_devices, now);
        return (Count(_accounts.GetOrAdd(AccountKey(username), _ => new Counter { WindowStartUtc = now }), AccountMaxFailures, now),
                Count(_devices.GetOrAdd(deviceId, _ => new Counter { WindowStartUtc = now }), DeviceMaxFailures, now));
    }

    public void RecordSuccess(Guid deviceId, string username)
    {
        _accounts.TryRemove(AccountKey(username), out _);
        _devices.TryRemove(deviceId, out _);
    }

    private static bool Count(Counter c, int max, DateTimeOffset now)
    {
        lock (c)
        {
            if (now - c.WindowStartUtc > Window)
            {
                c.Failures = 0;
                c.WindowStartUtc = now;
            }
            c.Failures++;
            if (c.Failures < max) return false;
            c.LockedUntilUtc = now + Lockout;
            c.Failures = 0;
            c.WindowStartUtc = now;
            return true;
        }
    }

    private static void Prune<TKey>(ConcurrentDictionary<TKey, Counter> map, DateTimeOffset now) where TKey : notnull
    {
        foreach (var (key, c) in map)
        {
            lock (c)
            {
                if (now - c.WindowStartUtc > Window && (c.LockedUntilUtc is not { } until || now >= until)) map.TryRemove(key, out _);
            }
        }
    }
}
