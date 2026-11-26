using System.Collections.Concurrent;

namespace Cms.Server.Auth;

/// <summary>
/// Locks a (kind, user name, client IP) combination for 15 minutes after 5 failed attempts in
/// 15 minutes. Together with the per-IP rate limit this stops PIN and password guessing. The key
/// includes the IP, so an attacker cannot lock the owner out from another PC. In memory only.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Disabled in demo mode: the demo passwords are public, and visitors share IPs.</summary>
    public bool Enabled { get; init; } = true;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStartUtc;
        public DateTimeOffset? LockedUntilUtc;
    }

    public static string Key(string kind, string? username, string? ip) =>
        $"{kind}|{(username ?? string.Empty).ToLowerInvariant()}|{ip ?? "unknown"}";

    public bool IsLockedOut(string key, DateTimeOffset now)
    {
        if (!Enabled || !_entries.TryGetValue(key, out var entry)) return false;
        lock (entry) return entry.LockedUntilUtc is { } until && now < until;
    }

    public void RecordFailure(string key, DateTimeOffset now)
    {
        if (!Enabled) return;
        if (_entries.Count > 10_000) Prune(now);
        var entry = _entries.GetOrAdd(key, _ => new Entry { WindowStartUtc = now });
        lock (entry)
        {
            if (now - entry.WindowStartUtc > Window)
            {
                entry.Failures = 0;
                entry.WindowStartUtc = now;
            }
            entry.Failures++;
            if (entry.Failures >= MaxFailures)
            {
                entry.LockedUntilUtc = now + Lockout;
                entry.Failures = 0;
                entry.WindowStartUtc = now;
            }
        }
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                var expired = now - entry.WindowStartUtc > Window && (entry.LockedUntilUtc is not { } until || now >= until);
                if (expired) _entries.TryRemove(key, out _);
            }
        }
    }
}
