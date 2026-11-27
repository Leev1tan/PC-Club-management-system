using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Events;
using Cms.Server.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

public static partial class DemoSeeder
{
    public const string FinishedEventName = "Кубок клубу CS2 2×2";
    public const string UpcomingEventName = "Турнір VIP Wingman 2×2";
    private static readonly string[] TeamNames = { "Сови", "Тигри", "Козаки", "Вовки", "Яструби", "Чайки" };

    private sealed partial class Seeding
    {
        public List<object> EventRows { get; } = new();

        /// <summary>
        /// A finished tournament three days ago (spec area 9): 4 teams of 2 on STD-01..STD-08 from 18:00 to 22:00, free PC
        /// time, an entry fee of 50 per player held at the registration and kept at the end, a bracket with results (the
        /// team of vika_gg won; she got free VIP time the next day, see SeedFreeTime). Written like the real service writes it.
        /// </summary>
        private void SeedFinishedEvent()
        {
            const decimal fee = 50m;
            var start = LocalTime(Today.AddDays(-3), 18);
            var end = start.AddHours(4);
            var registered = LocalTime(Today.AddDays(-6), 14);
            var created = LocalTime(Today.AddDays(-8), 12);
            if (end > _now || created < HistoryStart) return;
            var pcs = _pcs.Where(p => p.Device.Hostname is "STD-01" or "STD-02" or "STD-03" or "STD-04" or "STD-05" or "STD-06" or "STD-07" or "STD-08")
                .OrderBy(p => p.Device.Hostname).ToList();
            if (pcs.Count != 8 || pcs.Any(p => Blocked(p, start, end) != null)) return;
            var winner = _users.Single(u => u.Username == "vika_gg");
            var players = new List<UserEntity> { winner };
            players.AddRange(_regulars.Where(u => u != winner && IsFree(u, start, end)).OrderBy(_ => _rng.Next()).Take(7));
            if (players.Count != 8 || !IsFree(winner, start, end)) return;

            const long id = 1;
            var e = new EventEntity
            {
                Id = id, Name = FinishedEventName, Status = EventStatuses.Finished, ZoneId = pcs[0].Device.ZoneId, StartUtc = start, EndUtc = end,
                DurationMinutes = 240, StartDay = DateOnly.FromDateTime(Today.AddDays(-3)).DayNumber, PriceMode = EventPriceModes.Free,
                EntryFee = fee, TeamSize = 2, StartedUtc = start, EndedUtc = end, CreatedUtc = created, UpdatedUtc = end,
                CreatedById = Manager.Id, CreatedByUsername = Manager.Username
            };
            EventRows.Add(e);
            EventRows.AddRange(pcs.Select(p => new EventDeviceEntity { EventId = id, DeviceId = p.Device.Id }));
            Audit(created, Manager, AuditActions.EventCreated, "event", () => "1",
                $"Event #1: 8 PC(s) (chosen PCs), {Fmt(start, "dd.MM HH:mm")}-{Fmt(end, "HH:mm")}, PC time free, entry fee {fee} ₴, teams of 2",
                new { id, pcs = pcs.Select(p => p.Device.Hostname), startUtc = start, minutes = 240, priceMode = "free", price = 0m, entryFee = fee, teamSize = 2 });

            var teams = new List<EventTeamEntity>();
            long playerId = 0;
            for (var t = 0; t < 4; t++)
            {
                var at = registered.AddMinutes(t * 47);
                var team = new EventTeamEntity
                {
                    Id = t + 1, EventId = id, Name = TeamNames[t], CaptainUserId = players[2 * t].Id, Seed = t + 1, CreatedUtc = at, CreatedByUsername = "demo-cashier"
                };
                teams.Add(team);
                EventRows.Add(team);
                for (var k = 0; k < 2; k++)
                {
                    var user = players[2 * t + k];
                    var pc = pcs[2 * t + k];
                    var session = AddSession(pc, start, end, 0m, true, null, 240, user, "ended");
                    session.PaymentMethod = PaymentMethods.Free;
                    Reserve(pc, start, end);
                    Playing(user, start, end);
                    EventRows.Add(new EventPlayerEntity
                    {
                        Id = ++playerId, EventId = id, TeamId = team.Id, UserId = user.Id, DeviceId = pc.Device.Id, SessionId = session.Id,
                        Fee = fee, FeeStatus = EventFeeStatuses.Kept, CreatedUtc = at
                    });
                    // The fee: held at the registration (topped up first when the balance is too low), kept at the end
                    var holdAt = at.AddSeconds(k);
                    _playerEvents.Add((holdAt, 1, () =>
                    {
                        if (_wallets[user.Id].Balance < fee) TopUp(user, 200m, PaymentMethods.Cash, holdAt.AddMilliseconds(-10));
                        Post(user, holdAt, LedgerTypes.EventFeeHold, PaymentMethods.Balance, fee, -fee, 0m, 0m,
                            $"Внесок за участь: подія №1 ({Fmt(start, "dd.MM HH:mm")}) — {fee} ₴", eventId: id, deviceId: pc.Device.Id);
                    }));
                    _playerEvents.Add((end, 1, () => Post(user, end, LedgerTypes.EventFeeKept, PaymentMethods.Balance, fee, 0m, 0m, 0m,
                        $"Внесок за участь у події №1 — подія відбулася, {fee} ₴", actor: null, eventId: id, deviceId: pc.Device.Id)));
                    var value = TariffCalculator.QuoteFixed(pc.Pricing, start, 240, _zone).Total;
                    Audit(start, null, AuditActions.SessionFreeTime, "session", () => session.Id.ToString(),
                        $"Free time on {pc.Device.Hostname}: event #1 until {Fmt(end, "HH:mm")}, worth {value} ₴: event #1",
                        new { pc.Device.Hostname, minutes = 240, value, reason = "event #1", player = user.Username });
                }
                Audit(at, Cashier, AuditActions.EventTeamRegistered, "event", () => "1",
                    $"Event #1: team #{team.Id} registered: @{players[2 * t].Username}, @{players[2 * t + 1].Username} on {pcs[2 * t].Device.Hostname}, {pcs[2 * t + 1].Device.Hostname}, entry fee {fee} ₴ x 2 held",
                    new { eventId = id, team = team.Id, players = new[] { players[2 * t].Username, players[2 * t + 1].Username }, fee });
            }

            // Bracket: 1 v 4 and 2 v 3, the final; team 1 (vika_gg) wins
            var matches = Bracket.Create(teams.Select(t => t.Id).ToList());
            Bracket.SetResult(matches, 1, 0, 1, 2, 1);
            Bracket.SetResult(matches, 1, 1, 3, 0, 2);
            Bracket.SetResult(matches, 2, 0, 1, 2, 0);
            var decided = new[] { start.AddMinutes(75), start.AddMinutes(80), end.AddMinutes(-20) };
            var n = 0;
            foreach (var m in matches)
            {
                var at = m.Round == 1 ? decided[m.Slot] : decided[2];
                EventRows.Add(new EventMatchEntity
                {
                    Id = ++n, EventId = id, Round = m.Round, Slot = m.Slot, TeamAId = m.TeamA, TeamBId = m.TeamB, WinnerTeamId = m.Winner,
                    ScoreA = m.ScoreA, ScoreB = m.ScoreB, DecidedUtc = at, DecidedByUsername = "demo-cashier"
                });
                Audit(at, Cashier, AuditActions.EventResult, "event", () => "1",
                    $"Event #1: match {m.Round}.{m.Slot + 1}: team #{m.Winner} won ({m.ScoreA}:{m.ScoreB})" + (m.Round == 2 ? "; team #1 won the event" : ""),
                    new { eventId = id, round = m.Round, slot = m.Slot, winner = m.Winner, m.ScoreA, m.ScoreB });
            }
            e.WinnerTeamId = Bracket.Champion(matches);
            Audit(start.AddMinutes(-5), Manager, AuditActions.EventBracketCreated, "event", () => "1", "Event #1: bracket of 4 teams, 2 round(s), in registration order",
                new { eventId = id, teams = teams.Select(t => t.Id), shuffle = false, rounds = 2 });
            Audit(start, null, AuditActions.EventStarted, "event", () => "1", $"Event #1 started: 8 PC(s) unlocked until {Fmt(end, "HH:mm")}",
                new { id, endUtc = end, started = pcs.Select(p => p.Device.Hostname) });
            Audit(end, null, AuditActions.EventFinished, "event", () => "1", $"Event #1 finished at its end, entry fees {fee * 8} ₴ kept",
                new { id, early = false, kept = fee * 8, winnerTeam = 1 });
        }

