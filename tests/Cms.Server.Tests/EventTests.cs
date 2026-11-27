using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Devices;
using Cms.Server.Events;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Cms.Server.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

/// <summary>Events and tournaments (spec area 9) with a fixed club clock (UTC+2): booking of PCs, teams, fees, start, end, bracket.</summary>
public sealed class EventTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = Clock(Local(0, 10)); // Monday 10:00
    private readonly Guid _std, _vip;
    private readonly Guid[] _stdPcs, _vipPcs;
    private const string Monday = "2026-11-23";
    private const string EventName = "Кубок Ромашки 2×2";

    public EventTests()
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
        var stdPcs = Enumerable.Range(1, 4).Select(i => Pc($"STD-0{i}", _std)).ToArray();
        var vipPcs = Enumerable.Range(1, 2).Select(i => Pc($"VIP-0{i}", _vip)).ToArray();
        db.Devices.AddRange(stdPcs);
        db.Devices.AddRange(vipPcs);
        db.SaveChanges();
        _stdPcs = stdPcs.Select(p => p.Id).ToArray();
        _vipPcs = vipPcs.Select(p => p.Id).ToArray();
    }

    public void Dispose() => _testDb.Dispose();

    private void At(DateTimeOffset utc) => _clock.Time().UtcNow = utc;

    private EventService Events(CmsDbContext db, string actor = "manager") =>
        new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor()), new StaffActor(null, actor));

    private ReservationService Reservations(CmsDbContext db) =>
        new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor()), new StaffActor(null, "cashier"));

    private SessionService Sessions(CmsDbContext db, bool manager = false) =>
        new(db, new EfDeviceRepository(db), _clock, new AuditLogger(db, new HttpContextAccessor())) { CanOverrideReservation = manager };

    private async Task<UserEntity> PlayerAsync(decimal balance, decimal bonus = 0m)
    {
        await using var db = _testDb.NewContext();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Тарас Петренко" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        if (balance + bonus > 0)
        {
            await Wallet.CreditAsync(db, user.Id, balance, bonus);
            await Ledger.AppendAsync(db, new LedgerEntryEntity
            {
                UserId = user.Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = balance + bonus, BalanceDelta = balance, BonusDelta = bonus
            }, null, _clock.UtcNow);
        }
        return user;
    }

    private async Task<BillingResult<EventEntity>> CreateAsync(string time = "18:00", int minutes = 180, Guid? zone = null, Guid[]? pcs = null,
        string mode = EventPriceModes.Free, decimal price = 0m, decimal fee = 0m, int teamSize = 1, string name = EventName)
    {
        await using var db = _testDb.NewContext();
        return await Events(db).CreateAsync(new CreateEventRequest(name, pcs == null ? zone ?? _std : null, pcs, Monday, time, minutes, mode, price, fee, teamSize));
    }

    private async Task<BillingResult<EventTeamEntity>> RegisterAsync(long id, string? name, params UserEntity[] players)
    {
        await using var db = _testDb.NewContext();
        return await Events(db, "cashier").RegisterAsync(id, new RegisterTeamRequest(name, players.Select(p => p.Id).ToList()));
    }

    private async Task<decimal> BalanceAsync(Guid user)
    {
        await using var db = _testDb.NewContext();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user)).Balance;
    }

    private async Task AssertMoneyAsync()
    {
        await using var db = _testDb.NewContext();
        Assert.Empty(await Ledger.ReconcileAsync(db));
        var holds = await Holds.CheckAsync(db);
        Assert.True(holds.EventFees.Ok, $"broken events: {string.Join(",", holds.EventFees.Broken)}");
        Assert.True(holds.Deposits.Ok);
        Assert.True(LedgerTotals.Of(await db.Ledger.AsNoTracking().ToListAsync()).Reconciled);
    }

    private async Task<List<AuditLogEntity>> AuditAsync(string prefix)
    {
        await using var db = _testDb.NewContext();
        return await db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith(prefix)).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task An_event_books_its_pcs_against_reservations_and_other_events()
    {
        var created = await CreateAsync("18:00", 180);
        Assert.True(created.Ok, created.Error?.Message);
        var e = created.Value!;
        Assert.Equal(Local(0, 18), e.StartUtc);
        Assert.Equal(Local(0, 21), e.EndUtc);
        Assert.Equal(_std, e.ZoneId);

        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(4, await db.EventDevices.CountAsync(d => d.EventId == e.Id));
            var reservations = Reservations(db);
            // A reservation on an event PC is refused; before and after the event it works
            var clash = await reservations.CreateAsync(new CreateReservationRequest(_stdPcs[0], null, Monday, "20:00", 60, GuestName: "Ірина"));
            Assert.Equal("pc_event_booked", clash.Error!.Code);
            Assert.Equal(new[] { "STD-01", "18:00", "21:00" }, clash.Error.Args);
            Assert.True((await reservations.CreateAsync(new CreateReservationRequest(_stdPcs[0], null, Monday, "16:00", 120, GuestName: "Ірина"))).Ok);
            Assert.True((await reservations.CreateAsync(new CreateReservationRequest(_stdPcs[0], null, Monday, "21:00", 60, GuestName: "Ірина"))).Ok);
            // Any PC of the zone: all four PCs belong to the event
            Assert.Equal("zone_full", (await reservations.CreateAsync(new CreateReservationRequest(null, _std, Monday, "19:00", 60, GuestName: "Ірина"))).Error!.Code);
            // The public page sees no free PC of the zone then
            var free = await reservations.FreeCapacityAsync(new[] { _std }, new[] { (Local(0, 19), Local(0, 20)) });
            Assert.Equal(0, free[_std][0]);
        }

        // Another event on the same PCs overlaps; on other PCs or another time it works
        Assert.Equal("pc_event_booked", (await CreateAsync("20:30", 60, pcs: new[] { _stdPcs[1] })).Error!.Code);
        Assert.Equal("pc_already_reserved", (await CreateAsync("16:30", 60, pcs: new[] { _stdPcs[0] })).Error!.Code);
        Assert.True((await CreateAsync("19:00", 60, zone: _vip)).Ok);

        // An event leaves the any-PC reservations of a zone enough PCs
        await using (var db = _testDb.NewContext())
            Assert.True((await Reservations(db).CreateAsync(new CreateReservationRequest(null, _vip, Monday, "12:00", 60, GuestName: "Ірина"))).Ok);
        Assert.Equal("event_zone_reserved", (await CreateAsync("12:00", 60, pcs: _vipPcs)).Error!.Code);
        Assert.True((await CreateAsync("12:00", 60, pcs: new[] { _vipPcs[0] })).Ok);

        // Rules of the request
        Assert.Equal("event_bad_name", (await CreateAsync("10:30", 60, zone: _vip, name: " ")).Error!.Code);
        Assert.Equal("event_bad_name", (await CreateAsync("10:30", 60, zone: _vip, name: new string('я', 81))).Error!.Code);
        Assert.Equal("event_bad_duration", (await CreateAsync("10:30", 20, zone: _vip)).Error!.Code);
        Assert.Equal("event_bad_duration", (await CreateAsync("10:30", 70, zone: _vip)).Error!.Code);
        Assert.Equal("event_bad_time", (await CreateAsync("09:00", 60, zone: _vip)).Error!.Code);
        Assert.Equal("event_bad_price", (await CreateAsync("10:30", 60, zone: _vip, mode: EventPriceModes.Fixed, price: 0m)).Error!.Code);
        Assert.Equal("event_bad_price", (await CreateAsync("10:30", 60, zone: _vip, mode: "cheap")).Error!.Code);
        Assert.Equal("event_bad_fee", (await CreateAsync("10:30", 60, zone: _vip, fee: -1m)).Error!.Code);
        Assert.Equal("event_needs_pcs", (await CreateAsync("10:30", 60, zone: _vip, teamSize: 3)).Error!.Code);
        Assert.Equal("event_bad_team_size", (await CreateAsync("10:30", 60, zone: _vip, teamSize: 11)).Error!.Code);

        // Move: into a booked time is refused, into a free one works
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal("pc_already_reserved", (await Events(db).UpdateAsync(e.Id, new UpdateEventRequest(EventName, Monday, "15:00", 240))).Error!.Code);
            var moved = await Events(db).UpdateAsync(e.Id, new UpdateEventRequest("CS2 Cup", Monday, "18:15", 165));
            Assert.True(moved.Ok, moved.Error?.Message);
            Assert.Equal(Local(0, 21), moved.Value!.EndUtc);
        }

        // The audit log has ids, PCs and times, never the name of the event
        var audit = await AuditAsync("event.");
        Assert.True(audit.Count >= 4);
        Assert.DoesNotContain(audit, a => (a.Summary + a.DetailsJson).Contains("Ромашки") || (a.Summary + a.DetailsJson).Contains("CS2 Cup"));
    }

    [Fact]
    public async Task Teams_register_with_an_entry_fee_from_the_real_balance_and_get_it_back_when_they_withdraw()
    {
        var e = (await CreateAsync(fee: 50m, teamSize: 2)).Value!;
        var (a, b, c, d) = (await PlayerAsync(80m, bonus: 500m), await PlayerAsync(60m), await PlayerAsync(100m), await PlayerAsync(30m, bonus: 100m));
        var (x, y) = (await PlayerAsync(200m), await PlayerAsync(200m));

        // The bonus never pays the fee: d has 30 of real balance; nothing changes for the team
        var poor = await RegisterAsync(e.Id, "Тигри", c, d);
        Assert.Equal("insufficient_balance_fee", poor.Error!.Code);
        Assert.Equal(d.Username, poor.Error.Args[0]);
        Assert.Equal(50m, decimal.Parse(poor.Error.Args[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(30m, decimal.Parse(poor.Error.Args[2], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(100m, await BalanceAsync(c.Id));

        var team1 = await RegisterAsync(e.Id, "Тигри", a, b);
        Assert.True(team1.Ok, team1.Error?.Message);
        Assert.Equal(30m, await BalanceAsync(a.Id));
        Assert.Equal(10m, await BalanceAsync(b.Id));
        Assert.Equal("event_player_registered", (await RegisterAsync(e.Id, "Леви", a, c)).Error!.Code);
        Assert.Equal("event_bad_team", (await RegisterAsync(e.Id, "Леви", c)).Error!.Code);
        Assert.Equal("event_bad_team", (await RegisterAsync(e.Id, " ", c, x)).Error!.Code);
        var team2 = await RegisterAsync(e.Id, "Леви", c, x);
        Assert.True(team2.Ok);
        Assert.Equal("event_full", (await RegisterAsync(e.Id, "Вовки", y, d)).Error!.Code); // 4 PCs = 2 teams of 2

        await using (var db = _testDb.NewContext())
        {
            // Neighbours by PC name: team 1 on STD-01, STD-02; team 2 on STD-03, STD-04
            var seats = await db.EventPlayers.AsNoTracking().Where(p => p.EventId == e.Id).OrderBy(p => p.Id).Select(p => p.DeviceId).ToListAsync();
            Assert.Equal(_stdPcs.Cast<Guid?>(), seats);
            var holds = await Holds.EventFeesAsync(db);
            Assert.Equal(200m, holds.Held);
            Assert.Equal(4, holds.Open);
        }
        await AssertMoneyAsync();

        // Withdraw before the start: the fees go back, the PCs are free for the next team
        await using (var db = _testDb.NewContext())
            Assert.True((await Events(db, "cashier").WithdrawAsync(e.Id, team1.Value!.Id)).Ok);
        Assert.Equal(80m, await BalanceAsync(a.Id));
        Assert.Equal(60m, await BalanceAsync(b.Id));
        Assert.True((await RegisterAsync(e.Id, "Вовки", y, a)).Ok);
        await AssertMoneyAsync();

        // Cancel: every held fee goes back to the balances
        await using (var db = _testDb.NewContext())
        {
            var cancelled = await Events(db).CancelAsync(e.Id, "мало команд");
            Assert.True(cancelled.Ok);
            Assert.Equal(EventStatuses.Cancelled, cancelled.Value!.Status);
            Assert.Equal("event_not_open", (await Events(db).RegisterAsync(e.Id, new RegisterTeamRequest("Ще", new[] { b.Id, d.Id }))).Error!.Code);
            Assert.Empty(await db.EventPlayers.AsNoTracking().Where(p => p.FeeStatus == EventFeeStatuses.Held).ToListAsync());
            var totals = LedgerTotals.Of(await db.Ledger.AsNoTracking().ToListAsync());
            Assert.Equal(300m, totals.EventFeesHeld);
            Assert.Equal(300m, totals.EventFeesReleased);
            Assert.Equal(0m, totals.EventFeesKept);
            Assert.Equal(0m, totals.TimeTotal);
        }
        foreach (var p in new[] { a, b, c, x, y }) Assert.Equal(p == a ? 80m : p == b ? 60m : p == c ? 100m : 200m, await BalanceAsync(p.Id));
        await AssertMoneyAsync();

        var audit = await AuditAsync("event.");
        Assert.Contains(audit, r => r.Action == AuditActions.EventTeamRegistered && r.Summary!.Contains($"@{a.Username}") && r.Summary.Contains("STD-01"));
        Assert.Contains(audit, r => r.Action == AuditActions.EventCancelled && r.Summary!.Contains("returned"));
        Assert.DoesNotContain(audit, r => (r.Summary + r.DetailsJson).Contains("Тигри") || (r.Summary + r.DetailsJson).Contains("Вовки"));
    }

    [Fact]
    public async Task At_the_start_the_pcs_of_the_players_unlock_together_and_at_the_end_they_lock()
    {
        var e = (await CreateAsync("18:00", 120, mode: EventPriceModes.Fixed, price: 40m, fee: 25m)).Value!;
        var players = new[] { await PlayerAsync(100m, bonus: 15m), await PlayerAsync(100m), await PlayerAsync(30m) };
        foreach (var p in players) Assert.True((await RegisterAsync(e.Id, null, p)).Ok);
        Assert.Equal(5m, await BalanceAsync(players[2].Id)); // 30 - 25 fee: not enough for the PC time later

        // The hold: 15 min before the start a walk-in session is refused (a manager may override), the lock screen knows the event
        At(Local(0, 17, 50));
        await using (var db = _testDb.NewContext())
        {
            var hold = await EventHolds.ActiveAsync(db, _stdPcs[3], _clock);
            Assert.NotNull(hold);
            Assert.Equal(EventName, hold!.Name);
            Assert.Equal("18:00", hold.StartLocal);
            Assert.False(hold.Running);
            var walkIn = await Sessions(db).StartAsync(new StartSessionRequest(_stdPcs[3], null, null, 60, PaymentMethod: PaymentMethods.Cash));
            Assert.Equal("pc_event", walkIn.Error!.Code);
            Assert.Equal(EventName, walkIn.Error.Args[0]);
            Assert.Equal("override_needs_manager", (await Sessions(db).StartAsync(new StartSessionRequest(_stdPcs[3], null, null, 5, PaymentMethod: PaymentMethods.Cash, OverrideReservation: true))).Error!.Code);
            var overridden = await Sessions(db, manager: true).StartAsync(new StartSessionRequest(_stdPcs[3], null, null, 5, PaymentMethod: PaymentMethods.Cash, OverrideReservation: true));
            Assert.True(overridden.Ok, overridden.Error?.Message);
        }
        Assert.Contains(await AuditAsync("event.hold"), a => a.Summary!.Contains("STD-04"));

        // The job starts the event at its start time
        At(Local(0, 18, 0, 5));
        await using (var db = _testDb.NewContext())
        {
            var jobs = await Events(db).RunJobsAsync();
            Assert.Equal(1, jobs.Started);
        }
        await using (var db = _testDb.NewContext())
        {
            var rows = await db.EventPlayers.AsNoTracking().Where(p => p.EventId == e.Id).OrderBy(p => p.Id).ToListAsync();
            Assert.NotNull(rows[0].SessionId);
            Assert.NotNull(rows[1].SessionId);
            Assert.Null(rows[2].SessionId);
            Assert.Equal("insufficient_balance", rows[2].StartError);
            var sessions = await db.Sessions.AsNoTracking().Where(s => s.Status == "active" && s.UserId != null).ToListAsync();
            Assert.Equal(2, sessions.Count);
            Assert.All(sessions, s => Assert.Equal(Local(0, 20), s.EndUtc)); // the common end
            Assert.All(sessions, s => Assert.Equal(40m, s.TotalCost));
            // Bonus first (PC time), then the real balance
            Assert.Equal(75m - 25m, await BalanceAsync(players[0].Id)); // 100 - 25 fee - (40 - 15 bonus)
            Assert.Equal(35m, await BalanceAsync(players[1].Id));       // 100 - 25 - 40

            // The lock screen: the event of the session, and the event that books the PC
            var info = await PlayerSessions.InfoAsync(db, rows[0].DeviceId!.Value);
            Assert.Equal(EventName, info!.Event!.Name);
            Assert.Equal(Local(0, 20), info.Event.EndUtc);
            var running = await EventHolds.ActiveAsync(db, rows[2].DeviceId!.Value, _clock);
            Assert.True(running!.Running);
            Assert.Equal("20:00", running.EndLocal);
            var map = await EventHolds.NextByPcAsync(db, _clock);
            Assert.True(map[_stdPcs[0]].Running);
        }
        // The PC of the override was busy: the event needs it only when a player sits there (no player on STD-04)
        Assert.Contains(await AuditAsync("event.started"), a => a.Summary!.Contains("2 PC(s) unlocked until 20:00") && a.Summary.Contains("1 player(s) without"));

        // Staff start the third player after a top-up
        await using (var db = _testDb.NewContext())
        {
            var third = await db.EventPlayers.AsNoTracking().SingleAsync(p => p.UserId == players[2].Id);
            var stillPoor = await Events(db, "cashier").StartPlayerAsync(e.Id, third.Id);
            Assert.Equal("event_seat_failed", stillPoor.Error!.Code);
            Assert.Equal("insufficient_balance", stillPoor.Error.Args[1]);
            await Wallet.CreditAsync(db, players[2].Id, 50m);
            await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = players[2].Id, Type = LedgerTypes.TopUp, Method = PaymentMethods.None, Amount = 50m, BalanceDelta = 50m }, null, _clock.UtcNow);
            At(Local(0, 18, 30));
            var started = await Events(db, "cashier").StartPlayerAsync(e.Id, third.Id);
            Assert.True(started.Ok, started.Error?.Message);
            Assert.Equal("event_seat_running", (await Events(db, "cashier").StartPlayerAsync(e.Id, third.Id)).Error!.Code);
        }
        Assert.Equal(15m, await BalanceAsync(players[2].Id));
        await AssertMoneyAsync();

        // At the end the sessions expire (the PCs lock) and the job finishes the event: the fees are kept
        At(Local(0, 20, 0, 10));
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(4, await Sessions(db).CloseExpiredAsync()); // the three players and the walk-in of the override
            Assert.Equal(1, (await Events(db).RunJobsAsync()).Finished);
        }
        await using (var db = _testDb.NewContext())
        {
            var done = await db.Events.AsNoTracking().SingleAsync(x => x.Id == e.Id);
            Assert.Equal(EventStatuses.Finished, done.Status);
            Assert.Equal(Local(0, 20), done.EndedUtc);
            Assert.Null(await EventHolds.ActiveAsync(db, _stdPcs[0], _clock)); // the PCs are back to normal
            var rows = await db.Ledger.AsNoTracking().ToListAsync();
            var totals = LedgerTotals.Of(rows.Where(r => r.UserId != null)); // without the walk-in guest
            Assert.Equal(75m, totals.EventFeesKept);
            Assert.Equal(120m + 75m, totals.TimeTotal); // three times 40 of PC time, and the kept fees
            Assert.Equal(0m, (await Holds.EventFeesAsync(db)).Held);
            Assert.True(LedgerTotals.Of(rows).Reconciled);
        }
        await AssertMoneyAsync();
        Assert.Contains(await AuditAsync("event.finished"), a => a.Summary!.Contains("at its end") && a.Summary.Contains("kept"));
    }

    [Fact]
    public async Task A_free_event_is_audited_as_free_time_and_staff_can_finish_or_cancel_it_early()
    {
        var e = (await CreateAsync("18:00", 180, zone: _vip)).Value!;
        var (a, b) = (await PlayerAsync(0m), await PlayerAsync(0m));
        Assert.True((await RegisterAsync(e.Id, null, a)).Ok);
        Assert.True((await RegisterAsync(e.Id, null, b)).Ok);
        At(Local(0, 17, 40));
        await using (var db = _testDb.NewContext())
        {
            var start = await Events(db).StartAsync(e.Id); // staff start 20 minutes early
            Assert.True(start.Ok, start.Error?.Message);
            Assert.Equal(2, start.Value!.Started);
            Assert.Equal("event_not_open", (await Events(db).StartAsync(e.Id)).Error!.Code);
            var sessions = await db.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync();
            Assert.All(sessions, s => Assert.Equal(PaymentMethods.Free, s.PaymentMethod));
            Assert.All(sessions, s => Assert.Equal(Local(0, 21), s.EndUtc));
        }
        var free = await AuditAsync(AuditActions.SessionFreeTime);
        Assert.Equal(2, free.Count);
        Assert.All(free, f => Assert.Contains($"event #{e.Id}", f.Summary));
        await using (var db = _testDb.NewContext())
            Assert.Empty(await db.Ledger.AsNoTracking().Where(l => l.Type == LedgerTypes.SessionCharge).ToListAsync());

        // Finished early: the sessions end now and the PCs lock
        At(Local(0, 19, 0));
        await using (var db = _testDb.NewContext())
        {
            var finished = await Events(db).FinishAsync(e.Id, early: true);
            Assert.True(finished.Ok, finished.Error?.Message);
            Assert.Equal(Local(0, 19), finished.Value!.EndedUtc);
            Assert.Empty(await db.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync());
            Assert.Equal("event_not_open", (await Events(db).CancelAsync(e.Id, null)).Error!.Code);
        }

        // A running event that is cancelled ends its sessions and returns the fees
        var paid = (await CreateAsync("19:00", 60, fee: 10m)).Value!;
        var c = await PlayerAsync(10m);
        Assert.True((await RegisterAsync(paid.Id, null, c)).Ok);
        At(Local(0, 19, 1));
        await using (var db = _testDb.NewContext())
        {
            Assert.True((await Events(db).RunJobsAsync()).Started == 1);
            Assert.Single(await db.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync());
            Assert.True((await Events(db).CancelAsync(paid.Id, "збій світла")).Ok);
            Assert.Empty(await db.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync());
        }
        Assert.Equal(10m, await BalanceAsync(c.Id));
        await AssertMoneyAsync();

        // A scheduled event whose end passed without a start (server down) is cancelled with its fees returned
        var missed = (await CreateAsync("20:00", 60, zone: _vip, fee: 5m)).Value!;
        var d = await PlayerAsync(5m);
        Assert.True((await RegisterAsync(missed.Id, null, d)).Ok);
        At(Local(0, 21, 5));
        await using (var db = _testDb.NewContext())
            Assert.Equal(1, (await Events(db).RunJobsAsync()).Cancelled);
        Assert.Equal(5m, await BalanceAsync(d.Id));
        await AssertMoneyAsync();
    }

    [Fact]
    public async Task The_bracket_moves_the_winners_on_and_names_the_champion()
    {
        var e = (await CreateAsync("18:00", 240)).Value!;
        var teams = new List<long>();
        for (var i = 0; i < 3; i++) teams.Add((await RegisterAsync(e.Id, null, await PlayerAsync(0m))).Value!.Id);
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal("event_not_open", (await Events(db).SetResultAsync(e.Id, 1, new MatchResultRequest(teams[0]))).Error!.Code);
            var bracket = await Events(db).CreateBracketAsync(e.Id, shuffle: false);
            Assert.True(bracket.Ok, bracket.Error?.Message);
            // 3 teams: seed 1 has a bye, 2 plays 3; the final waits
            Assert.Equal(3, bracket.Value!.Count);
        }
        // A fourth team before the start: the bracket without results is cleared
        teams.Add((await RegisterAsync(e.Id, null, await PlayerAsync(0m))).Value!.Id);
        await using (var db = _testDb.NewContext())
        {
            Assert.Empty(await db.EventMatches.AsNoTracking().Where(m => m.EventId == e.Id).ToListAsync());
            Assert.True((await Events(db).CreateBracketAsync(e.Id, shuffle: false)).Ok);
            At(Local(0, 18, 1));
            Assert.True((await Events(db).RunJobsAsync()).Started == 1);
        }
        async Task<List<EventMatchEntity>> Matches()
        {
            await using var db = _testDb.NewContext();
            return await db.EventMatches.AsNoTracking().Where(m => m.EventId == e.Id).OrderBy(m => m.Round).ThenBy(m => m.Slot).ToListAsync();
        }
        async Task<BillingResult<EventMatchEntity>> Result(long match, long? winner, int? a = null, int? b = null)
        {
            await using var db = _testDb.NewContext();
            return await Events(db, "cashier").SetResultAsync(e.Id, match, new MatchResultRequest(winner, a, b));
        }
        var m = await Matches();
        Assert.Equal(3, m.Count);
        Assert.Equal((teams[0], teams[3]), (m[0].TeamAId!.Value, m[0].TeamBId!.Value)); // 1 v 4
        Assert.Equal((teams[1], teams[2]), (m[1].TeamAId!.Value, m[1].TeamBId!.Value)); // 2 v 3
        Assert.Equal("match_not_ready", (await Result(m[2].Id, teams[0])).Error!.Code);
        Assert.Equal("bad_winner", (await Result(m[0].Id, teams[1])).Error!.Code);
        Assert.Equal("bad_score", (await Result(m[0].Id, teams[0], -1, 2)).Error!.Code);
        Assert.True((await Result(m[0].Id, teams[3], 1, 2)).Ok);
        Assert.True((await Result(m[1].Id, teams[1], 16, 9)).Ok);
        m = await Matches();
        Assert.Equal((teams[3], teams[1]), (m[2].TeamAId!.Value, m[2].TeamBId!.Value));
        Assert.True((await Result(m[2].Id, teams[1], 2, 0)).Ok);
        await using (var db = _testDb.NewContext())
            Assert.Equal(teams[1], (await db.Events.AsNoTracking().SingleAsync(x => x.Id == e.Id)).WinnerTeamId);
        // The semi-final cannot change while the final has a winner; its score can
        Assert.Equal("next_match_decided", (await Result(m[0].Id, teams[0])).Error!.Code);
        Assert.True((await Result(m[0].Id, teams[3], 1, 3)).Ok);
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal("bracket_has_results", (await Events(db).CreateBracketAsync(e.Id, shuffle: true)).Error!.Code);
            // Clear the final: no champion
            Assert.True((await Events(db, "cashier").SetResultAsync(e.Id, m[2].Id, new MatchResultRequest(null))).Ok);
            Assert.Null((await db.Events.AsNoTracking().SingleAsync(x => x.Id == e.Id)).WinnerTeamId);
        }
        var audit = await AuditAsync("event.result");
        Assert.Contains(audit, a => a.Summary!.Contains($"team #{teams[1]} won the event"));
    }

    [Theory]
    [InlineData(2, 1, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(5, 3, 3)]
    [InlineData(8, 3, 0)]
    [InlineData(13, 4, 3)]
    public void A_bracket_gives_the_byes_to_the_best_seeds(int teams, int rounds, int byes)
    {
        var ids = Enumerable.Range(1, teams).Select(i => (long)i).ToList();
        var matches = Bracket.Create(ids);
        Assert.Equal(rounds, Bracket.Rounds(teams));
        Assert.Equal(Bracket.Size(teams) - 1, matches.Count);
        Assert.Equal(byes, matches.Count(m => m.IsBye));
        // The byes are the best seeds, every team plays once in round 1 or has a bye, seeds 1 and 2 are in different halves
        Assert.All(matches.Where(m => m.IsBye), m => Assert.True(m.Winner <= byes));
        Assert.Equal(ids, matches.Where(m => m.Round == 1).SelectMany(m => new[] { m.TeamA, m.TeamB }).Where(t => t != null).Select(t => t!.Value).OrderBy(t => t));
        var half = Bracket.Size(teams) / 4;
        var first = matches.Where(m => m.Round == 1).ToList();
        if (teams > 2)
        {
            Assert.Contains(first.Take(half), m => m.TeamA == 1 || m.TeamB == 1);
            Assert.Contains(first.Skip(half), m => m.TeamA == 2 || m.TeamB == 2);
        }
        // Play it out: the lower seed always wins; the champion is seed 1
        for (var round = 1; round <= rounds; round++)
            foreach (var m in matches.Where(m => m.Round == round && !m.IsBye).ToList())
            {
                var current = matches.Single(x => x.Round == m.Round && x.Slot == m.Slot);
                Assert.Null(Bracket.SetResult(matches, round, m.Slot, Math.Min(current.TeamA!.Value, current.TeamB!.Value), 1, 0));
            }
        Assert.Equal(1L, Bracket.Champion(matches));
        Assert.Equal(new[] { 1, 2 }, Bracket.SeedOrder(2));
        Assert.Equal(new[] { 1, 8, 4, 5, 2, 7, 3, 6 }, Bracket.SeedOrder(8));
    }
}
