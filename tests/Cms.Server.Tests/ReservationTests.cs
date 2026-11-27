using Cms.Server.Analytics;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Cms.Server.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

/// <summary>Reservations (spec area 7) with a fixed club clock (UTC+2): overlaps, zones, holds, no-shows, deposits in the ledger.</summary>
public sealed class ReservationTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = Clock(Local(0, 10)); // Monday 10:00
    private readonly Guid _std, _vip, _std1, _std2, _vip1;
    private const string Monday = "2026-11-23";

    public ReservationTests()
    {
        using var db = _testDb.NewContext();
        (_std, _vip) = (Guid.NewGuid(), Guid.NewGuid());
        db.Zones.AddRange(new ZoneEntity { Id = _std, Name = "Standard" }, new ZoneEntity { Id = _vip, Name = "VIP", SortOrder = 1 });
        var std = Standard(_std);
        std.Tariff.IsDefault = true;
        db.Tariffs.Add(std.Tariff);
        db.TariffRules.AddRange(std.Rules);
        db.Tariffs.Add(new TariffEntity { Id = Guid.NewGuid(), Name = "VIP", PricePerHour = 90m, ZoneId = _vip });
        DeviceEntity Pc(string name, Guid zone) => new() { Id = Guid.NewGuid(), Hostname = name, DeviceKey = "k-" + name, Status = "approved", ZoneId = zone };
        var pcs = new[] { Pc("STD-01", _std), Pc("STD-02", _std), Pc("VIP-01", _vip) };
        (_std1, _std2, _vip1) = (pcs[0].Id, pcs[1].Id, pcs[2].Id);
        db.Devices.AddRange(pcs);
        db.SaveChanges();
    }

    public void Dispose() => _testDb.Dispose();

    private void At(DateTimeOffset utc) => _clock.Time().UtcNow = utc;

    private ReservationService Service(CmsDbContext db) =>
        new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor()), new StaffActor(null, "cashier"));

    private SessionService Sessions(CmsDbContext db, bool manager = false) =>
        new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor())) { CanOverrideReservation = manager };

    private async Task<UserEntity> PlayerAsync(decimal balance, decimal bonus = 0m)
    {
        await using var db = _testDb.NewContext();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Тарас Петренко" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await Wallet.CreditAsync(db, user.Id, balance, bonus);
        await Ledger.AppendAsync(db, new LedgerEntryEntity
        {
            UserId = user.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = balance + bonus, BalanceDelta = balance, BonusDelta = bonus
        }, null, _clock.UtcNow);
        return user;
    }

    private async Task<BillingResult<ReservationEntity>> BookAsync(Guid? pc, Guid? zone, string time, int minutes, Guid? user = null, string? guest = "Ірина Коваль", decimal deposit = 0m)
    {
        await using var db = _testDb.NewContext();
        return await Service(db).CreateAsync(new CreateReservationRequest(pc, zone, Monday, time, minutes, user, user == null ? guest : null, deposit));
    }

    private async Task<ReservationEntity> GetAsync(long id)
    {
        await using var db = _testDb.NewContext();
        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<decimal> BalanceAsync(Guid user)
    {
        await using var db = _testDb.NewContext();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user)).Balance;
    }

    [Fact]
    public async Task A_pc_is_booked_once_and_a_zone_never_needs_more_pcs_than_it_has()
    {
        Assert.True((await BookAsync(_std1, null, "18:00", 120)).Ok);
        Assert.Equal("pc_already_reserved", (await BookAsync(_std1, null, "19:00", 60)).Error!.Code);
        Assert.Equal(new[] { "STD-01", "18:00", "20:00" }, (await BookAsync(_std1, null, "17:30", 45)).Error!.Args);
        Assert.True((await BookAsync(_std1, null, "20:00", 60)).Ok);              // right after: no overlap
        Assert.True((await BookAsync(null, _std, "18:30", 60)).Ok);               // any PC: STD-02
        Assert.Equal("zone_full", (await BookAsync(null, _std, "18:45", 60)).Error!.Code);
        Assert.True((await BookAsync(_std2, null, "18:00", 30)).Ok);              // STD-02 is free until 18:30
        Assert.Equal("zone_full", (await BookAsync(_std2, null, "19:00", 30)).Error!.Code); // the any-PC one needs STD-02 then
        Assert.True((await BookAsync(_vip1, null, "18:30", 60)).Ok);              // another zone

        // Rules of the request
        Assert.Equal("bad_reservation_duration", (await BookAsync(_vip1, null, "21:00", 20)).Error!.Code);
        Assert.Equal("bad_reservation_duration", (await BookAsync(_vip1, null, "21:00", 70)).Error!.Code);
        Assert.Equal("bad_reservation_time", (await BookAsync(_vip1, null, "09:00", 60)).Error!.Code);
        Assert.Equal("reservation_needs_guest", (await BookAsync(_vip1, null, "21:00", 60, guest: " ")).Error!.Code);
        Assert.Equal("reservation_needs_guest", (await BookAsync(_vip1, null, "21:00", 60, guest: new string('я', 61))).Error!.Code);
        Assert.Equal("deposit_needs_player", (await BookAsync(_vip1, null, "21:00", 60, deposit: 50m)).Error!.Code);
        Assert.Equal("reservation_needs_pc_or_zone", (await BookAsync(null, null, "21:00", 60)).Error!.Code);

        // Move: into a taken slot is refused, into a free one works
        var late = (await BookAsync(_vip1, null, "22:00", 60)).Value!;
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal("pc_already_reserved", (await Service(db).UpdateAsync(late.Id, new UpdateReservationRequest(_vip1, null, Monday, "19:00", 60))).Error!.Code);
            var moved = await Service(db).UpdateAsync(late.Id, new UpdateReservationRequest(_vip1, null, Monday, "20:00", 90, "біля вікна"));
            Assert.True(moved.Ok);
            Assert.Equal(Local(0, 20), moved.Value!.StartUtc);
        }

        // The audit texts never carry the walk-in name
        await using (var db = _testDb.NewContext())
        {
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("reservation.")).ToListAsync();
            Assert.True(audit.Count >= 6);
            Assert.DoesNotContain(audit, a => (a.Summary + a.DetailsJson).Contains("Ірина") || (a.Summary + a.DetailsJson).Contains("Коваль"));
            Assert.True((await AuditChain.VerifyAsync(db)).Ok);
        }
    }

    [Fact]
    public async Task A_held_pc_takes_only_its_check_in_or_a_manager_override()
    {
        var player = await PlayerAsync(300m);
        var r = (await BookAsync(_std1, null, "12:00", 120, player.Id)).Value!;
        var walkIn = (await BookAsync(_std2, null, "12:00", 60)).Value!;
        await using var db = _testDb.NewContext();
        At(Local(0, 11, 44));
        Assert.Null(await ReservationHolds.ActiveAsync(db, _std1, _clock));
        Assert.True((await Sessions(db).StartAsync(new StartSessionRequest(_std1, null, null, 30, PaymentMethod: "cash"))).Ok); // before the hold
        db.ChangeTracker.Clear();

        At(Local(0, 11, 50));
        var hold = await ReservationHolds.ActiveAsync(db, _std2, _clock);
        Assert.Equal(("Ірина", "12:00"), (hold!.Name, hold.StartLocal)); // the first name only, club time
        var refused = await Sessions(db).StartAsync(new StartSessionRequest(_std2, null, null, 60, PaymentMethod: "cash"));
        Assert.Equal("pc_reserved", refused.Error!.Code);
        Assert.Equal("Ірина", refused.Error.Args[0]);
        Assert.Equal("override_needs_manager", (await Sessions(db).StartAsync(new StartSessionRequest(_std2, null, null, 60, PaymentMethod: "cash", OverrideReservation: true))).Error!.Code);
        var overridden = await Sessions(db, manager: true).StartAsync(new StartSessionRequest(_std2, null, null, 60, PaymentMethod: "cash", OverrideReservation: true));
        Assert.True(overridden.Ok);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.Action == AuditActions.ReservationHoldOverridden);
        Assert.Contains($"#{walkIn.Id}", audit.Summary);
        Assert.DoesNotContain("Ірина", audit.Summary + audit.DetailsJson);

        // The reserved player's own PC: the nickname on the hold; a start without the check-in is refused too
        Assert.Equal(player.Username, (await ReservationHolds.ActiveAsync(db, _std1, _clock))!.Name);
    }

    [Fact]
    public async Task No_shows_cancel_returns_and_check_ins_keep_the_ledger_analytics_and_z_report_reconciled()
    {
        var player = await PlayerAsync(300m, bonus: 0m);
        await using (var db = _testDb.NewShiftContext())
            Assert.True((await new ShiftService(db, _clock, actor: new StaffActor(null, "cashier")).OpenAsync(0m, null)).Ok);

        // Not enough real balance: nothing is saved
        Assert.Equal("insufficient_balance_deposit", (await BookAsync(_std1, null, "12:00", 120, player.Id, deposit: 500m)).Error!.Code);
        await using (var db = _testDb.NewContext()) Assert.Empty(await db.Reservations.ToListAsync());

        var noShow = (await BookAsync(_std1, null, "12:00", 120, player.Id, deposit: 100m)).Value!;
        var cancelled = (await BookAsync(_std2, null, "12:00", 60, player.Id, deposit: 50m)).Value!;
        var checkedIn = (await BookAsync(_vip1, null, "14:00", 60, player.Id, deposit: 80m)).Value!;
        Assert.Equal(300m - 230m, await BalanceAsync(player.Id));

        await using (var db = _testDb.NewContext())
        {
            Assert.True((await Service(db).CancelAsync(cancelled.Id, "передумав")).Ok);
            Assert.Equal("reservation_not_open", (await Service(db).CancelAsync(cancelled.Id, null)).Error!.Code);
        }
        Assert.Equal(DepositStatuses.Returned, (await GetAsync(cancelled.Id)).DepositStatus);
        Assert.Equal(120m, await BalanceAsync(player.Id));

        // No check-in 15 minutes after the start: no-show, the deposit is kept (club setting, default)
        At(Local(0, 12, 14));
        await using (var db = _testDb.NewContext()) Assert.Equal(0, (await Service(db).RunJobsAsync()).NoShows);
        At(Local(0, 12, 15));
        await using (var db = _testDb.NewContext()) Assert.Equal(1, (await Service(db).RunJobsAsync()).NoShows);
        var gone = await GetAsync(noShow.Id);
        Assert.Equal((ReservationStatuses.NoShow, DepositStatuses.Kept), (gone.Status, gone.DepositStatus));
        Assert.Equal(120m, await BalanceAsync(player.Id));

        // Check-in: not before the hold, then the deposit pays the session (VIP 90 ₴/h)
        At(Local(0, 13, 40));
        await using (var db = _testDb.NewContext())
            Assert.Equal("reservation_not_yet", (await Service(db).CheckInAsync(checkedIn.Id, new CheckInRequest())).Error!.Code);
        At(Local(0, 13, 50));
        await using (var db = _testDb.NewContext())
        {
            var result = await Service(db).CheckInAsync(checkedIn.Id, new CheckInRequest(Minutes: 60));
            Assert.True(result.Ok, result.Error?.Message);
            Assert.Equal(_vip1, result.Value.Session.DeviceId);
            Assert.Equal(90m, result.Value.Session.TotalCost);
            Assert.Equal("reservation_not_open", (await Service(db).CheckInAsync(checkedIn.Id, new CheckInRequest())).Error!.Code);
        }
        Assert.Equal((ReservationStatuses.CheckedIn, DepositStatuses.Applied), ((await GetAsync(checkedIn.Id)).Status, (await GetAsync(checkedIn.Id)).DepositStatus));
        Assert.Equal(120m + 80m - 90m, await BalanceAsync(player.Id));

        // A no-show with the setting "return": the deposit goes back
        await using (var db = _testDb.NewContext())
            await ReservationSettings.SaveAsync(db, ReservationSettings.Default with { NoShowKeepsDeposit = false }, "manager", _clock.UtcNow);
        var returned = (await BookAsync(_std1, null, "16:00", 60, player.Id, deposit: 30m)).Value!;
        var open = (await BookAsync(_std2, null, "18:00", 60, player.Id, deposit: 20m)).Value!;
        At(Local(0, 16, 20));
        await using (var db = _testDb.NewContext()) await Service(db).RunJobsAsync();
        Assert.Equal(DepositStatuses.Returned, (await GetAsync(returned.Id)).DepositStatus);
        Assert.Equal(110m - 20m, await BalanceAsync(player.Id));

        await using (var db = _testDb.NewContext())
        {
            // The ledger explains every balance; every deposit is held, released or kept exactly once
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.Null(await Ledger.CheckChainAsync(db, player.Id));
            var holds = await Holds.DepositsAsync(db);
            Assert.True(holds.Ok, string.Join(",", holds.Broken));
            Assert.Equal((20m, 1), (holds.Held, holds.Open));
            Assert.Equal(open.Id, (await db.Reservations.AsNoTracking().SingleAsync(r => r.DepositStatus == DepositStatuses.Held)).Id);

            var rows = await db.Ledger.AsNoTracking().ToListAsync();
            var totals = LedgerTotals.Of(rows);
            Assert.Equal((280m, 160m, 100m), (totals.DepositsHeld, totals.DepositsReleased, totals.DepositsKept));
            Assert.Equal(100m + 90m, totals.TimeTotal);           // the kept deposit is PC time revenue
            Assert.Equal(totals.RevenueTotal, totals.Revenue.Total);
            Assert.True(totals.Reconciled);

            var shift = await db.Shifts.AsNoTracking().SingleAsync();
            var z = await ZReport.BuildAsync(db, shift, _clock);
            Assert.True(z.Ledger.Reconciled);
            Assert.Equal((280m, 160m, 100m), (z.Holds.DepositsHeld, z.Holds.DepositsReleased, z.Holds.DepositsKept));
            Assert.Equal(190m, z.Time.Total);
            Assert.Equal(190m, z.Time.ByMethod.Balance);

            var report = await AnalyticsService.BuildAsync(db, AnalyticsPeriod.Resolve("today", null, null, _clock)!, _clock);
            Assert.Equal(100m, report.Holds.DepositsKept);
            Assert.Equal(190m, report.Revenue.Time);
            Assert.Equal(90m, report.AverageTicket.Session);        // the kept deposit is no session
            Assert.True(report.Check.Reconciled && report.Check.ShiftsAddUp);
            Assert.Contains(await db.AuditLog.AsNoTracking().Select(a => a.Summary).ToListAsync(),
                s => s!.StartsWith($"Reservation #{noShow.Id} cancelled automatically") && s.Contains("deposit 100") && s.EndsWith("kept"));
        }
    }

    [Fact]
    public async Task Names_and_phones_are_erased_some_days_after_the_slot()
    {
        long request, walkIn, player;
        await using (var db = _testDb.NewContext())
        {
            var service = Service(db);
            request = (await service.RequestAsync(_vip, Local(0, 18), 60, "Леся Гордієнко", "+447700900456", 2)).Value!.Id;
            walkIn = (await BookAsync(_std1, null, "18:00", 60, guest: "Остап Вишняк")).Value!.Id;
            player = (await BookAsync(_std2, null, "18:00", 60, (await PlayerAsync(100m)).Id)).Value!.Id;
            Assert.True((await service.DeclineAsync(request, "немає місць")).Ok);
        }
        // Not yet: 29 days after the slot; the walk-in becomes a no-show on the way
        At(Local(29, 19));
        await using (var db = _testDb.NewContext()) Assert.Equal(0, (await Service(db).RunJobsAsync()).Erased);
        Assert.Equal("Остап Вишняк", (await GetAsync(walkIn)).GuestName);
        At(Local(30, 19));
        await using (var db = _testDb.NewContext()) Assert.Equal(2, (await Service(db).RunJobsAsync()).Erased);
        foreach (var id in new[] { request, walkIn })
        {
            var r = await GetAsync(id);
            Assert.Equal((null, null), (r.GuestName, r.Phone));
            Assert.NotNull(r.ErasedUtc);
        }
        Assert.Null((await GetAsync(player)).ErasedUtc); // a player reservation has no name or phone to erase
        await using (var db = _testDb.NewContext())
        {
            var row = await db.AuditLog.AsNoTracking().SingleAsync(a => a.Action == AuditActions.ReservationDataErased);
            Assert.Contains($"{request}", row.DetailsJson);
            Assert.DoesNotContain("Остап", row.Summary + row.DetailsJson);
            Assert.DoesNotContain("7700", row.Summary + row.DetailsJson);
            Assert.DoesNotContain(await db.AuditLog.AsNoTracking().ToListAsync(), a => (a.Summary + a.DetailsJson).Contains("Леся") || (a.Summary + a.DetailsJson).Contains("Остап"));
            Assert.Equal(0, (await Service(db).RunJobsAsync()).Erased);
        }
    }

    [Fact]
    public async Task Any_pc_reservations_get_a_pc_at_the_hold_and_requests_expire()
    {
        var zoneWide = (await BookAsync(null, _std, "12:00", 60)).Value!;
        long request;
        await using (var db = _testDb.NewContext())
        {
            var r = new ReservationEntity
            {
                Status = ReservationStatuses.Requested, Source = "public", ZoneId = _vip, StartUtc = Local(0, 13), EndUtc = Local(0, 14), DurationMinutes = 60,
                StartDay = ReservationService.DayOf(Local(0, 13), _clock), GuestName = "Олег", Phone = "+447700900123", CreatedUtc = _clock.UtcNow, UpdatedUtc = _clock.UtcNow
            };
            db.Reservations.Add(r);
            await db.SaveChangesAsync();
            request = r.Id;
        }
        // A request holds nothing
        Assert.True((await BookAsync(_vip1, null, "13:00", 60)).Ok);

        At(Local(0, 11, 46));
        await using (var db = _testDb.NewContext()) Assert.Equal(1, (await Service(db).RunJobsAsync()).Assigned);
        var assigned = (await GetAsync(zoneWide.Id)).AssignedDeviceId;
        Assert.NotNull(assigned);
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(zoneWide.Id, (await ReservationHolds.ActiveAsync(db, assigned!.Value, _clock))!.Id);
            Assert.Equal("zone_full", (await Service(db).ConfirmAsync(request, new ConfirmReservationRequest())).Error!.Code);
            Assert.Equal("reservation_other_zone", (await Service(db).ConfirmAsync(request, new ConfirmReservationRequest(_std1))).Error!.Code);
        }

        At(Local(0, 13));
        await using (var db = _testDb.NewContext()) Assert.Equal(1, (await Service(db).RunJobsAsync()).Expired);
        Assert.Equal(ReservationStatuses.Expired, (await GetAsync(request)).Status);
        // The any-PC reservation became a no-show at 12:15
        Assert.Equal(ReservationStatuses.NoShow, (await GetAsync(zoneWide.Id)).Status);
    }
}
