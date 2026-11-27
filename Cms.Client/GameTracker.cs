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
///    game still runs, that one is reported again;
///  - a lock screen that started again (watchdog, update) knows nothing of the games of the old process: it takes over
///    the current game of the session from the server (Adopt) and follows it by its ProcessName, and it shows that game
///    on the top bar (Showing) until it stops (Windows test 2026-11-27 night, defect 6).
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
        /// <summary>Taken over from the server: it runs already, so two checks without its process mean it stopped.</summary>
        public bool Adopted { get; init; }
    }

    private readonly List<Tracked> _games = new();
    private readonly HashSet<Guid> _stopped = new();
    private Guid? _current;

    /// <summary>The game that this lock screen reported last as started and that still runs, or null.</summary>
    public Guid? Current => _current;

    /// <summary>PIDs of the processes the lock screen started (for the teardown).</summary>
    public IReadOnlyList<int> StartedPids => _games.Where(g => g.Pid.HasValue).Select(g => g.Pid!.Value).ToList();

    public IReadOnlyList<Guid> Running => _games.Select(g => g.Game.Id).ToList();

    /// <summary>The lock screen started an entry (pid: the process it started, null for shell links).</summary>
    public GameEvent Started(CatalogGame game, int? pid, DateTimeOffset now)
    {
        _games.RemoveAll(g => g.Game.Id == game.Id);
        _games.Add(new Tracked { Game = game, Pid = pid, StartedUtc = now });
        _stopped.Remove(game.Id);
        _current = game.Id;
        return new GameEvent(LauncherEvent.GameStarted, game.Id, now);
    }

    /// <summary>
    /// The game to show as "Now: ..." on the top bar: the current game of this lock screen, else the game of the session on
    /// the server (serverGame) unless this lock screen reported it stopped (the server learns that a few seconds later).
    /// </summary>
    public Guid? Showing(Guid? serverGame) =>
        _current ?? (serverGame is { } g && !_stopped.Contains(g) ? g : null);

    /// <summary>
    /// The server says that this game of the session runs, but this lock screen did not start it (it started again): follow
    /// it by its ProcessName from now on, so its stop is reported. False when it is followed already, when this lock screen
    /// reported it stopped, or when the entry has no ProcessName (then it is only shown).
    /// </summary>
    public bool Adopt(CatalogGame game, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(game.ProcessName) || _stopped.Contains(game.Id) || _games.Any(g => g.Game.Id == game.Id)) return false;
        _games.Add(new Tracked { Game = game, StartedUtc = now, Adopted = true });
        _current ??= game.Id;
        return true;
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
            var stopped = g.Seen || g.Adopted ? g.Misses >= 2 : now - g.StartedUtc > StartTimeout;
            if (!stopped) continue;
            _games.Remove(g);
            _stopped.Add(g.Game.Id);
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
        foreach (var g in _games) _stopped.Add(g.Game.Id);
        _games.Clear();
        _current = null;
        return events;
    }
}
