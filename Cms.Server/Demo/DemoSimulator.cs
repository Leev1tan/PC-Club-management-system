using System.Text.Json;
using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>
/// Plays the part of the Windows agents for the seeded demo PCs. It uses the same
/// repository calls as a real agent (heartbeat, poll commands, ack), so the admin UI
/// sees exactly what it would see with real PCs:
///  - heartbeats every 5 s (one PC stays offline on purpose);
///  - picks up queued commands and acknowledges them after a short, random delay;
///  - "restart" takes the PC offline for about 30 s, then it comes back;
///  - an autopilot starts sessions so the club map keeps changing: walk-in guests pay in cash or by
///    card, sometimes a registered player pays from the balance, sometimes a package is bought;
///  - now and then a session is paused for a few minutes and resumed.
/// Sessions go through Billing/SessionService, so prices, ledger rows and commands follow the real rules.
/// Not thread-safe: called from one background loop only.
/// </summary>
public sealed class DemoSimulator
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestartDowntime = TimeSpan.FromSeconds(30); // > 20 s "offline" threshold
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

    public DemoSimulator(ILogger<DemoSimulator> logger, DeviceRuntimeState runtime, Random? rng = null, ClubClock? clock = null)
    {
        _logger = logger;
        _runtime = runtime;
        _rng = rng ?? new Random();
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
    }

    public async Task TickAsync(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now, CancellationToken ct = default)
    {
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
            if (pc.Info.AlwaysOffline) continue;
            if (pc.RestartingUntilUtc is { } until)
            {
                if (now < until) continue;
                pc.RestartingUntilUtc = null; // back from the restart
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
                Uptime: now - pc.BootedUtc));
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
                return ("done", "Windows user logged off");
            case CommandTypes.Message:
                return ("done", "Message shown: " + Truncate(ReadMessageText(cmd.Payload), 200));
            case CommandTypes.Restart:
                pc.RestartingUntilUtc = now + RestartDowntime + TimeSpan.FromSeconds(_rng.Next(0, 10));
                _logger.LogInformation("Demo: {Host} restarts", pc.Info.Hostname);
                return ("done", "Restart scheduled in 5 seconds");
            default:
                return ("ignored", "Unknown command");
        }
    }

    private SessionService Sessions(CmsDbContext db, IDeviceRepository repo, DateTimeOffset now) =>
        new(db, repo, new ClubClock(new FixedTimeProvider(now), _zone));

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
        var free = online.Where(p => !busy.Contains(p.Info.Id)).ToList();
        var occupancy = (double)(online.Count - free.Count) / online.Count;
        if (free.Count == 0 || occupancy >= TargetOccupancy) return;

        var pc = free[_rng.Next(free.Count)];
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == pc.Info.Id, ct);
        if (device == null) return;

        Guid? packageId = null;
        string? packageName = null;
        if (_rng.NextDouble() < 0.35 && device.ZoneId.HasValue)
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
        UserEntity? player = null;
        if (_rng.NextDouble() < 0.25)
        {
            var players = await db.Users.AsNoTracking().ToListAsync(ct);
            if (players.Count > 0) player = players[_rng.Next(players.Count)];
        }
        var desk = _rng.NextDouble() < 0.7 ? PaymentMethods.Cash : PaymentMethods.Card;

        var service = Sessions(db, repo, now);
        var result = await service.StartAsync(new StartSessionRequest(device.Id, null, player?.Id, packageId.HasValue ? null : minutes,
            packageId, player != null ? PaymentMethods.Balance : desk));
        if (!result.Ok && player != null)
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

        public bool IsUp(DateTimeOffset now) =>
            !Info.AlwaysOffline && (RestartingUntilUtc is not { } until || now >= until);
    }

    private sealed record PendingAck(Guid DeviceId, CommandView Command, DateTimeOffset DueUtc);
}
