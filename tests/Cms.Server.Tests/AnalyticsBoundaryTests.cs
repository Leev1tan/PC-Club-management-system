using Cms.Server.Analytics;
using Cms.Server.Billing;
using Cms.Server.Demo;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// Analytics at exact boundary instants (a sweep over 72 club times found them): at 00:00:00 club time the new club
/// day has no second yet, and at 09:00:00 / 21:00:00 the demo shift closes and the next one opens at "now".
/// </summary>
public sealed class AnalyticsBoundaryTests
{
    [Theory]
    [InlineData("00:00")]
    [InlineData("09:00")]
    [InlineData("21:00")]
    [InlineData("now")]
    public async Task The_series_has_every_day_and_the_shifts_add_up_at_a_boundary(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        using var testDb = new TestDb();
        await using (var seed = testDb.NewShiftContext()) await DemoSeeder.ResetAsync(seed, now, new Random(42));
        await using var db = testDb.NewContext();
        var clock = new ClubClock(new FixedTimeProvider(now), ClubTimeZone.Default);

        foreach (var (key, days) in new[] { ("30d", 30), ("7d", 7), ("today", 1) })
        {
            var period = AnalyticsPeriod.Resolve(key, null, null, clock)!;
            var report = await AnalyticsService.BuildAsync(db, period, clock);
            if (key != "today")
            {
                Assert.Equal(days, report.Series.Count);
                Assert.Equal(period.To.ToString("yyyy-MM-dd"), report.Series[^1].Label);
            }
            Assert.True(report.Check.Reconciled, $"{key} at {clubTime}: not reconciled");
            Assert.True(report.Check.ShiftsAddUp, $"{key} at {clubTime}: Z-reports {report.Check.ZReports} + outside {report.Check.OutsideShifts} != the period");
            // The shifts inside the period add up with their own Z-reports
            var inside = (await db.Shifts.AsNoTracking().ToListAsync())
                .Where(s => s.OpenedUtc >= period.FromUtc && (s.ClosedUtc ?? now) <= period.EndUtc).ToList();
            decimal zTime = 0;
            foreach (var shift in inside) zTime += (await ZReport.BuildAsync(db, shift, clock)).Time.Total;
            Assert.True(report.Check.ZReports.Time <= zTime, $"{key} at {clubTime}: the Z-report part is more than the Z-reports");
        }

        // Today at 00:00:00: the hourly series starts with the 00:00 hour (empty), not with nothing
        var today = await AnalyticsService.BuildAsync(db, AnalyticsPeriod.Resolve("today", null, null, clock)!, clock);
        Assert.NotEmpty(today.Series);
        Assert.Equal("00:00", today.Series[0].Label);
    }
}
