using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

/// <summary>Sessions "from the balance" (player login): the end follows the balances and the tariff rules.</summary>
public sealed class BalanceLimitTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly ClubClock _clock = Clock(Local(0, 10)); // Monday 10:00: 45 ₴/h until 17:00, then 70 ₴/h
    private readonly Guid _zone = Guid.NewGuid(), _pc = Guid.NewGuid(), _pc2 = Guid.NewGuid(), _minZone = Guid.NewGuid(), _minPc = Guid.NewGuid();

    public BalanceLimitTests()
    {
        using var db = _testDb.NewContext();
        db.Zones.AddRange(new ZoneEntity { Id = _zone, Name = "Standard" }, new ZoneEntity { Id = _minZone, Name = "Step", SortOrder = 1 });
        var std = Standard(_zone);
        var stepped = Standard(_minZone, step: 15, minimum: 30m);
        db.Tariffs.AddRange(std.Tariff, stepped.Tariff);
        db.TariffRules.AddRange(std.Rules.Concat(stepped.Rules));
        db.Devices.AddRange(
            new DeviceEntity { Id = _pc, Hostname = "STD-01", DeviceKey = "k1", Status = "approved", ZoneId = _zone },
            new DeviceEntity { Id = _pc2, Hostname = "STD-02", DeviceKey = "k2", Status = "approved", ZoneId = _zone },
            new DeviceEntity { Id = _minPc, Hostname = "STEP-01", DeviceKey = "k3", Status = "approved", ZoneId = _minZone });
        db.SaveChanges();
    }

    public void Dispose() => _testDb.Dispose();

    private SessionService Service(CmsDbContext db) =>
        new(db, new EfDeviceRepository(db), _clock, new Cms.Server.Auth.AuditLogger(db, new Microsoft.AspNetCore.Http.HttpContextAccessor()));
    private void At(DateTimeOffset utc) => _clock.Time().UtcNow = utc;

    private async Task<Guid> PlayerAsync(decimal balance, decimal bonus = 0m, decimal debt = 0m)
    {
        await using var db = _testDb.NewContext();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "p" + Guid.NewGuid().ToString("N")[..8], DisplayName = "P", Balance = balance, BonusPoints = bonus, Debt = debt };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = user.Id, Type = LedgerTypes.OpeningBalance, Method = "none", Amount = balance + bonus,
            BalanceDelta = balance, BonusDelta = bonus, DebtDelta = debt }, null, _clock.UtcNow);
        return user.Id;
    }

    private async Task<BillingResult<SessionEntity>> StartAsync(Guid pc, Guid user)
    {
        await using var db = _testDb.NewContext();
        return await Service(db).StartAsync(new StartSessionRequest(pc, null, user, null, PaymentMethod: PaymentMethods.Balance, LimitToBalance: true));
    }

    private async Task<List<string?>> SessionSetsAsync(Guid pc)
    {
        await using var db = _testDb.NewContext();
        return (await db.Commands.AsNoTracking().Where(c => c.DeviceId == pc && c.Type == CommandTypes.SessionSet).ToListAsync())
            .OrderBy(c => c.CreatedUtc).Select(c => c.PayloadJson).ToList();
    }

    private async Task<int> TickAsync()
    {
        await using var db = _testDb.NewContext();
        return await Service(db).CloseExpiredAsync();
    }

    [Fact]
    public void The_projection_is_the_last_minute_that_the_money_pays()
    {
        var now = Local(0, 10);
        var pricing = Pricing(Standard());
        decimal Cost(DateTimeOffset until) => TariffCalculator.QuotePlayed(new[] { (pricing, now, until) }, pricing, Zone).Total;
        Assert.Equal(now.AddMinutes(120), SessionService.ProjectEnd(Cost, 90m, now));        // 45 ₴/h
        Assert.Equal(now.AddMinutes(1), SessionService.ProjectEnd(Cost, 0.75m, now));        // one minute
        Assert.Equal(now, SessionService.ProjectEnd(Cost, 0.74m, now));                      // not even a minute
        // 7 h at 45 = 315 until 17:00, then 70 ₴/h: 35 ₴ more = 30 min
        Assert.Equal(Local(0, 17, 30), SessionService.ProjectEnd(Cost, 350m, now));
        Assert.Equal(now.AddMinutes(SessionService.MaxBalanceMinutes), SessionService.ProjectEnd(Cost, 100_000m, now));
    }

    [Fact]
    public async Task A_session_from_the_balance_tells_the_agent_when_the_money_runs_out_and_ends_there()
    {
        var player = await PlayerAsync(60m, bonus: 30m);
        var started = await StartAsync(_pc, player);
        Assert.True(started.Ok, started.Error?.Message);
        var session = started.Value!;
        Assert.False(session.IsPrepaid);
        Assert.True(session.LimitToBalance);
        Assert.Equal(Local(0, 12), session.BalanceEndUtc); // (60 + 30 bonus) / 45 per hour = 2 h
        Assert.Null(session.EndUtc);
        Assert.Contains(Local(0, 12).ToString("yyyy-MM-ddTHH:mm:ss"), Assert.Single(await SessionSetsAsync(_pc)));

        // A top-up at the desk moves the end; a small change does not send a new end
        At(Local(0, 11));
        Assert.Equal(0, await TickAsync());
        Assert.Single(await SessionSetsAsync(_pc));
        await using (var db = _testDb.NewContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await Wallet.CreditAsync(db, player, 45m);
            await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = player, Type = LedgerTypes.TopUp, Method = PaymentMethods.Cash, Amount = 45m, BalanceDelta = 45m, DeskAmount = 45m }, null, _clock.UtcNow);
            await tx.CommitAsync();
        }
        Assert.Equal(0, await TickAsync());
        await using (var db = _testDb.NewContext())
            Assert.Equal(Local(0, 13), (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).BalanceEndUtc);
        Assert.Equal(2, (await SessionSetsAsync(_pc)).Count);

        // At 13:00 the money is used up: the session ends, paid from the bonus first, then the balance
        At(Local(0, 13));
        Assert.Equal(1, await TickAsync());
        await using (var db = _testDb.NewContext())
        {
            var ended = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
            Assert.Equal(("ended", 135m), (ended.Status, ended.TotalCost));
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == player);
            Assert.Equal((0m, 0m, 0m), (user.Balance, user.BonusPoints, user.Debt));
            Assert.Empty(await Ledger.ReconcileAsync(db));
            Assert.Contains(await db.Commands.AsNoTracking().Where(c => c.DeviceId == _pc).Select(c => c.Type).ToListAsync(), t => t == CommandTypes.Lock);
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.Action == "session.ended").ToListAsync();
            Assert.Contains(audit, a => a.Summary!.EndsWith("(balance used up)"));
        }
    }

    [Fact]
    public async Task Money_spent_elsewhere_ends_the_session_early_and_a_rest_becomes_debt()
    {
        var player = await PlayerAsync(45m);
        var session = (await StartAsync(_pc, player)).Value!;
        At(Local(0, 10, 30)); // 22.50 played
        await using (var db = _testDb.NewContext())
        {
            // Bar purchase from the balance: 40 of the 45 are gone
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.NotNull(await Wallet.TryChargeAsync(db, player, 40m, SpendPurpose.Bar, useBonus: false));
            await Ledger.AppendAsync(db, new LedgerEntryEntity { UserId = player, Type = LedgerTypes.BarSale, Method = PaymentMethods.Balance, Amount = 40m, BalanceDelta = -40m }, null, _clock.UtcNow);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await TickAsync());
        await using (var db = _testDb.NewContext())
        {
            var ended = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
            Assert.Equal(22.5m, ended.TotalCost);
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == player);
            Assert.Equal((0m, 17.5m), (user.Balance, user.Debt));
            Assert.Empty(await Ledger.ReconcileAsync(db));
        }
    }

    [Fact]
    public async Task Pause_keeps_the_end_and_resume_sends_the_new_end()
    {
        var player = await PlayerAsync(90m);
        var session = (await StartAsync(_pc, player)).Value!;
        At(Local(0, 11));
        await using (var db = _testDb.NewContext()) Assert.True((await Service(db).PauseAsync(session.Id)).Ok);
        At(Local(0, 11, 20));
        Assert.Equal(0, await TickAsync()); // paused: nothing runs
        await using (var db = _testDb.NewContext()) Assert.True((await Service(db).ResumeAsync(session.Id)).Ok);
        // 45 ₴ left for 1 h: the end is 12:20, sent with the resume (not "no end")
        var last = (await SessionSetsAsync(_pc)).Last();
        Assert.Contains(Local(0, 12, 20).ToString("yyyy-MM-ddTHH:mm:ss"), last);
    }

    [Fact]
    public async Task A_transfer_sends_the_end_to_the_new_pc()
    {
        var player = await PlayerAsync(90m);
        var session = (await StartAsync(_pc, player)).Value!;
        At(Local(0, 10, 30));
        await using (var db = _testDb.NewContext())
            Assert.True((await Service(db).TransferAsync(session.Id, new TransferSessionRequest(_pc2))).Ok);
        Assert.Contains(Local(0, 12).ToString("yyyy-MM-ddTHH:mm:ss"), (await SessionSetsAsync(_pc2)).Last());
    }

    [Fact]
    public async Task Too_little_money_a_debt_or_a_guest_start_no_session_from_the_balance()
    {
        // Step 15 min and a minimum charge of 30: 20 ₴ buy nothing
        var poor = await PlayerAsync(20m);
        var refused = await StartAsync(_minPc, poor);
        Assert.Equal("insufficient_balance", refused.Error?.Code);
        var owes = await PlayerAsync(100m, debt: 10m);
        Assert.Equal("player_has_debt", (await StartAsync(_pc, owes)).Error?.Code);
        await using var db = _testDb.NewContext();
        var guest = await Service(db).StartAsync(new StartSessionRequest(_pc, null, null, null, LimitToBalance: true));
        Assert.Equal("balance_limit_needs_player", guest.Error?.Code);
        var prepaid = await Service(db).StartAsync(new StartSessionRequest(_pc, null, await PlayerAsync(100m), 60, LimitToBalance: true));
        Assert.Equal("balance_limit_needs_player", prepaid.Error?.Code);
    }

    [Fact]
    public async Task With_a_step_and_a_minimum_the_end_never_costs_more_than_the_money()
    {
        var player = await PlayerAsync(40m); // minimum 30, step 15 min at 45 ₴/h = 11.25 per step
        var session = (await StartAsync(_minPc, player)).Value!;
        // The minimum of 30 ₴ covers 30 min, 45 min cost 33.75, a 46th minute starts a 4th step (60 min = 45 > 40): the end is 10:45
        Assert.Equal(Local(0, 10, 45), session.BalanceEndUtc);
        At(Local(0, 10, 45));
        Assert.Equal(1, await TickAsync());
        await using var db = _testDb.NewContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == player);
        Assert.Equal((6.25m, 0m), (user.Balance, user.Debt));
    }
}
