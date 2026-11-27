using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Devices;
using Cms.Server.Pos;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Cms.Server.Shifts;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>
/// Plays the part of the Windows agents for the seeded demo PCs. It uses the same
/// repository calls as a real agent (heartbeat, poll commands, ack), so the admin UI
/// sees exactly what it would see with real PCs:
///  - heartbeats every 5 s (one PC stays offline on purpose);
///  - picks up queued commands and acknowledges them after a short, random delay;
///  - "restart" takes the PC offline for about 30 s, then it comes back; "shutdown" takes it offline
///    until a Wake-on-LAN (simulated, no network traffic) or for 3 minutes; a wake starts the always
///    offline PC for 10 minutes;
///  - an autopilot starts sessions so the club map keeps changing: walk-in guests pay in cash or by
///    card, sometimes a registered player pays from the balance, sometimes a package is bought;
///  - now and then a session is paused for a few minutes and resumed;
///  - the bar sells a drink or a snack every minute or so (cash, card or a player's balance), and a
///    sold-out product gets a delivery; desk payments happen only while a shift is open, and when a
///    visitor closes the shift the demo cashier opens a new one a few minutes later;
///  - once in a while a PC reports an emergency staff PIN unlock (audit log);
///  - players start games of the catalog in running sessions and sometimes switch (current game, top games);
///  - players and guests order from the bar on the lock screen; the cashier prepares and delivers the orders.
/// Sessions, sales and shifts go through the real services, so prices, ledger rows and commands follow the real rules.
/// Not thread-safe: called from one background loop only.
/// </summary>
public sealed class DemoSimulator
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestartDowntime = TimeSpan.FromSeconds(30); // > 20 s "offline" threshold
    private static readonly TimeSpan ShutdownDowntime = TimeSpan.FromMinutes(3);   // comes back by itself: the public demo stays whole
    private static readonly TimeSpan WakeAwake = TimeSpan.FromMinutes(10);        // the always offline PC after a wake
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _wakes = new();
    private static readonly TimeSpan HeartbeatPruneInterval = TimeSpan.FromMinutes(10);
    private const double TargetOccupancy = 0.65;
    private const int MaxPaused = 2;

    private readonly ILogger<DemoSimulator> _logger;
    private readonly DeviceRuntimeState _runtime;
    private readonly Random _rng;
    private readonly Dictionary<Guid, PcState> _pcs = new();
    private readonly List<PendingAck> _acks = new();
    private readonly Dictionary<Guid, DateTimeOffset> _resumeAt = new();
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _nextAutopilotUtc;
    private DateTimeOffset _nextPauseUtc;
    private DateTimeOffset _nextPruneUtc;
    // Bar, shifts and PIN unlocks use their own random numbers, so the session autopilot behaves as before
    private readonly Random _pos;
    private DateTimeOffset _nextBarUtc;
    private DateTimeOffset _nextShiftCheckUtc;
    private DateTimeOffset? _noShiftSinceUtc;
    private DateTimeOffset _nextUnlockUtc;
    private static readonly TimeSpan ReopenShiftAfter = TimeSpan.FromMinutes(3);
    // Games use their own random numbers too
    private readonly Random _games = new();
    private DateTimeOffset _nextGamesUtc;
    // Reservations: who arrives when (null = a no-show), and new bookings now and then
    private readonly Random _resv;
    private readonly Dictionary<long, DateTimeOffset?> _arrivals = new();
    private DateTimeOffset _nextReservationsUtc;
    private DateTimeOffset _nextBookingUtc;
    // Bar orders from the lock screens: placed now and then, handled by the cashier on duty a few minutes later
    private readonly Random _ord;
    private DateTimeOffset _nextOrderUtc;
    private DateTimeOffset _nextOrderDeskUtc;

    private readonly Cms.Server.Accounts.GameAccountVault? _vault;

    public DemoSimulator(ILogger<DemoSimulator> logger, DeviceRuntimeState runtime, Random? rng = null, ClubClock? clock = null, Random? posRng = null,
        Random? reservationRng = null, Random? orderRng = null, Cms.Server.Accounts.GameAccountVault? vault = null)
    {
        _vault = vault;
        _resv = reservationRng ?? new Random();
        _ord = orderRng ?? new Random();
        _logger = logger;
        _runtime = runtime;
        _rng = rng ?? new Random();
        _pos = posRng ?? new Random();
        _zone = clock?.Zone ?? ClubTimeZone.Default;
    }

    public int PcCount => _pcs.Count;

    /// <summary>True while the PC sends heartbeats (not the offline PC, not restarting).</summary>
    public bool IsOnline(Guid deviceId, DateTimeOffset now) =>
        _pcs.TryGetValue(deviceId, out var pc) && pc.IsUp(now);

    public void Load(DemoSeedResult seed, DateTimeOffset now)
    {
        _pcs.Clear();
        _acks.Clear();
        foreach (var pc in seed.Pcs)
        {
            _pcs[pc.Id] = new PcState(pc)
            {
                NextHeartbeatUtc = now.AddMilliseconds(_rng.Next(0, 5000)),
                BootedUtc = now.AddMinutes(-_rng.Next(30, 600))
            };
            if (pc.Locked.HasValue) _runtime.SetLocked(pc.Id, pc.Locked.Value);
        }
        _resumeAt.Clear();
        foreach (var id in seed.PausedSessions ?? Array.Empty<Guid>())
            _resumeAt[id] = now.AddSeconds(_rng.Next(150, 240));
        _nextAutopilotUtc = now.AddSeconds(20);
        _nextPauseUtc = now.AddSeconds(_rng.Next(60, 120));
        _nextPruneUtc = now + HeartbeatPruneInterval;
        _nextBarUtc = now.AddSeconds(_pos.Next(30, 60));
        _nextShiftCheckUtc = now.AddSeconds(30);
        _noShiftSinceUtc = null;
        _nextUnlockUtc = now.AddMinutes(_pos.Next(40, 90));
        _nextGamesUtc = now.AddSeconds(_games.Next(15, 30));
        _arrivals.Clear();
        _nextReservationsUtc = now.AddSeconds(10);
        _nextBookingUtc = now.AddMinutes(_resv.Next(8, 15));
        _nextOrderUtc = now.AddSeconds(_ord.Next(30, 90));
        _nextOrderDeskUtc = now.AddSeconds(45);
    }

    /// <summary>A Wake-on-LAN of the admin UI (any thread): handled in the next tick.</summary>
    public void Wake(Guid deviceId) => _wakes.Enqueue(deviceId);

    private void HandleWakes(DateTimeOffset now)
    {
        while (_wakes.TryDequeue(out var id))
        {
            if (!_pcs.TryGetValue(id, out var pc) || pc.IsUp(now)) continue;
            var boot = now.AddSeconds(_rng.Next(15, 26));
            if (pc.ShutDownUntilUtc.HasValue) pc.ShutDownUntilUtc = boot;
            if (pc.Info.AlwaysOffline)
            {
                (pc.AwakeFromUtc, pc.AwakeUntilUtc) = (boot, boot + WakeAwake);
                pc.Booting = true;
            }
            _logger.LogInformation("Demo: {Host} wakes up (Wake-on-LAN)", pc.Info.Hostname);
        }
    }

    public async Task TickAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct = default)
    {
        HandleWakes(now);
        SendHeartbeats(repo, now);
        await PickUpCommandsAsync(db, repo, now, ct);
        ExecuteDueCommands(repo, now);

        if (now >= _nextAutopilotUtc)
        {
            _nextAutopilotUtc = now.AddSeconds(_rng.Next(15, 40));
            await StartWalkInSessionAsync(db, repo, now, ct);
        }

        await ResumeDueSessionsAsync(db, repo, now);
        if (now >= _nextPauseUtc)
        {
            _nextPauseUtc = now.AddSeconds(_rng.Next(90, 180));
            await PauseOneSessionAsync(db, repo, now, ct);
        }

        if (now >= _nextShiftCheckUtc)
        {
            _nextShiftCheckUtc = now.AddSeconds(20);
            await KeepAShiftOpenAsync(db, now, ct);
        }
        if (now >= _nextBarUtc)
        {
            _nextBarUtc = now.AddSeconds(_pos.Next(35, 95));
            await SellAtTheBarAsync(db, now, ct);
        }
        if (now >= _nextGamesUtc)
        {
            _nextGamesUtc = now.AddSeconds(_games.Next(20, 45));
            await PlayGamesAsync(db, now, ct);
        }
        if (now >= _nextReservationsUtc)
        {
            _nextReservationsUtc = now.AddSeconds(_resv.Next(15, 25));
            await CheckInArrivalsAsync(db, repo, now, ct);
        }
        if (now >= _nextOrderUtc)
        {
            _nextOrderUtc = now.AddSeconds(_ord.Next(120, 240));
            await PlaceOrderAsync(db, now, ct);
        }
        if (now >= _nextOrderDeskUtc)
        {
            _nextOrderDeskUtc = now.AddSeconds(30);
            await HandleOrdersAsync(db, now, ct);
        }
        if (now >= _nextBookingUtc)
        {
            _nextBookingUtc = now.AddMinutes(_resv.Next(8, 15));
            await BookAsync(db, repo, now, ct);
        }
        if (now >= _nextUnlockUtc)
        {
            _nextUnlockUtc = now.AddMinutes(_pos.Next(60, 120));
            await ReportEmergencyUnlockAsync(db, now, ct);
        }

        if (now >= _nextPruneUtc)
        {
            // The UI only uses Devices.LastSeenUtc; keep the heartbeat table small
            _nextPruneUtc = now + HeartbeatPruneInterval;
            await db.Heartbeats.ExecuteDeleteAsync(ct);
        }
    }

    private void SendHeartbeats(IDeviceRepository repo, DateTimeOffset now)
    {
        foreach (var pc in _pcs.Values)
        {
            if (!pc.IsUp(now)) continue;
            if (pc.RestartingUntilUtc.HasValue || pc.ShutDownUntilUtc.HasValue || pc.Booting)
            {
                // back from a restart, a shutdown or a wake
                pc.RestartingUntilUtc = null;
                pc.ShutDownUntilUtc = null;
                pc.Booting = false;
                pc.BootedUtc = now;
                pc.NextHeartbeatUtc = now;
            }
            if (now < pc.NextHeartbeatUtc) continue;

            var inUse = _runtime.IsLocked(pc.Info.Id) == false;
            repo.Heartbeat(pc.Info.DeviceKey, new DeviceHeartbeatRequest(
                CpuPercent: inUse ? _rng.Next(35, 96) : _rng.Next(1, 9),
                MemPercent: inUse ? _rng.Next(45, 85) : _rng.Next(18, 30),
                ActiveUser: inUse ? "player" : "kiosk",
                Ip: pc.Info.Ip,
                Uptime: now - pc.BootedUtc,
                Mac: pc.Info.Mac));
            pc.NextHeartbeatUtc = now + HeartbeatInterval;
        }
    }

    private async Task PickUpCommandsAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct)
    {
        var deviceIds = await db.Commands.AsNoTracking()
            .Where(c => c.Status == "pending")
            .Select(c => c.DeviceId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var deviceId in deviceIds)
        {
            if (!_pcs.TryGetValue(deviceId, out var pc) || !pc.IsUp(now)) continue;
            foreach (var cmd in repo.PollCommands(deviceId, 10).ToList())
                _acks.Add(new PendingAck(deviceId, cmd, now.AddMilliseconds(_rng.Next(300, 1500))));
        }
    }

    private void ExecuteDueCommands(IDeviceRepository repo, DateTimeOffset now)
    {
        var due = _acks.Where(a => a.DueUtc <= now).OrderBy(a => a.DueUtc).ToList();
        foreach (var ack in due)
        {
            _acks.Remove(ack);
            if (!_pcs.TryGetValue(ack.DeviceId, out var pc)) continue;
            var (status, result) = Execute(pc, ack.Command, now);
            repo.AckCommand(ack.DeviceId, ack.Command.Id, new AckCommandRequest(status, result));
        }
    }

    private (string Status, string? Result) Execute(PcState pc, CommandView cmd, DateTimeOffset now)
    {
        switch (cmd.Type?.ToLowerInvariant())
        {
            case CommandTypes.Lock:
                return ("done", "Lock screen shown");
            case CommandTypes.Unlock:
                return ("done", "Unlocked");
            case CommandTypes.SessionSet:
                return ("done", "Session end time updated");
            case CommandTypes.Logoff:
                return ("done", "Sent to the lock screen: it logs off the player's Windows session");
            case CommandTypes.Message:
                return ("done", "Message shown: " + Truncate(ReadMessageText(cmd.Payload), 200));
            case CommandTypes.Restart:
                pc.RestartingUntilUtc = now + RestartDowntime + TimeSpan.FromSeconds(_rng.Next(0, 10));
                _logger.LogInformation("Demo: {Host} restarts", pc.Info.Hostname);
                return ("done", "Restart scheduled in 5 seconds");
            case CommandTypes.Shutdown:
                pc.ShutDownUntilUtc = now + ShutdownDowntime;
                _logger.LogInformation("Demo: {Host} shuts down", pc.Info.Hostname);
                return ("done", "Shutdown scheduled in 5 seconds");
            default:
                return ("ignored", "Unknown command");
        }
    }

    private SessionService Sessions(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, StaffActor? actor = null) =>
        new(db, repo, new ClubClock(new FixedTimeProvider(now), _zone), actor: actor);

    private ClubClock ClockAt(DateTimeOffset now) => new(new FixedTimeProvider(now), _zone);

    /// <summary>The cashier on duty (opener of the open shift) takes the money at the desk.</summary>
    private static StaffActor? OnDuty(ShiftEntity? shift) => shift == null ? null : new StaffActor(shift.OpenedById, shift.OpenedByUsername);

    /// <summary>
    /// When occupancy is low, someone takes a free PC: a guest paying 30 min - 3 h in cash or by card,
    /// a package (3 h, 5 h, morning or night pack - whatever is on sale now), or a registered player
    /// paying from the balance (bonus first). Falls back to a guest when the player's balance is too low.
    /// </summary>
    private async Task StartWalkInSessionAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct)
    {
        var busy = (await db.Sessions.AsNoTracking()
            .Where(s => s.Status == "active")
            .Select(s => s.DeviceId)
            .ToListAsync(ct)).ToHashSet();

        var online = _pcs.Values.Where(p => p.IsUp(now)).ToList();
        if (online.Count == 0) return;
        // A cashier sees "reserved at HH:MM" on the map and sells time only until the hold of the reservation
        var reserved = await ReservationMap.NextByPcAsync(db, ClockAt(now), ct);
        var hold = (await ReservationSettings.LoadAsync(db, ct)).HoldMinutes;
        double Room(Guid pc) => reserved.TryGetValue(pc, out var r) ? (r.StartUtc.AddMinutes(-hold) - now).TotalMinutes : double.MaxValue;
        var free = online.Where(p => !busy.Contains(p.Info.Id) && Room(p.Info.Id) >= 30).ToList();
        var occupancy = (double)(online.Count - free.Count) / online.Count;
        if (free.Count == 0 || occupancy >= TargetOccupancy) return;

        var pc = free[_rng.Next(free.Count)];
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == pc.Info.Id, ct);
        if (device == null) return;

        Guid? packageId = null;
        string? packageName = null;
        var room = Room(pc.Info.Id);
        if (_rng.NextDouble() < 0.35 && device.ZoneId.HasValue && room == double.MaxValue)
        {
            var onSale = (await db.Packages.AsNoTracking().Where(p => p.ZoneId == device.ZoneId && p.IsActive).ToListAsync(ct))
                .Where(p => PackageWindows.Evaluate(p, now, _zone).Available).ToList();
            if (onSale.Count > 0)
            {
                var package = onSale[_rng.Next(onSale.Count)];
                (packageId, packageName) = (package.Id, package.Name);
            }
        }
        var minutes = new[] { 30, 60, 60, 90, 120, 180 }[_rng.Next(6)];
        if (minutes > room) minutes = (int)(room / 30) * 30; // until the hold of the next reservation
        UserEntity? player = null;
        if (_rng.NextDouble() < 0.25)
        {
            var players = await db.Users.AsNoTracking().ToListAsync(ct);
            if (players.Count > 0) player = players[_rng.Next(players.Count)];
        }
        var desk = _rng.NextDouble() < 0.7 ? PaymentMethods.Cash : PaymentMethods.Card;
        // Money at the desk needs an open shift (when the club requires it); players can still pay from the balance
        var shift = await ShiftService.OpenShiftAsync(db, ct);
        var deskOpen = shift != null || !db.RequireOpenShift;
        if (!deskOpen && player == null) return;

        var service = Sessions(db, repo, now, OnDuty(shift));
        var result = await service.StartAsync(new StartSessionRequest(device.Id, null, player?.Id, packageId.HasValue ? null : minutes,
            packageId, player != null ? PaymentMethods.Balance : desk));
        if (!result.Ok && player != null && deskOpen)
        {
            db.ChangeTracker.Clear();
            result = await service.StartAsync(new StartSessionRequest(device.Id, null, null, packageId.HasValue ? null : minutes, packageId, desk));
        }
        db.ChangeTracker.Clear();
        if (!result.Ok)
        {
            _logger.LogDebug("Demo: walk-in on {Host} not started: {Error}", device.Hostname, result.Error!.Message);
            return;
        }
        _logger.LogInformation("Demo: {Who} takes {Host}: {What}", result.Value!.UserId.HasValue ? "a player" : "a walk-in guest",
            device.Hostname, packageName ?? $"{minutes} min");
    }

    /// <summary>Pauses one running prepaid session on an online PC for 1-3 minutes ("back in a moment").</summary>
    private async Task PauseOneSessionAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct)
    {
        if (_resumeAt.Count >= MaxPaused) return;
        var running = (await db.Sessions.AsNoTracking()
                .Where(s => s.Status == "active" && s.IsPrepaid && s.PausedUtc == null && s.EndUtc != null)
                .ToListAsync(ct))
            .Where(s => s.EndUtc!.Value > now.AddMinutes(10) && IsOnline(s.DeviceId, now))
            .ToList();
        if (running.Count == 0) return;
        var session = running[_rng.Next(running.Count)];
        var result = await Sessions(db, repo, now).PauseAsync(session.Id);
        db.ChangeTracker.Clear();
        if (!result.Ok) return;
        _resumeAt[session.Id] = now.AddSeconds(_rng.Next(60, 180));
        _logger.LogInformation("Demo: session {Id} paused", session.Id);
    }

    private async Task ResumeDueSessionsAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now)
    {
        foreach (var id in _resumeAt.Where(x => x.Value <= now).Select(x => x.Key).ToList())
        {
            _resumeAt.Remove(id);
            var result = await Sessions(db, repo, now).ResumeAsync(id);
            db.ChangeTracker.Clear();
            if (result.Ok) _logger.LogInformation("Demo: session {Id} resumed", id);
        }
    }

    /// <summary>A visitor closed the shift: after a few minutes the demo cashier opens the next one.</summary>
    private async Task KeepAShiftOpenAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (await ShiftService.OpenShiftAsync(db, ct) != null)
        {
            _noShiftSinceUtc = null;
            return;
        }
        _noShiftSinceUtc ??= now;
        if (now - _noShiftSinceUtc.Value < ReopenShiftAfter) return;
        var cashier = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Role == StaffRoles.Cashier && s.IsActive, ct);
        if (cashier == null) return;
        var result = await new ShiftService(db, ClockAt(now), actor: new StaffActor(cashier.Id, cashier.Username))
            .OpenAsync(DemoSeeder.ShiftFloat, "Нова зміна (демо)");
        db.ChangeTracker.Clear();
        _noShiftSinceUtc = null;
        if (result.Ok) _logger.LogInformation("Demo: {Cashier} opened shift #{Id}", cashier.Username, result.Value!.Id);
    }

    /// <summary>
    /// A walk-in guest buys one or two things at the bar (cash or card), or a player pays from the
    /// real balance; a sold-out product gets a delivery first.
    /// </summary>
    private async Task SellAtTheBarAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var shift = await ShiftService.OpenShiftAsync(db, ct);
        var deskOpen = shift != null || !db.RequireOpenShift;
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        var soldOut = products.Where(p => p.TrackStock && p.StockQuantity == 0).ToList();
        if (soldOut.Count > 0)
        {
            var product = soldOut[_pos.Next(soldOut.Count)];
            var manager = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Role == StaffRoles.Manager, ct);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await Stock.AddAsync(db, product.Id, DemoSeeder.DeliveryQuantity, ct);
                await Stock.RecordAsync(db, product.Id, StockMovementTypes.Restock, DemoSeeder.DeliveryQuantity,
                    manager == null ? null : new StaffActor(manager.Id, manager.Username), now,
                    shiftId: shift?.Id, reason: "Поставка від дистриб'ютора", ct: ct);
                await tx.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
            _logger.LogInformation("Demo: delivery of {Product}", product.Name);
            return;
        }

        var available = products.Where(p => !p.TrackStock || p.StockQuantity > 0).ToList();
        if (available.Count == 0) return;
        var lines = Enumerable.Range(0, _pos.Next(1, 3)).Select(_ => available[_pos.Next(available.Count)]).Distinct()
            .Select(p => new SaleLine(p.Id, 1)).ToList();
        UserEntity? player = null;
        if (_pos.NextDouble() < 0.2)
        {
            var players = await db.Users.AsNoTracking().ToListAsync(ct);
            if (players.Count > 0) player = players[_pos.Next(players.Count)];
        }
        if (player == null && !deskOpen) return;
        var method = player != null ? PaymentMethods.Balance : _pos.NextDouble() < 0.6 ? PaymentMethods.Cash : PaymentMethods.Card;

        var service = new SaleService(db, ClockAt(now), actor: OnDuty(shift));
        var result = await service.SellAsync(lines, method, player?.Id);
        db.ChangeTracker.Clear();
        if (!result.Ok && player != null && deskOpen)
        {
            result = await service.SellAsync(lines, PaymentMethods.Cash, null); // the balance is too low: pays in cash
            db.ChangeTracker.Clear();
        }
        if (result.Ok)
            _logger.LogInformation("Demo: bar receipt #{Id}, {Total} ₴ ({Method})", result.Value!.Id, result.Value.Total, result.Value.Method);
    }

    /// <summary>
    /// Running, not paused sessions on online PCs: a player without a game starts one of the zone's
    /// catalog (weighted like the seeded history); one who played for 40 minutes sometimes switches.
    /// </summary>
    private async Task PlayGamesAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var sessions = (await db.Sessions.AsNoTracking().Where(s => s.Status == "active" && s.PausedUtc == null).ToListAsync(ct))
            .Where(s => IsOnline(s.DeviceId, now) && !SessionService.IsDue(s, now)).ToList();
        if (sessions.Count == 0) return;
        var open = (await db.GamePlays.AsNoTracking().Where(p => p.EndUtc == null).ToListAsync(ct))
            .GroupBy(p => p.DeviceId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.StartUtc).First());
        var devices = await db.Devices.AsNoTracking().Select(d => new { d.Id, d.ZoneId }).ToDictionaryAsync(d => d.Id, d => d.ZoneId, ct);
        var zones = await db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.SortOrder, ct);
        var games = await db.Games.AsNoTracking().Where(g => g.IsActive).ToListAsync(ct);
        var gameZones = (await db.GameZones.AsNoTracking().ToListAsync(ct)).ToLookup(z => z.GameId, z => z.ZoneId);
        foreach (var session in sessions)
        {
            var current = open.TryGetValue(session.DeviceId, out var p) && p.SessionId == session.Id ? p : null;
            var start = current == null ? _games.NextDouble() < 0.5 : now - current.StartUtc > TimeSpan.FromMinutes(40) && _games.NextDouble() < 0.1;
            if (!start) continue;
            var zoneId = devices.TryGetValue(session.DeviceId, out var z) ? z : null;
            var zoneIndex = zoneId.HasValue && zones.TryGetValue(zoneId.Value, out var order) ? order : 0;
            var options = games.Where(g => g.Id != current?.GameId && (g.AllZones || (zoneId.HasValue && gameZones[g.Id].Contains(zoneId.Value))))
                .Select(g => (Game: g, Weight: DemoSeeder.GameWeight(g.Name, zoneIndex))).Where(x => x.Weight > 0).ToList();
            if (options.Count == 0) continue;
            var r = _games.NextDouble() * options.Sum(o => o.Weight);
            var pick = options[^1].Game;
            foreach (var o in options)
            {
                r -= o.Weight;
                if (r <= 0) { pick = o.Game; break; }
            }
            if (await GamePlays.StartAsync(db, session.DeviceId, pick.Id, now, ct))
            {
                _logger.LogDebug("Demo: {Game} started on {Device}", pick.Name, session.DeviceId);
                // A game that needs a club account: the simulated agent leases one and fetches the (fake) credentials once
                if (pick.AccountPlatform != null)
                {
                    db.ChangeTracker.Clear();
                    await DemoAccounts.LeaseAsync(db, _vault, session.DeviceId, pick.Id, now, _logger);
                }
            }
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Held reservations: most guests arrive between 10 minutes before and 8 minutes after the start and the cashier
    /// on duty checks them in (a player from the balance, a walk-in in cash or by card); about one in four never comes
    /// and the reservation job cancels it after the grace period.
    /// </summary>
    private async Task CheckInArrivalsAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct)
    {
        var clock = ClockAt(now);
        var settings = await ReservationSettings.LoadAsync(db, ct);
        var today = ReservationService.DayOf(now, clock);
        var held = (await db.Reservations.AsNoTracking()
                .Where(r => r.Status == ReservationStatuses.Confirmed && r.StartDay >= today - 1 && r.StartDay <= today + 1).ToListAsync(ct))
            .Where(r => ReservationHolds.IsHeld(r, now, settings)).ToList();
        if (held.Count == 0) return;
        var shift = await ShiftService.OpenShiftAsync(db, ct);
        var deskOpen = shift != null || !db.RequireOpenShift;
        var openerId = shift?.OpenedById;
        var cashier = (openerId.HasValue ? await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == openerId, ct) : null)
                      ?? await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Role == StaffRoles.Cashier, ct);
        foreach (var r in held)
        {
            if (!_arrivals.TryGetValue(r.Id, out var at))
            {
                at = _resv.NextDouble() < 0.75 ? r.StartUtc.AddMinutes(_resv.Next(-10, 9)) : null;
                _arrivals[r.Id] = at;
            }
            if (at == null || at > now) continue;
            var method = r.UserId.HasValue ? PaymentMethods.Balance : deskOpen ? (_resv.NextDouble() < 0.6 ? PaymentMethods.Cash : PaymentMethods.Card) : null;
            if (method == null) continue;
            var service = new ReservationService(db, repo, clock, new AuditLogger(db, new HttpContextAccessor()), auditActor: cashier);
            var result = await service.CheckInAsync(r.Id, new CheckInRequest(PaymentMethod: method));
            db.ChangeTracker.Clear();
            if (!result.Ok && r.UserId.HasValue && deskOpen)
            {
                result = await service.CheckInAsync(r.Id, new CheckInRequest(PaymentMethod: PaymentMethods.Cash)); // the balance is too low
                db.ChangeTracker.Clear();
            }
            if (result.Ok) _logger.LogInformation("Demo: reservation #{Id} checked in", r.Id);
            else _logger.LogDebug("Demo: reservation #{Id} not checked in: {Error}", r.Id, result.Error!.Code);
        }
    }

    /// <summary>A call to the desk: a reservation in 1 to 4 hours for a player (sometimes with a deposit) or a walk-in name.</summary>
    private async Task BookAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct)
    {
        var clock = ClockAt(now);
        var local = clock.ToLocal(now);
        var start = now.AddMinutes(_resv.Next(4, 16) * 15 - local.Minute % 15).AddSeconds(-local.Second);
        var minutes = new[] { 60, 90, 120, 180 }[_resv.Next(4)];
        var pc = await DemoReservations.FreePcAsync(db, clock, start, minutes, _resv, ct);
        if (pc == null) return;
        var cashier = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Role == StaffRoles.Cashier, ct);
        UserEntity? player = null;
        if (_resv.NextDouble() < 0.6)
        {
            var players = await db.Users.AsNoTracking().Where(u => u.Balance >= 100m && u.Username != DemoOptions.PlayerLogin).ToListAsync(ct);
            if (players.Count > 0) player = players[_resv.Next(players.Count)];
        }
        var startLocal = clock.ToLocal(start);
        var result = await new ReservationService(db, repo, clock, new AuditLogger(db, new HttpContextAccessor()), auditActor: cashier)
            .CreateAsync(new CreateReservationRequest(pc, null, startLocal.ToString("yyyy-MM-dd"), startLocal.ToString("HH:mm"), minutes,
                player?.Id, player == null ? DemoReservations.WalkInNames[_resv.Next(DemoReservations.WalkInNames.Length)] : null,
                player != null && _resv.NextDouble() < 0.5 ? 50m : 0m));
        db.ChangeTracker.Clear();
        if (result.Ok) _logger.LogInformation("Demo: reservation #{Id} booked for {Start:HH:mm}", result.Value!.Id, startLocal);
    }

    /// <summary>
    /// A player or a guest in a running session orders one or two things from the lock screen: a player pays from the
    /// real balance when it is enough, else at the desk; a guest at the desk. One open order per PC.
    /// </summary>
    private async Task PlaceOrderAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var withOrder = (await db.BarOrders.AsNoTracking().Where(o => o.Status == BarOrderStatuses.New || o.Status == BarOrderStatuses.Preparing)
            .Select(o => o.DeviceId).ToListAsync(ct)).ToHashSet();
        var sessions = (await db.Sessions.AsNoTracking().Where(s => s.Status == "active" && s.PausedUtc == null).ToListAsync(ct))
            .Where(s => IsOnline(s.DeviceId, now) && !SessionService.IsDue(s, now) && !withOrder.Contains(s.DeviceId)).ToList();
        if (sessions.Count == 0) return;
        var session = sessions[_ord.Next(sessions.Count)];
        var service = new BarOrderService(db, ClockAt(now), new AuditLogger(db, new HttpContextAccessor()));
        var menu = await service.MenuAsync(session.DeviceId);
        if (!menu.Available || menu.Products.Count == 0) return;
        var lines = Enumerable.Range(0, _ord.Next(1, 3)).Select(_ => menu.Products[_ord.Next(menu.Products.Count)]).DistinctBy(p => p.Id)
            .Select(p => new OrderLine(p.Id, 1)).ToList();
        var total = lines.Sum(l => menu.Products.First(p => p.Id == l.ProductId).Price);
        var payment = menu.CanPayFromBalance && menu.Balance >= total + 30m && _ord.NextDouble() < 0.7 ? BarOrderPayments.Balance : BarOrderPayments.Desk;
        var result = await service.PlaceAsync(session.DeviceId, lines, payment);
        db.ChangeTracker.Clear();
        if (result.Ok) _logger.LogInformation("Demo: bar order #{Id} from a PC ({Payment})", result.Value!.Id, payment);
    }

    /// <summary>The cashier on duty: an order older than 2 minutes is prepared, older than 4 minutes delivered (one in ten is cancelled).</summary>
    private async Task HandleOrdersAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.BarOrders.AsNoTracking().Where(o => o.Status == BarOrderStatuses.New || o.Status == BarOrderStatuses.Preparing).ToListAsync(ct);
        if (open.Count == 0) return;
        var shift = await ShiftService.OpenShiftAsync(db, ct);
        var deskOpen = shift != null || !db.RequireOpenShift;
        var openerId = shift?.OpenedById;
        var cashier = (openerId.HasValue ? await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == openerId, ct) : null)
                      ?? await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Role == StaffRoles.Cashier, ct);
        var service = new BarOrderService(db, ClockAt(now), new AuditLogger(db, new HttpContextAccessor()), auditActor: cashier);
        foreach (var order in open)
        {
            var age = now - order.CreatedUtc;
            BillingResult<BarOrderEntity> result;
            if (order.Status == BarOrderStatuses.New && age >= TimeSpan.FromMinutes(2)) result = await service.PrepareAsync(order.Id);
            else if (order.Status == BarOrderStatuses.Preparing && age >= TimeSpan.FromMinutes(4))
            {
                if (_ord.NextDouble() < 0.1) result = await service.CancelAsync(order.Id, "Закінчилося на складі");
                else if (order.Payment == BarOrderPayments.Desk && !deskOpen) continue;
                else result = await service.DeliverAsync(order.Id, _ord.NextDouble() < 0.6 ? PaymentMethods.Cash : PaymentMethods.Card);
                if (!result.Ok) { db.ChangeTracker.Clear(); result = await service.CancelAsync(order.Id, "Немає в наявності"); }
            }
            else continue;
            db.ChangeTracker.Clear();
            if (result.Ok) _logger.LogInformation("Demo: bar order #{Id} is {Status}", order.Id, result.Value!.Status);
        }
    }

    /// <summary>Somebody on staff used the PIN on the lock screen of a PC; the agent reports it.</summary>
    private async Task ReportEmergencyUnlockAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var online = _pcs.Values.Where(p => p.IsUp(now)).ToList();
        if (online.Count == 0) return;
        var pc = online[_pos.Next(online.Count)];
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == pc.Info.Id, ct);
        if (device == null) return;
        var request = new DeviceEventRequest(Guid.NewGuid(), LauncherEvent.StaffUnlock, now.AddMinutes(-_pos.Next(1, 10)),
            _pos.NextDouble() < 0.5 ? nameof(LockReason.LockedByAgent) : nameof(LockReason.AgentSilent), "player");
        await DeviceEvents.RecordAsync(db, new AuditLogger(db, new HttpContextAccessor()), device, request, now, ClockAt(now), ct);
        db.ChangeTracker.Clear();
        _logger.LogInformation("Demo: emergency PIN unlock reported by {Host}", device.Hostname);
    }

    private static string ReadMessageText(object? payload)
    {
        if (payload is JsonElement { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? string.Empty;
        if (payload is JsonElement { ValueKind: JsonValueKind.String } str)
            return str.GetString() ?? string.Empty;
        return payload?.ToString() ?? string.Empty;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";

    private sealed class PcState(SimulatedPc info)
    {
        public SimulatedPc Info { get; } = info;
        public DateTimeOffset NextHeartbeatUtc { get; set; }
        public DateTimeOffset BootedUtc { get; set; }
        public DateTimeOffset? RestartingUntilUtc { get; set; }
        /// <summary>Shut down: offline until this time (a wake brings it closer).</summary>
        public DateTimeOffset? ShutDownUntilUtc { get; set; }
        /// <summary>The always offline PC runs from AwakeFromUtc to AwakeUntilUtc after a wake.</summary>
        public DateTimeOffset? AwakeFromUtc { get; set; }
        public DateTimeOffset? AwakeUntilUtc { get; set; }
        public bool Booting { get; set; }

        public bool IsUp(DateTimeOffset now)
        {
            if (Info.AlwaysOffline && !(AwakeFromUtc <= now && now < AwakeUntilUtc)) return false;
            if (RestartingUntilUtc is { } restart && now < restart) return false;
            if (ShutDownUntilUtc is { } down && now < down) return false;
            return true;
        }
    }

    private sealed record PendingAck(Guid DeviceId, CommandView Command, DateTimeOffset DueUtc);
}
