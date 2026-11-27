using System.Globalization;
using Cms.Server.Billing;

namespace Cms.Server.Tests;

/// <summary>
/// Instants for the demo seed tests. The demo shifts change at 09:00 and 21:00 club time and the club day
/// starts at 00:00, so a seed made a minute after such a change has an almost empty open shift or day.
/// The tests check the seed at these instants too, not only at the wall-clock time of the test run
/// (v0.6.0-beta.1 failed in CI because the run started at 09:01 club time).
/// </summary>
internal static class ClubTimePoints
{
    /// <summary>"now", or the most recent past instant at the given club time ("HH:mm", club time zone).</summary>
    public static DateTimeOffset At(string clubTime)
    {
        var now = DateTimeOffset.UtcNow;
        if (clubTime == "now") return now;
        var zone = ClubTimeZone.Default;
        var time = TimeOnly.ParseExact(clubTime, "HH:mm", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var back = 0; back < 3; back++)
        {
            var local = today.AddDays(-back).ToDateTime(time);
            var instant = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (instant <= now) return instant;
        }
        throw new InvalidOperationException($"No past instant at {clubTime}");
    }
}
