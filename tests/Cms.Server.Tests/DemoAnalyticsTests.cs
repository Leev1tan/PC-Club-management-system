using System.Diagnostics;
using Cms.Server.Analytics;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Server.Pos;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Cms.Server.Tests;

/// <summary>
/// Demo mode, phase 5: 30 days of history, every closed shift anchored in the audit chain like a real
/// close, analytics that reconcile with the ledger and the Z-reports, and a fast reset (SQLite and PostgreSQL).
/// </summary>
public sealed class DemoAnalyticsTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TestDb _testDb = new();

    public DemoAnalyticsTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _testDb.Dispose();

    private static async Task<TimeSpan> ResetAsync(Func<CmsDbContext> context, DateTimeOffset now, int seed)
    {
        await using var db = context();
        var watch = Stopwatch.StartNew();
        await DemoSeeder.ResetAsync(db, now, new Random(seed));
        return watch.Elapsed;
    }

    private async Task CheckSeedAsync(Func<CmsDbContext> context, DateTimeOffset now)
    {
        await using var db = context();
        var clock = new ClubClock(new FixedTimeProvider(now), ClubTimeZone.Default);

        // Shifts: back to back for the whole history, every closed one anchored, the audit chain intact
        var shifts = await db.Shifts.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        var closed = shifts.Where(s => s.Status == ShiftStatuses.Closed).ToList();
        Assert.True(closed.Count >= 2 * DemoSeeder.HistoryDays, $"{closed.Count} closed shifts");
        Assert.All(closed, s => Assert.NotNull(s.AuditHeadHash));
        var verify = await AuditChain.VerifyAsync(db);
        Assert.True(verify.Ok, $"{verify.Problem} at {verify.FirstBrokenId}");
        Assert.Equal(closed.Count, verify.AnchorsChecked);
        var audit = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(shifts.Count, audit.Count(a => a.Action == AuditActions.ShiftOpened));
        foreach (var shift in closed)
        {
            // Like a real close: the anchor is the row right before the "shift.closed" row of the shift
            var row = audit.Single(a => a.Action == AuditActions.ShiftClosed && a.TargetId == shift.Id.ToString());
            Assert.Equal(row.Id - 1, shift.AuditHeadId);
            Assert.Equal(row.PrevHash, shift.AuditHeadHash);
            Assert.Equal(shift.ExpectedCash, await ShiftService.ExpectedCashAsync(db, shift));
            Assert.Equal(shift.CountedCash - shift.ExpectedCash, shift.Variance);
        }
        Assert.Equal(-30m, closed.Sum(s => s.Variance));

        // Money and stock reconcile; ids follow the dates
        Assert.Empty(await Ledger.ReconcileAsync(db));
        Assert.Empty(await Stock.ReconcileAsync(db));
        var ledger = await db.Ledger.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        Assert.True(ledger.Zip(ledger.Skip(1)).All(p => p.First.CreatedUtc <= p.Second.CreatedUtc));
        Assert.DoesNotContain(ledger, l => l.ShiftId == null && PaymentMethods.IsDesk(l.Method));

        // Sessions: no PC and no player has two at once
        var sessions = await db.Sessions.AsNoTracking().ToListAsync();
        Assert.True(sessions.Count >= 50 * DemoSeeder.HistoryDays, $"{sessions.Count} sessions");
        foreach (var group in sessions.GroupBy(s => s.DeviceId).Concat(sessions.Where(s => s.UserId != null).GroupBy(s => s.UserId!.Value)))
        {
            var list = group.OrderBy(s => s.StartUtc).ToList();
            foreach (var (a, b) in list.Zip(list.Skip(1)))
                Assert.True((a.EndUtc ?? now) <= b.StartUtc, $"overlap {a.Id} / {b.Id}");
        }

        // Analytics of 30 days: reconciled with the ledger and the Z-reports, a club-like day
        var report = await AnalyticsService.BuildAsync(db, AnalyticsPeriod.Resolve("30d", null, null, clock)!, clock);
        Assert.True(report.Check.Reconciled);
        Assert.True(report.Check.ShiftsAddUp);
        Assert.True(report.Check.ShiftsInside >= 2 * DemoSeeder.HistoryDays - 4, $"{report.Check.ShiftsInside} shifts inside");
        Assert.Equal(30, report.Series.Count);
        // Every day has revenue; in its first hour the current club day can still be empty
        var days = clock.ToLocal(now).TimeOfDay < TimeSpan.FromHours(1) ? report.Series.Take(report.Series.Count - 1) : report.Series;
        Assert.All(days, p => Assert.True(p.Total > 0, p.Label));
        Assert.True(report.Revenue.Time > report.Revenue.Bar && report.Revenue.Bar > 0);
        Assert.True(report.Revenue.ByMethod.Cash > 0 && report.Revenue.ByMethod.Card > 0 && report.Revenue.ByMethod.Balance > 0 && report.Revenue.ByMethod.Bonus > 0);
        Assert.True(report.Bonus.Issued > 0 && report.Bonus.Spent > 0);
        Assert.Equal(1, report.Refunds.BarReceipts);
        Assert.InRange(report.Occupancy.Percent!.Value, 25m, 75m);
        Assert.Equal(3, report.Occupancy.Zones.Count);
        var evening = report.Heatmap.Percent.Select(day => day[20]!.Value).Average();
        var morning = report.Heatmap.Percent.Select(day => day[7]!.Value).Average();
        Assert.True(evening > 70m && morning < 30m, $"20:00 {evening:0}%, 07:00 {morning:0}%");
        _output.WriteLine($"30 days: {sessions.Count} sessions, {ledger.Count} ledger rows, {audit.Count} audit rows, {closed.Count} closed shifts; " +
                          $"revenue {report.Revenue.Total} (time {report.Revenue.Time}, bar {report.Revenue.Bar}), occupancy {report.Occupancy.Percent}% " +
                          $"(20:00 {evening:0.0}%, 07:00 {morning:0.0}%)");
    }

    [Theory]
    [InlineData("now")]
    [InlineData("09:01")] // a minute after the day shift opened
    [InlineData("21:01")] // a minute after the night shift opened
    [InlineData("00:01")] // a minute into the club day
    [InlineData("01:00")] // an hour into the club day
    [InlineData("14:00")]
    public async Task Seed_has_30_days_anchored_shifts_and_analytics_that_reconcile_on_sqlite(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        var first = await ResetAsync(_testDb.NewShiftContext, now, 42);
        var second = await ResetAsync(_testDb.NewShiftContext, now, 43);
        _output.WriteLine($"SQLite reset: {first.TotalMilliseconds:0} ms, again {second.TotalMilliseconds:0} ms");
        await CheckSeedAsync(_testDb.NewShiftContext, now);
        Assert.True(second < TimeSpan.FromSeconds(10), $"reset took {second}");
    }

    [PostgresFact]
    public async Task Seed_has_30_days_anchored_shifts_and_analytics_that_reconcile_on_postgres()
    {
        using var pg = new PostgresDb();
        CmsDbContext Context() => pg.NewContext();
        var now = DateTimeOffset.UtcNow;
        var first = await ResetAsync(Context, now, 42);
        var second = await ResetAsync(Context, now, 43); // after a reset: TRUNCATE, explicit ids, sequences set again
        _output.WriteLine($"PostgreSQL reset: {first.TotalMilliseconds:0} ms, again {second.TotalMilliseconds:0} ms");
        await CheckSeedAsync(Context, now);
        Assert.True(second < TimeSpan.FromSeconds(10), $"reset took {second}");

        // The ids generated after the seed follow the seeded ids (the simulator writes audit rows and shifts)
        await using var db = Context();
        var open = await ShiftService.OpenShiftAsync(db);
        var actor = new StaffActor(open!.OpenedById, open.OpenedByUsername);
        Assert.True((await new ShiftService(db, ClubClock.System(), actor: actor).CloseAsync(open.Id, 1000m, "test", isManager: false)).Ok);
        Assert.True((await new ShiftService(db, ClubClock.System(), actor: actor).OpenAsync(500m, null)).Ok);
        await AuditChain.AppendAsync(db, new AuditLogEntity { Action = "test.after_seed", Summary = "after the seed" });
        var verify = await AuditChain.VerifyAsync(db);
        Assert.True(verify.Ok, $"{verify.Problem} at {verify.FirstBrokenId}");
    }
}
