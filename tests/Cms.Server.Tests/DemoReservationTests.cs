using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Demo reservations: seeded for today and tomorrow at any club time; the simulator checks most in and some become no-shows.</summary>
public sealed class DemoReservationTests
{
    [Theory]
    [InlineData("09:01")]
    [InlineData("21:01")]
    [InlineData("00:01")]
    [InlineData("now")]
    public async Task The_demo_has_reservations_and_the_simulator_checks_in_or_lets_them_go(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        using var testDb = new TestDb();
        var runtime = new DeviceRuntimeState();
        DemoSeedResult seed;
        await using (var db = testDb.NewShiftContext()) seed = await DemoSeeder.ResetAsync(db, now, new Random(42));
        var clock = new ClubClock(new FixedTimeProvider(now), ClubTimeZone.Default);

        await using (var db = testDb.NewContext())
        {
            var all = await db.Reservations.AsNoTracking().ToListAsync();
            var today = ReservationService.DayOf(now, clock);
            Assert.True(all.Count(r => r.StartDay == today + 1) >= 6, $"{all.Count(r => r.StartDay == today + 1)} for tomorrow");
            Assert.True(all.Count(r => r.StartUtc < now.AddHours(5)) >= 4, $"{all.Count(r => r.StartUtc < now.AddHours(5))} in the next hours");
            Assert.All(all, r => Assert.Contains(r.Status, new[] { ReservationStatuses.Confirmed, ReservationStatuses.Requested }));
            Assert.Equal(2, all.Count(r => r.Status == ReservationStatuses.Requested && r.Source == "public" && r.Phone!.StartsWith("+447700900")));
            Assert.True((await ReservationSettings.LoadAsync(db)).PublicBooking);
            Assert.Contains(all, r => r.DepositStatus == DepositStatuses.Held && r.UserId != null);
            Assert.Contains(all, r => r.DeviceId == null && r.ZoneId != null);
            Assert.Contains(all, r => r.UserId == null && r.GuestName != null);
            Assert.True((await Holds.DepositsAsync(db)).Ok);
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.True((await AuditChain.VerifyAsync(db)).Ok);
            // No PC booked twice
            foreach (var g in all.Where(r => r.DeviceId != null).GroupBy(r => r.DeviceId))
                foreach (var (a, b) in g.OrderBy(r => r.StartUtc).Zip(g.OrderBy(r => r.StartUtc).Skip(1)))
                    Assert.True(a.EndUtc <= b.StartUtc);
        }

        // Four hours of the club: the simulator ticks every minute, the reservation job runs too
        var sim = new DemoSimulator(NullLogger<DemoSimulator>.Instance, runtime, new Random(7), clock, posRng: new Random(11), reservationRng: new Random(5));
        sim.Load(seed, now);
        for (var minute = 1; minute <= 240; minute++)
        {
            var at = now.AddMinutes(minute);
            await using var db = testDb.NewShiftContext();
            await sim.TickAsync(db, new EfDeviceRepository(db, runtime), at);
            db.ChangeTracker.Clear();
            await new ReservationService(db, new EfDeviceRepository(db, runtime), clock.At(at), new AuditLogger(db, new HttpContextAccessor())).RunJobsAsync();
        }

        await using (var db = testDb.NewContext())
        {
            var end = now.AddMinutes(240);
            var settings = await ReservationSettings.LoadAsync(db);
            var past = (await db.Reservations.AsNoTracking().ToListAsync()).Where(r => r.StartUtc.AddMinutes(settings.GraceMinutes) < end).ToList();
            Assert.True(past.Count >= 3, $"{past.Count} reservations started");
            Assert.All(past, r => Assert.Contains(r.Status, new[] { ReservationStatuses.CheckedIn, ReservationStatuses.NoShow }));
            Assert.Contains(past, r => r.Status == ReservationStatuses.CheckedIn);
            Assert.All(past.Where(r => r.Status == ReservationStatuses.CheckedIn), r => Assert.NotNull(r.SessionId));
            Assert.True((await Holds.DepositsAsync(db)).Ok);
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.True((await AuditChain.VerifyAsync(db)).Ok);
        }
    }
}
