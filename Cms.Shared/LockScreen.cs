namespace Cms.Shared;

public enum LockReason
{
    /// <summary>Not locked.</summary>
    None,
    /// <summary>The agent says locked (no session, session expired, lock command).</summary>
    LockedByAgent,
    /// <summary>The agent heartbeat file is missing or older than 15 s: fail closed.</summary>
    AgentSilent,
    /// <summary>state.json is missing or cannot be read: fail closed.</summary>
    NoAgentState,
}

public sealed record LockDecision(bool Locked, LockReason Reason, bool StaffOverride);

/// <summary>
/// What the launcher read from state.json. LockGeneration: the number of lock commands of the agent (0 for an older
/// agent); it changes with every lock command, also when the PC was locked already.
/// </summary>
public sealed record AgentLockSnapshot(bool IsLocked, DateTimeOffset? LockChangedUtc, long LockGeneration = 0);

/// <summary>
/// Lock decision of the lock screen (Cms.Launcher). It fails CLOSED: when the agent is silent or
/// its state cannot be read, the PC stays locked. Only the staff PIN (verified locally, also offline)
/// unlocks it then. A staff unlock lasts until the agent reports a new lock event or an unlock. A new lock event is a
/// new LockChangedUtc or a new LockGeneration: a lock command of the staff on a PC that the agent has locked already
/// changes only the generation (Windows test 2026-11-27, defect 2).
/// </summary>
public sealed class LockScreenController
{
    private DateTimeOffset? _overrideLockChangedUtc;
    private long? _overrideLockGeneration;

    public bool StaffOverride { get; private set; }

    public LockDecision Evaluate(AgentLockSnapshot? state, DateTimeOffset? agentHeartbeatUtc, DateTimeOffset nowUtc)
    {
        var silent = agentHeartbeatUtc is not { } hb || (nowUtc - hb).Duration() > AgentFiles.AgentSilentAfter;

        LockReason reason;
        if (silent)
        {
            reason = LockReason.AgentSilent;
        }
        else if (state == null)
        {
            reason = LockReason.NoAgentState;
        }
        else if (!state.IsLocked)
        {
            StaffOverride = false; // the agent unlocked: the override is no longer needed
            return new LockDecision(false, LockReason.None, false);
        }
        else
        {
            reason = LockReason.LockedByAgent;
            if (StaffOverride && (state.LockChangedUtc != _overrideLockChangedUtc || state.LockGeneration != _overrideLockGeneration))
                StaffOverride = false; // a new lock event (or a new lock command) after the staff unlock
        }

        return StaffOverride
            ? new LockDecision(false, reason, true)
            : new LockDecision(true, reason, false);
    }

    /// <summary>Call after the staff PIN was verified.</summary>
    public void BeginStaffOverride(AgentLockSnapshot? state)
    {
        StaffOverride = true;
        _overrideLockChangedUtc = state?.LockChangedUtc;
        _overrideLockGeneration = state?.LockGeneration;
    }
}

/// <summary>
/// Slows down PIN guessing on the lock screen: 3 free attempts, then a pause of 30 s that doubles
/// with every further wrong PIN, up to 15 minutes.
/// </summary>
public sealed class PinAttemptLimiter
{
    public const int FreeAttempts = 3;
    private static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(15);

    private int _failures;
    private DateTimeOffset _lockedUntilUtc = DateTimeOffset.MinValue;

    /// <summary>Time to wait before the next attempt, or null when an attempt is allowed now.</summary>
    public TimeSpan? WaitTime(DateTimeOffset nowUtc) => nowUtc < _lockedUntilUtc ? _lockedUntilUtc - nowUtc : null;

    public void RecordFailure(DateTimeOffset nowUtc)
    {
        _failures++;
        if (_failures < FreeAttempts) return;
        var factor = Math.Pow(2, Math.Min(_failures - FreeAttempts, 10));
        var pause = TimeSpan.FromSeconds(Math.Min(FirstPause.TotalSeconds * factor, MaxPause.TotalSeconds));
        _lockedUntilUtc = nowUtc + pause;
    }

    public void RecordSuccess()
    {
        _failures = 0;
        _lockedUntilUtc = DateTimeOffset.MinValue;
    }
}
