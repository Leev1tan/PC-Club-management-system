using Cms.Shared;

namespace Cms.Client;

/// <summary>An event for the server: a catalog entry started or stopped on this PC.</summary>
public sealed record GameEvent(string Type, Guid GameId, DateTimeOffset AtUtc);

/// <summary>
/// Follows the games that the lock screen started, from the process list of the player's session:
///  - a game counts as running while the started process lives, or a process with its ProcessName
///    (Steam and launcher links start the game through another program);
///  - it stops when it was seen and is gone for two checks in a row, or when it never appeared within
///    the start timeout (a failed start);
///  - the server keeps one current game per PC, so when the current one stops while another started
///    game still runs, that one is reported again.
/// The started PIDs are also the processes that the teardown kills at the end of the session.
/// </summary>
public sealed class GameTracker
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);

    private sealed class Tracked
    {
        public required CatalogGame Game { get; init; }
        public int? Pid { get; init; }
        public DateTimeOffset StartedUtc { get; init; }
        public bool Seen { get; set; }
        public int Misses { get; set; }
    }

    private readonly List<Tracked> _games = new();
    private Guid? _current;

    /// <summary>PIDs of the processes the lock screen started (for the teardown).</summary>
    public IReadOnlyList<int> StartedPids => _games.Where(g => g.Pid.HasValue).Select(g => g.Pid!.Value).ToList();

    public IReadOnlyList<Guid> Running => _games.Select(g => g.Game.Id).ToList();

    /// <summary>The lock screen started an entry (pid: the process it started, null for shell links).</summary>
    public GameEvent Started(CatalogGame game, int? pid, DateTimeOffset now)
    {
        _games.RemoveAll(g => g.Game.Id == game.Id);
        _games.Add(new Tracked { Game = game, Pid = pid, StartedUtc = now });
        _current = game.Id;
        return new GameEvent(LauncherEvent.GameStarted, game.Id, now);
    }

    /// <summary>Checks the process list; returns the events to report (stops, and a running game reported again).</summary>
    public IReadOnlyList<GameEvent> Update(IReadOnlyList<ProcessInfo> processes, int sessionId, DateTimeOffset now)
    {
        var events = new List<GameEvent>();
        var mine = processes.Where(p => p.SessionId == sessionId).ToList();
        foreach (var g in _games.ToList())
        {
            var alive = (g.Pid is { } pid && mine.Any(p => p.Pid == pid)) ||
                        (g.Game.ProcessName is { Length: > 0 } name && mine.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (alive)
            {
                g.Seen = true;
                g.Misses = 0;
                continue;
            }
            g.Misses++;
            var stopped = g.Seen ? g.Misses >= 2 : now - g.StartedUtc > StartTimeout;
            if (!stopped) continue;
            _games.Remove(g);
            events.Add(new GameEvent(LauncherEvent.GameStopped, g.Game.Id, now));
            if (_current == g.Game.Id)
            {
                _current = null;
                var other = _games.Where(x => x.Seen).OrderByDescending(x => x.StartedUtc).FirstOrDefault();
                if (other != null)
                {
                    _current = other.Game.Id;
                    events.Add(new GameEvent(LauncherEvent.GameStarted, other.Game.Id, now));
                }
            }
        }
        return events;
    }

    /// <summary>The session ended: every tracked game stops (the teardown kills them).</summary>
    public IReadOnlyList<GameEvent> StopAll(DateTimeOffset now)
    {
        var events = _games.Select(g => new GameEvent(LauncherEvent.GameStopped, g.Game.Id, now)).ToList();
        _games.Clear();
        _current = null;
        return events;
    }
}
