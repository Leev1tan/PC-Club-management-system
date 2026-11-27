namespace Cms.Client;

/// <summary>
/// The supervisor of the lock screen in the player's Windows session (Windows test 2026-11-27, defect 5: a killed
/// Cms.Launcher left an empty screen, and an ending session could not lock the PC).
///  - The kiosk shell is "Cms.Launcher.exe --watchdog": a process without a window that starts the lock screen and starts
///    it again about 0.5 s after it ends (RestartPolicy).
///  - The lock screen starts a new watchdog when the watchdog is gone (its named mutex is gone), so one kill of either
///    process is repaired at once.
///  - A scheduled task of the kiosk user (at logon and every minute) starts "--watchdog" too; it ends at once while a
///    watchdog runs. It repairs a kill of both processes within a minute.
/// The names are per Windows session (Local\).
/// </summary>
public static class LauncherModes
{
    public const string WatchdogArg = "--watchdog";
    /// <summary>Held by the watchdog of this session while it runs.</summary>
    public const string WatchdogMutex = @"Local\ClubLauncher.Watchdog";
    /// <summary>Held by the lock screen of this session while it runs (one lock screen per session).</summary>
    public const string LockScreenMutex = @"Local\ClubLauncher.LockScreen";
    /// <summary>Exit code of a lock screen that found another one in its session.</summary>
    public const int ExitAlreadyRunning = 3;
    /// <summary>Exit code in kiosk mode when the Windows user is not the kiosk user (KioskAccount): an administrator's desktop.</summary>
    public const int ExitNotKioskUser = 4;

    public static bool IsWatchdog(IEnumerable<string>? args) =>
        args?.Any(a => string.Equals(a?.Trim(), WatchdogArg, StringComparison.OrdinalIgnoreCase)) == true;
}

/// <summary>
/// When the watchdog starts the lock screen again: 0.5 s after it ended; after more than 5 ends within 60 s (the lock
/// screen fails at once, for example a damaged install) 10 s, so a crash loop does not take the CPU. It never gives up.
/// </summary>
public sealed class RestartPolicy
{
    public static readonly TimeSpan NormalDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan CrashLoopDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public const int CrashLoopEnds = 5;

    private readonly Queue<DateTimeOffset> _ends = new();

    /// <summary>The lock screen ended now: the wait before it starts again.</summary>
    public TimeSpan OnExit(DateTimeOffset now)
    {
        _ends.Enqueue(now);
        while (_ends.Count > 0 && now - _ends.Peek() > Window) _ends.Dequeue();
        return _ends.Count > CrashLoopEnds ? CrashLoopDelay : NormalDelay;
    }
}

/// <summary>A process of the program, as the watchdog sees it.</summary>
public sealed record LauncherProcess(int Pid, int SessionId, string? Path, DateTimeOffset StartUtc);

public static class LockScreenProcesses
{
    /// <summary>A process younger than this is not adopted: it can be a watchdog that ends at once (one runs already).</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The lock screen that runs already in the watchdog's session (the oldest other process of the same program), or null.
    /// Only one watchdog runs per session, so every other process of the program there is a lock screen.
    /// </summary>
    public static int? PickRunningLockScreen(IEnumerable<LauncherProcess> processes, int ownPid, int sessionId, string? ownPath, DateTimeOffset now) =>
        processes
            .Where(p => p.Pid != ownPid && p.SessionId == sessionId && LauncherPath.Same(p.Path, ownPath) && now - p.StartUtc >= MinAge)
            .OrderBy(p => p.StartUtc)
            .Select(p => (int?)p.Pid)
            .FirstOrDefault();
}
