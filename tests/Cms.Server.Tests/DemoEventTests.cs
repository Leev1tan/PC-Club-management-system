using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Server.Events;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The demo has one finished tournament (bracket, fees kept) and one upcoming event (two teams, fees held), at any club time.</summary>
public sealed class DemoEventTests
{
    [Theory]
    [InlineData("09:01")]
    [InlineData("21:01")]
    [InlineData("00:01")]
    [InlineData("now")]
    public async Task The_demo_has_a_finished_and_an_upcoming_event(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        using var testDb = new TestDb();
        await using (var db = testDb.NewShiftContext()) await DemoSeeder.ResetAsync(db, now, new Random(42));

        await using var check = testDb.NewContext();
        var events = await check.Events.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        var (done, next) = (events[0], events[1]);

        Assert.Equal(DemoSeeder.FinishedEventName, done.Name);
        Assert.Equal(EventStatuses.Finished, done.Status);
        Assert.True(done.EndUtc < now);
        Assert.Equal(1L, done.WinnerTeamId);
        Assert.Equal(4, await check.EventTeams.CountAsync(t => t.EventId == done.Id));
        var players = await check.EventPlayers.AsNoTracking().Where(p => p.EventId == done.Id).ToListAsync();
        Assert.Equal(8, players.Count);
        Assert.All(players, p => Assert.Equal(EventFeeStatuses.Kept, p.FeeStatus));
        // Every player had a free session of the event on its PC, and the PC played nothing else then
        var sessionIds = players.Select(p => p.SessionId!.Value).ToList();
        var sessions = await check.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToListAsync();
        Assert.Equal(8, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(PaymentMethods.Free, s.PaymentMethod));
        var pcs = players.Select(p => p.DeviceId!.Value).ToList();
        var overlapping = (await check.Sessions.AsNoTracking().Where(s => pcs.Contains(s.DeviceId)).ToListAsync())
            .Where(s => !sessionIds.Contains(s.Id) && s.StartUtc < done.EndUtc && (s.EndUtc ?? now) > done.StartUtc).ToList();
        Assert.Empty(overlapping);
        var matches = await check.EventMatches.AsNoTracking().Where(m => m.EventId == done.Id).ToListAsync();
        Assert.Equal(3, matches.Count);
        Assert.All(matches, m => Assert.NotNull(m.WinnerTeamId));

        Assert.Equal(DemoSeeder.UpcomingEventName, next.Name);
        Assert.Equal(EventStatuses.Scheduled, next.Status);
        Assert.True(next.StartUtc > now.AddDays(1));
        Assert.Equal(EventPriceModes.Fixed, next.PriceMode);
        Assert.Equal(2, await check.EventTeams.CountAsync(t => t.EventId == next.Id && t.Status == EventMemberStatuses.Active));
        Assert.Equal(6, await check.EventDevices.CountAsync(d => d.EventId == next.Id));

        // Money: the kept fees are revenue, the held ones wait; everything reconciles
        var fees = await Holds.EventFeesAsync(check);
        Assert.True(fees.Ok, string.Join(",", fees.Broken));
        Assert.Equal(400m, fees.Held);
        Assert.Empty(await Ledger.ReconcileAsync(check));
        var totals = LedgerTotals.Of(await check.Ledger.AsNoTracking().ToListAsync());
        Assert.Equal(400m, totals.EventFeesKept);
        Assert.True(totals.Reconciled);
        Assert.True((await AuditChain.VerifyAsync(check)).Ok);

        // The audit log has the event ids, never the names of the events or the teams
        var audit = await check.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("event.")).ToListAsync();
        Assert.True(audit.Count >= 10);
        Assert.DoesNotContain(audit, a => (a.Summary + a.DetailsJson).Contains("Кубок") || (a.Summary + a.DetailsJson).Contains("Сови"));
    }
}
