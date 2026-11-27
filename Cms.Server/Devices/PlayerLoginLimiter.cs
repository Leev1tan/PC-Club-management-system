using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Cms.Server.Devices;

/// <summary>
/// Lockout of the player login on the lock screens, in memory (like the staff login throttle):
///  - per account (the user name as typed, also names that do not exist, so a locked answer does not
///    tell which names exist): 5 failures in 15 minutes lock the name for 15 minutes on every PC;
///  - per PC: 10 failures in 15 minutes lock the login of that PC for 15 minutes (one PC cannot try
///    many accounts). The club PCs share one IP behind the router, so there is no per-IP limit here.
/// A successful login clears both counters. A manager or the owner can clear a lockout in the admin UI
/// (PlayerLockoutsController); the list shows a name only when it is an account (players type their password
/// into the name field), otherwise only an opaque id (HMAC with a random key of this process), and the audit
/// log gets nothing derived from such a name.
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

    /// <summary>Random key of this process for <see cref="AccountId"/> (the lockouts live in memory, so the ids need not survive a restart).</summary>
    private readonly byte[] _idKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Opaque id of a typed user name for the lockout list: HMAC-SHA256 with a random key of this process, so it
    /// cannot be reversed (players type PINs into the name field) and differs between processes. Never store it.
    /// </summary>
    public string AccountId(string? username) =>
        Convert.ToHexString(HMACSHA256.HashData(_idKey, Encoding.UTF8.GetBytes(AccountKey(username))))[..24].ToLowerInvariant();

    public sealed record LockedAccount(string Id, string Name, DateTimeOffset LockedUntilUtc);
    public sealed record LockedDevice(Guid DeviceId, DateTimeOffset LockedUntilUtc);

    /// <summary>Names locked now (the name as typed; show it only when it is an account).</summary>
    public IReadOnlyList<LockedAccount> LockedAccounts(DateTimeOffset now) =>
        _accounts.Select(kv => (kv.Key, Until: Until(kv.Value, now))).Where(x => x.Until.HasValue)
            .Select(x => new LockedAccount(AccountId(x.Key), x.Key, x.Until!.Value)).OrderBy(x => x.LockedUntilUtc).ToList();

    /// <summary>PCs whose player login is locked now.</summary>
    public IReadOnlyList<LockedDevice> LockedDevices(DateTimeOffset now) =>
        _devices.Select(kv => (kv.Key, Until: Until(kv.Value, now))).Where(x => x.Until.HasValue)
            .Select(x => new LockedDevice(x.Key, x.Until!.Value)).OrderBy(x => x.LockedUntilUtc).ToList();

    /// <summary>Clears the lockout and the failures of a name (as typed or by its opaque id). Returns the name, or null when it was not locked.</summary>
    public string? ClearAccount(string? username, string? id, DateTimeOffset now)
    {
        var key = username != null ? AccountKey(username)
            : _accounts.Keys.FirstOrDefault(k => string.Equals(AccountId(k), id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (key == null || !_accounts.TryGetValue(key, out var c) || Until(c, now) == null) return null;
        return _accounts.TryRemove(key, out _) ? key : null;
    }

    /// <summary>Clears the lockout and the failures of a PC. False when it was not locked.</summary>
    public bool ClearDevice(Guid deviceId, DateTimeOffset now) =>
        _devices.TryGetValue(deviceId, out var c) && Until(c, now) != null && _devices.TryRemove(deviceId, out _);

    private static DateTimeOffset? Until(Counter c, DateTimeOffset now)
    {
        lock (c) return c.LockedUntilUtc is { } until && now < until ? until : null;
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
