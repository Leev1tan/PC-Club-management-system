namespace Cms.Client;

/// <summary>What the agent knows about the lock screen of the PC (heartbeat field "lockScreen").</summary>
public static class LockScreenStates
{
    /// <summary>The lock screen answers and shows the lock state of the agent.</summary>
    public const string Ok = "ok";
    /// <summary>The lock screen answers; staff unlocked it with the PIN (Ctrl+Shift+U) while the agent says locked.</summary>
    public const string StaffUnlock = "staff_unlock";
    /// <summary>No lock screen status for 20 s (or never, 2 minutes after the agent start): the screen may be empty.</summary>
    public const string Silent = "silent";
    /// <summary>The agent started less than 2 minutes ago and has no status yet.</summary>
    public const string Unknown = "unknown";
    /// <summary>
    /// Kiosk PC: nobody is signed in, and the agent restarted Windows 3 times in 60 min without a sign-in of ClubUser
    /// (Cms.Client.KioskRestartPolicy, guard full). Staff must look at the PC. An older server ignores this value.
    /// </summary>
    public const string KioskSignInFailed = "kiosk_signin_failed";
}

/// <summary>A change of the lock screen that the agent writes to its log.</summary>
public enum LockScreenEvent
{
    /// <summary>The PC must be locked, but the lock screen does not answer (Warning; again every 15 minutes).</summary>
    SilentWhileLocked,
    /// <summary>The lock screen answers again after SilentWhileLocked (Information).</summary>
    Back
}

/// <summary>
/// The agent's view of the lock screen. The lock screen reports its state over the pipe every 5 s ("status"). When the
/// PC must be locked and no report came for 20 s, the agent logs it (Windows test 2026-11-27, defect 5: a killed lock
/// screen left the PC open and nobody knew). No clock and no I/O, so it is tested on any OS.
/// The newest lock wins (Windows test 2026-11-27 night, defect 5): a lock command ends the staff PIN unlock of the last
/// report at once, and a report made from an older lock generation (sent before the lock screen read the new state.json)
/// counts only as a sign of life, so the admin view does not show "unlocked" again for a few seconds after the lock.
/// </summary>
public sealed class LockScreenWatch
{
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(20);
    /// <summary>After the agent start the player may not be signed in yet: a lock screen that never answered counts after this.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RepeatWarning = TimeSpan.FromMinutes(15);

    private readonly DateTimeOffset _startUtc;
    private DateTimeOffset? _lastReportUtc;
    private DateTimeOffset? _warnedUtc;
    private bool _back;
    private long _generation;

    public LockScreenWatch(DateTimeOffset agentStartUtc) => _startUtc = agentStartUtc;

    /// <summary>The last report: the lock screen shows "locked".</summary>
    public bool ScreenLocked { get; private set; }
    /// <summary>The last report: a staff PIN unlock is active on the lock screen.</summary>
    public bool ScreenStaffUnlock { get; private set; }
    public DateTimeOffset? LastReportUtc => _lastReportUtc;

    /// <summary>The lock generation of the agent (AgentLock.Generation): the newest lock command.</summary>
    public long Generation => _generation;

    /// <summary>
    /// The agent has this lock generation now (at the start from state.json, and after every lock command). A higher one is
    /// a new lock: a staff PIN unlock of the last report ends here, before the lock screen reports again.
    /// </summary>
    public void LockGeneration(long generation)
    {
        if (generation <= _generation) return;
        _generation = generation;
        ScreenLocked = true;
        ScreenStaffUnlock = false;
    }

    /// <summary>
    /// A status report of the lock screen. lockGeneration: the generation of the state that the report comes from (null from
    /// an older lock screen: taken as current). Returns false for a report from before the newest lock: it shows that the
    /// lock screen answers, but its lock state is not used.
    /// </summary>
    public bool Report(DateTimeOffset now, bool locked, bool staffUnlock, long? lockGeneration = null)
    {
        _lastReportUtc = now;
        if (_warnedUtc != null)
        {
            _warnedUtc = null;
            _back = true;
        }
        if (lockGeneration is { } g && g < _generation) return false;
        ScreenLocked = locked;
        ScreenStaffUnlock = staffUnlock;
        return true;
    }

    /// <summary>"ok", "staff_unlock", "silent" or "unknown" (LockScreenStates).</summary>
    public string State(DateTimeOffset now)
    {
        if (_lastReportUtc is { } last && (now - last).Duration() <= SilentAfter)
            return ScreenStaffUnlock && !ScreenLocked ? LockScreenStates.StaffUnlock : LockScreenStates.Ok;
        if (_lastReportUtc == null && now - _startUtc < StartGrace) return LockScreenStates.Unknown;
        return LockScreenStates.Silent;
    }

    /// <summary>Called every second with the decision of the agent: the event to log now, or null.</summary>
    public LockScreenEvent? Check(DateTimeOffset now, bool mustBeLocked)
    {
        if (_back)
        {
            _back = false;
            return LockScreenEvent.Back;
        }
        if (!mustBeLocked || State(now) != LockScreenStates.Silent) return null;
        if (_warnedUtc is { } warned && now - warned < RepeatWarning) return null;
        _warnedUtc = now;
        return LockScreenEvent.SilentWhileLocked;
    }

    /// <summary>
    /// The lock state for the server (heartbeat "isLocked"): the agent's decision, but "unlocked" while a staff PIN unlock
    /// is active on a lock screen that answers. The admin UI then shows what the screen shows.
    /// </summary>
    public bool ReportedLocked(bool agentLocked, DateTimeOffset now) =>
        agentLocked && State(now) != LockScreenStates.StaffUnlock;
}