        private string Fmt(DateTimeOffset utc, string format) => TimeZoneInfo.ConvertTime(utc, _zone).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An upcoming event in two days (spec area 9), booked through the real service: a 2×2 tournament on the VIP PCs at
    /// 19:00 for 3 hours, PC time 60 per player (paid at the start), an entry fee of 100 per player; two teams are
    /// registered by the demo cashier (their fees held), one place is free.
    /// </summary>
    public static async Task SeedUpcomingEventAsync(CmsDbContext db, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var clock = new ClubClock(new FixedTimeProvider(now), zone);
        var manager = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Username == "demo-manager", ct);
        var cashier = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Username == "demo-cashier", ct);
        var vip = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Name == "VIP", ct);
        if (manager == null || cashier == null || vip == null) return;
        var audit = new AuditLogger(db, new HttpContextAccessor());
        var day = clock.ToLocal(now).Date.AddDays(2);
        var created = await new EventService(db, new EfDeviceRepository(db), clock, audit, auditActor: manager).CreateAsync(new CreateEventRequest(
            UpcomingEventName, vip.Id, null, day.ToString("yyyy-MM-dd"), "19:00", 180, EventPriceModes.Fixed, 60m, 100m, 2));
        db.ChangeTracker.Clear();
        if (!created.Ok) return;
        var players = (await db.Users.AsNoTracking().Where(u => u.Username != DemoOptions.PlayerLogin).ToListAsync(ct))
            .Where(u => u.Balance >= 150m).OrderByDescending(u => u.Balance).ThenBy(u => u.Username).Take(4).ToList();
        var service = new EventService(db, new EfDeviceRepository(db), clock, audit, auditActor: cashier);
        for (var t = 0; t + 1 < players.Count; t += 2)
        {
            await service.RegisterAsync(created.Value!.Id, new RegisterTeamRequest(TeamNames[4 + t / 2], new[] { players[t].Id, players[t + 1].Id }));
            db.ChangeTracker.Clear();
        }
    }
}
