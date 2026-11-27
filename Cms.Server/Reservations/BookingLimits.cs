using System.Collections.Concurrent;

namespace Cms.Server.Reservations;

/// <summary>
/// Limits of the public booking page (in memory; a restart clears them):
///  - per IP address: the rate limiter policies booking-write (5 requests per 10 minutes) and booking-read
///    (120 availability reads per minute), see AuthSetup; Booking:RequestsPerIpPer10Minutes, Booking:ReadsPerIpPerMinute;
///  - per phone number (its last 9 digits): 3 requests per 24 hours, here; and at most MaxOpenPerPhone (setting, 2)
///    open requests or reservations, checked in the database (ReservationService.RequestAsync).
/// </summary>
public sealed class BookingPhoneLimiter
{
    public const int PerDay = 3;
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _attempts = new();

    /// <summary>Counts one request of the phone; false when the phone has used its requests of the last 24 hours.</summary>
    public bool TryRecord(string phoneKey, DateTimeOffset now)
    {
        if (_attempts.Count > 50_000)
            foreach (var (key, list) in _attempts)
                lock (list) if (list.All(t => now - t > Window)) _attempts.TryRemove(key, out _);
        var times = _attempts.GetOrAdd(phoneKey, _ => new List<DateTimeOffset>());
        lock (times)
        {
            times.RemoveAll(t => now - t > Window);
            if (times.Count >= PerDay) return false;
            times.Add(now);
            return true;
        }
    }
}
