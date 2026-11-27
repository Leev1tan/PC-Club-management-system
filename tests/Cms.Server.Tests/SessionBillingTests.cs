using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

/// <summary>Session rules with a fixed club clock (UTC+2) on an in-memory SQLite database.</summary>
public sealed class SessionBillingTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = Clock(Local(0, 10)); // Monday 10:00

    private Guid _stdZone, _vipZone, _consoleZone, _std1, _std2, _vip1, _console1;
    private Guid _stdTariff, _vipTariff, _pack3h, _nightPack, _vipPack3h;

    public SessionBillingTests()
    {
        using var db = _testDb.NewContext();
        _stdZone = Guid.NewGuid(); _vipZone = Guid.NewGuid(); _consoleZone = Guid.NewGuid();
        db.Zones.AddRange(
            new ZoneEntity { Id = _stdZone, Name = "Standard" },
            new ZoneEntity { Id = _vipZone, Name = "VIP", SortOrder = 1 },
            new ZoneEntity { Id = _consoleZone, Name = "Console", SortOrder = 2 });
        var std = Standard(_stdZone);
        std.Tariff.IsDefault = true;
        var vip = Vip(_vipZone);
        var console = new TariffEntity { Id = Guid.NewGuid(), Name = "Console", PricePerHour = 50m, ZoneId = _consoleZone };
        _stdTariff = std.Tariff.Id;
        _vipTariff = vip.Tariff.Id;
        db.Tariffs.AddRange(std.Tariff, vip.Tariff, console);
        db.TariffRules.AddRange(std.Rules.Concat(vip.Rules));

        DeviceEntity Pc(string name, Guid zone) => new() { Id = Guid.NewGuid(), Hostname = name, DeviceKey = "k-" + name, Status = "approved", ZoneId = zone };
        var pcs = new[] { Pc("STD-01", _stdZone), Pc("STD-02", _stdZone), Pc("VIP-01", _vipZone), Pc("CON-01", _consoleZone) };
        (_std1, _std2, _vip1, _console1) = (pcs[0].Id, pcs[1].Id, pcs[2].Id, pcs[3].Id);
        db.Devices.AddRange(pcs);

        _pack3h = Guid.NewGuid(); _nightPack = Guid.NewGuid(); _vipPack3h = Guid.NewGuid();
        db.Packages.AddRange(
            new PackageEntity { Id = _pack3h, Name = "3 години", ZoneId = _stdZone, Price = 150m, DurationMinutes = 180 },
            new PackageEntity { Id = _nightPack, Name = "Нічний", ZoneId = _stdZone, Price = 250m, WindowStartMinute = 22 * 60, WindowEndMinute = 8 * 60 },
            new PackageEntity { Id = _vipPack3h, Name = "3 години VIP", ZoneId = _vipZone, Price = 240m, DurationMinutes = 180 });
        db.SaveChanges();
    }

    public void Dispose() => _testDb.Dispose();

    private SessionService Service(CmsDbContext db) => new(db, new EfDeviceRepository(db), _clock);

    private void At(DateTimeOffset utc) => _clock.Time().UtcNow = utc;

    private async Task<Guid> PlayerAsync(decimal balance, decimal bonus = 0m)
    {
        await using var db = _testDb.NewContext();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P", Balance = balance, BonusPoints = bonus };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        // Opening balance so that the ledger explains the balances
        if (balance != 0 || bonus != 0)
            await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = user.Id, Type = LedgerTypes.OpeningBalance, Method = "none", Amount = balance + bonus, BalanceDelta = balance, BonusDelta = bonus }, null, _clock.UtcNow);
        return user.Id;
    }

    private async Task<SessionEntity> StartAsync(StartSessionRequest req)
    {
        await using var db = _testDb.NewContext();
        var result = await Service(db).StartAsync(req);
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!;
    }

    private async Task<(decimal Balance, decimal Bonus)> BalancesAsync(Guid userId)
    {
        await using var db = _testDb.NewContext();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        return (u.Balance, u.BonusPoints);
    }

    private async Task AssertLedgerIsConsistentAsync(Guid userId)
    {
        await using var db = _testDb.NewContext();
        Assert.Empty(await Ledger.ReconcileAsync(db, userId));
        Assert.Null(await Ledger.CheckChainAsync(db, userId));
    }

    private async Task<List<string>> CommandsAsync(Guid deviceId)
    {
        await using var db = _testDb.NewContext();
        return (await db.Commands.AsNoTracking().Where(c => c.DeviceId == deviceId).ToListAsync())
            .OrderBy(c => c.CreatedUtc).Select(c => c.Type + (c.PayloadJson is { } p ? " " + p : "")).ToList();
    }

    [Fact]
    public async Task An_open_session_across_a_rule_boundary_is_billed_per_segment_at_the_end()
    {
        At(Local(0, 16, 30));
        var session = await StartAsync(new StartSessionRequest(_std1, null, null, null));
        Assert.Contains($"{CommandTypes.SessionSet} {{\"endUtc\":null}}", await CommandsAsync(_std1)); // open-ended: no end on the PC

        At(Local(0, 17, 30));
        await using var db = _testDb.NewContext();
        var end = await Service(db).EndAsync(session.Id, new EndSessionRequest("card"));
        Assert.True(end.Ok);
        Assert.Equal(57.50m, end.Value!.Session.TotalCost); // 30 min × 45 + 30 min × 70
        Assert.Equal(57.50m, end.Value.AmountDue);

        var row = await db.Ledger.AsNoTracking().SingleAsync(l => l.SessionId == session.Id);
        Assert.Null(row.UserId);
        Assert.Equal((PaymentMethods.Card, 57.50m, 0m), (row.Method, row.DeskAmount, row.BalanceDelta));
        Assert.All(await db.SessionSegments.AsNoTracking().Where(s => s.SessionId == session.Id).ToListAsync(), s => Assert.NotNull(s.EndUtc));
    }

    [Fact]
    public async Task Paused_time_is_not_billed_and_bonus_is_spent_first()
    {
        var player = await PlayerAsync(100m, bonus: 10m);
        var session = await StartAsync(new StartSessionRequest(_std1, null, player, null));

        At(Local(0, 10, 30));
        await using (var db = _testDb.NewContext()) Assert.True((await Service(db).PauseAsync(session.Id)).Ok);
        Assert.EndsWith($"{CommandTypes.SessionSet} {{\"endUtc\":null}}|{CommandTypes.Lock}", string.Join("|", await CommandsAsync(_std1)));
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal("session_paused", (await Service(db).PauseAsync(session.Id)).Error!.Code);
            Assert.Equal("session_paused", (await Service(db).ExtendAsync(session.Id, new ExtendSessionRequest(30))).Error!.Code);
        }

        At(Local(0, 11));
        await using (var db = _testDb.NewContext()) Assert.True((await Service(db).ResumeAsync(session.Id)).Ok);
        Assert.EndsWith(CommandTypes.Unlock, (await CommandsAsync(_std1)).Last());

        At(Local(0, 11, 30));
        await using (var db = _testDb.NewContext())
        {
            var end = await Service(db).EndAsync(session.Id);
            Assert.Equal(45m, end.Value!.Session.TotalCost); // 60 played minutes × 45, the 30 paused minutes are free
            Assert.Equal(new Charge(10m, 35m), end.Value.FromBalances);
            Assert.Equal(0m, end.Value.AmountDue);
            Assert.Equal(2, await db.SessionSegments.CountAsync(s => s.SessionId == session.Id));
        }
        Assert.Equal((65m, 0m), await BalancesAsync(player));
        await AssertLedgerIsConsistentAsync(player);
    }

    [Fact]
    public async Task Resume_moves_the_end_of_prepaid_time_by_the_pause()
    {
        var session = await StartAsync(new StartSessionRequest(_std1, null, null, 60, PaymentMethod: "cash"));
        Assert.Equal(Local(0, 11), session.EndUtc);

        At(Local(0, 10, 20));
        await using (var db = _testDb.NewContext()) await Service(db).PauseAsync(session.Id);

        // While paused, the old end time passes but the session stays (its time stands still)
        At(Local(0, 11, 5));
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(0, await Service(db).CloseExpiredAsync());
            var resumed = await Service(db).ResumeAsync(session.Id);
            Assert.Equal(Local(0, 11, 45), resumed.Value!.EndUtc); // 40 minutes were left
        }
        Assert.Contains(await CommandsAsync(_std1), c => c.StartsWith(CommandTypes.SessionSet) && c.Contains("2026-11-23T09:45:00"));
    }

    [Fact]
    public async Task A_night_pack_never_runs_past_its_cut_off()
    {
        var player = await PlayerAsync(300m, bonus: 50m);
        At(Local(0, 23)); // Monday 23:00
        var session = await StartAsync(new StartSessionRequest(_std1, null, player, null, _nightPack));
        Assert.Equal(Local(1, 8), session.EndUtc);
        Assert.Equal(Local(1, 8), session.CutoffUtc);
        Assert.Equal((100m, 0m), await BalancesAsync(player)); // 250 ₴: 50 bonus + 200 balance

        await using (var db = _testDb.NewContext())
        {
            var extend = await Service(db).ExtendAsync(session.Id, new ExtendSessionRequest(60));
            Assert.Equal("past_cutoff", extend.Error!.Code);
        }

        // A pause does not move the cut-off
        At(Local(1, 7));
        await using (var db = _testDb.NewContext()) await Service(db).PauseAsync(session.Id);
        At(Local(1, 7, 30));
        await using (var db = _testDb.NewContext())
            Assert.Equal(Local(1, 8), (await Service(db).ResumeAsync(session.Id)).Value!.EndUtc);

        // Paused again until after 08:00: the session ends at the cut-off anyway
        At(Local(1, 7, 40));
        await using (var db = _testDb.NewContext()) await Service(db).PauseAsync(session.Id);
        At(Local(1, 8, 0, 5));
        await using (var db = _testDb.NewContext())
        {
            Assert.Equal(1, await Service(db).CloseExpiredAsync());
            var ended = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
            Assert.Equal(("ended", Local(1, 8)), (ended.Status, ended.EndUtc));
            Assert.Null(ended.PausedUtc);
        }
        await AssertLedgerIsConsistentAsync(player);
    }

    [Fact]
    public async Task A_package_is_bought_from_the_balance_and_needs_enough_money()
    {
        var player = await PlayerAsync(100m, bonus: 60m);
        var session = await StartAsync(new StartSessionRequest(_std1, null, player, null, _pack3h));
        Assert.Equal((Local(0, 13), 150m, PaymentMethods.Balance), (session.EndUtc!.Value, session.TotalCost, session.PaymentMethod!));
        Assert.Equal((10m, 0m), await BalancesAsync(player));

        await using (var db = _testDb.NewContext())
        {
            var row = await db.Ledger.AsNoTracking().SingleAsync(l => l.SessionId == session.Id);
            Assert.Equal((LedgerTypes.PackagePurchase, -90m, -60m, 10m, 0m), (row.Type, row.BalanceDelta, row.BonusDelta, row.BalanceAfter!.Value, row.BonusAfter!.Value));

            // The second purchase fails: nothing changes
            var rows = await db.Ledger.CountAsync();
            var second = await Service(db).StartAsync(new StartSessionRequest(_std2, null, player, null, _pack3h));
            Assert.Equal("insufficient_balance", second.Error!.Code);
            Assert.Equal(rows, await db.Ledger.CountAsync());
            Assert.False(await db.Sessions.AnyAsync(s => s.DeviceId == _std2));
        }
        Assert.Equal((10m, 0m), await BalancesAsync(player));
        await AssertLedgerIsConsistentAsync(player);
    }

    [Fact]
    public async Task Packages_are_bound_to_their_zone_and_window()
    {
        var player = await PlayerAsync(1000m);
        await using var db = _testDb.NewContext();
        Assert.Equal("package_other_zone", (await Service(db).StartAsync(new StartSessionRequest(_vip1, null, player, null, _pack3h))).Error!.Code);
        Assert.Equal("package_outside_window", (await Service(db).StartAsync(new StartSessionRequest(_std1, null, player, null, _nightPack))).Error!.Code);
        Assert.Equal("tariff_other_zone", (await Service(db).StartAsync(new StartSessionRequest(_std1, _vipTariff, player, 60))).Error!.Code);
        Assert.Equal(1000m, (await BalancesAsync(player)).Balance);
    }

    [Fact]
    public async Task UseBonus_false_takes_only_real_money()
    {
        var player = await PlayerAsync(200m, bonus: 100m);
        await StartAsync(new StartSessionRequest(_std1, null, player, null, _pack3h, UseBonus: false));
        Assert.Equal((50m, 100m), await BalancesAsync(player));
    }

    [Fact]
    public async Task Walk_in_guests_pay_at_the_desk()
    {
        var session = await StartAsync(new StartSessionRequest(_std1, null, null, 120, PaymentMethod: "card"));
        Assert.Equal(90m, session.TotalCost); // Monday 10:00-12:00 at 45 ₴/h

        await using var db = _testDb.NewContext();
        var row = await db.Ledger.AsNoTracking().SingleAsync(l => l.SessionId == session.Id);
        Assert.Equal((null, PaymentMethods.Card, 90m, LedgerTypes.SessionCharge), (row.UserId, row.Method, row.DeskAmount, row.Type));

        var balance = await Service(db).StartAsync(new StartSessionRequest(_std2, null, null, 60, PaymentMethod: "balance"));
        Assert.Equal("guest_needs_desk", balance.Error!.Code);

        // Extensions of a guest session are paid at the desk too (cash by default)
        var extended = await Service(db).ExtendAsync(session.Id, new ExtendSessionRequest(30));
        Assert.Equal(112.50m, extended.Value!.TotalCost);
        var rows = (await db.Ledger.AsNoTracking().Where(l => l.SessionId == session.Id).ToListAsync()).OrderBy(l => l.Id).ToList();
        Assert.Equal(new[] { (PaymentMethods.Card, 90m), (PaymentMethods.Cash, 22.50m) }, rows.Select(l => (l.Method, l.DeskAmount)));
    }

    [Fact]
    public async Task A_transfer_within_the_zone_costs_nothing_and_moves_the_pc_commands()
    {
        var session = await StartAsync(new StartSessionRequest(_std1, null, null, 60, PaymentMethod: "cash"));
        At(Local(0, 10, 15));
        await using (var db = _testDb.NewContext())
        {
            var moved = await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_std2));
            Assert.True(moved.Ok, moved.Error?.Message);
            Assert.Equal(0m, moved.Value.Quote.Surcharge);
            Assert.Equal((_std2, Local(0, 11)), (moved.Value.Session.DeviceId, moved.Value.Session.EndUtc!.Value));
            Assert.Equal(2, await db.SessionSegments.CountAsync(s => s.SessionId == session.Id));
        }
        Assert.Equal(CommandTypes.Lock, (await CommandsAsync(_std1)).Last());
        Assert.Equal(CommandTypes.Unlock, (await CommandsAsync(_std2)).Last());
    }

    [Fact]
    public async Task An_upgrade_to_a_dearer_zone_pays_the_price_difference_of_the_remaining_time()
    {
        var player = await PlayerAsync(200m);
        var session = await StartAsync(new StartSessionRequest(_std1, null, player, 120)); // 10:00-12:00 at 45 = 90 ₴
        Assert.Equal((110m, 0m), await BalancesAsync(player));

        At(Local(0, 11));
        await using (var db = _testDb.NewContext())
        {
            var quote = await Service(db).QuoteTransferAsync(session.Id, _vip1);
            Assert.Equal((45m, 60, "tariff"), (quote.Value!.Surcharge, quote.Value.RemainingMinutes, quote.Value.Basis)); // 60 min: VIP 90 - Std 45

            var moved = await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_vip1));
            Assert.Equal((_vipTariff, 135m), (moved.Value.Session.TariffId!.Value, moved.Value.Session.TotalCost));
        }
        Assert.Equal((65m, 0m), await BalancesAsync(player));
        await AssertLedgerIsConsistentAsync(player);

        // Back to a cheaper zone: no refund
        await using (var db = _testDb.NewContext())
            Assert.Equal(0m, (await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_std2))).Value.Quote.Surcharge);
    }

    [Fact]
    public async Task A_package_moves_to_the_same_kind_of_package_of_the_target_zone()
    {
        var player = await PlayerAsync(500m);
        var session = await StartAsync(new StartSessionRequest(_std1, null, player, null, _pack3h)); // 150 ₴, 10:00-13:00

        await using var db = _testDb.NewContext();
        Assert.Equal("package_no_match_in_zone", (await Service(db).QuoteTransferAsync(session.Id, _console1)).Error!.Code);

        At(Local(0, 11)); // 2 of 3 hours left: (240 - 150) × 2/3 = 60 ₴
        var moved = await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_vip1));
        Assert.True(moved.Ok, moved.Error?.Message);
        Assert.Equal(("package", 60m, _vipPack3h), (moved.Value.Quote.Basis, moved.Value.Quote.Surcharge, moved.Value.Session.PackageId!.Value));
        Assert.Equal(Local(0, 13), moved.Value.Session.EndUtc);
        Assert.Equal(290m, (await BalancesAsync(player)).Balance);
    }

    [Fact]
    public async Task A_transferred_open_session_bills_each_segment_at_its_own_tariff()
    {
        At(Local(0, 16));
        var session = await StartAsync(new StartSessionRequest(_std1, null, null, null));
        At(Local(0, 16, 40));
        await using var db = _testDb.NewContext();
        var moved = await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_vip1));
        Assert.Equal(("segments", 0m), (moved.Value.Quote.Basis, moved.Value.Quote.Surcharge));

        At(Local(0, 17, 30));
        var end = await Service(db).EndAsync(session.Id);
        // Std 16:00-16:40 at 45 = 30; VIP 16:40-17:00 at 90 = 30, 17:00-17:30 at 100 = 50
        Assert.Equal(110m, end.Value!.Session.TotalCost);
    }

    [Fact]
    public async Task A_transfer_needs_a_free_approved_pc()
    {
        var a = await StartAsync(new StartSessionRequest(_std1, null, null, 60, PaymentMethod: "cash"));
        await StartAsync(new StartSessionRequest(_std2, null, null, 60, PaymentMethod: "cash"));
        await using var db = _testDb.NewContext();
        Assert.Equal("device_busy", (await Service(db).TransferAsync(a.Id, new TransferSessionRequest(_std2))).Error!.Code);
        Assert.Equal("same_device", (await Service(db).TransferAsync(a.Id, new TransferSessionRequest(_std1))).Error!.Code);
        Assert.Equal("device_not_found", (await Service(db).TransferAsync(a.Id, new TransferSessionRequest(Guid.NewGuid()))).Error!.Code);
    }

    [Fact]
    public async Task A_paused_session_can_move_and_stays_paused_until_resumed()
    {
        var session = await StartAsync(new StartSessionRequest(_std1, null, null, 60, PaymentMethod: "cash"));
        At(Local(0, 10, 10));
        await using var db = _testDb.NewContext();
        await Service(db).PauseAsync(session.Id);
        At(Local(0, 10, 20));
        var moved = await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_std2));
        Assert.NotNull(moved.Value.Session.PausedUtc);
        Assert.DoesNotContain(await CommandsAsync(_std2), c => c == CommandTypes.Unlock);

        At(Local(0, 10, 30));
        var resumed = await Service(db).ResumeAsync(session.Id);
        Assert.Equal(Local(0, 11, 20), resumed.Value!.EndUtc);
        Assert.Equal(CommandTypes.Unlock, (await CommandsAsync(_std2)).Last());
        var segments = await db.SessionSegments.AsNoTracking().Where(s => s.SessionId == session.Id).ToListAsync();
        Assert.Equal(new[] { _std1, _std2 }, segments.OrderBy(s => s.StartUtc).Select(s => s.DeviceId));
    }
}
