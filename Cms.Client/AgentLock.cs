using Cms.Shared;

namespace Cms.Client;

/// <summary>
/// The lock state of a club PC in the agent (Cms.Agent.Service), without I/O and without a clock, so it is tested on
/// any OS. The rule: a club PC is LOCKED unless a session runs on it or staff unlocked it.
///  - A new agent (no state.json) starts locked.
///  - On every start of the agent, a saved state without an active session gives a locked PC: a staff unlock without
///    a session does not survive an agent restart or a reboot.
///  - "unlock" without a payload is an unlock for a session (session start, resume, move, or a staff unlock while a
///    session runs); "unlock" with {"reason":"staff"} is a staff unlock without a session (Cms.Shared.UnlockReasons).
///  - "session_set" only sets the end time; it never unlocks. "lock" locks.
///  - A heartbeat that says "no active session on this PC" locks a PC that is unlocked for a session.
/// </summary>
public sealed record AgentLock
{
    /// <summary>LockReason in state.json: locked because no session runs (start, or the server has no session).</summary>
    public const string NoSession = "no_session";
    /// <summary>LockReason in state.json: locked by a "lock" command (session end, pause, staff lock, new PC).</summary>
    public const string Manual = "manual";
    /// <summary>LockReason in state.json: the end time of the session passed (also while the server is down).</summary>
    public const string SessionExpired = "session_expired";
    /// <summary>LockReason in state.json: not locked.</summary>
    public const string NotLocked = "none";

    /// <summary>Unlock in state.json: locked.</summary>
    public const string UnlockNone = "none";
    /// <summary>Unlock in state.json: unlocked for a session (the end time applies).</summary>
    public const string UnlockSession = "session";
    /// <summary>Unlock in state.json: unlocked by staff without a session (until "lock" or the next agent start).</summary>
    public const string UnlockStaff = "staff";

    /// <summary>Why the PC is unlocked: "session", "staff", or "none" (locked).</summary>
    public string Unlock { get; init; } = UnlockNone;

    /// <summary>Why the PC is locked while Unlock is "none": "no_session" or "manual".</summary>
    public string LockedBy { get; init; } = NoSession;

    /// <summary>The end of the session from "session_set"; null = open-ended, paused, or no session.</summary>
    public DateTimeOffset? SessionEndUtc { get; init; }

    /// <summary>The state of an agent without state.json: locked.</summary>
    public static AgentLock Initial { get; } = new();

    /// <summary>
    /// The state after an agent start from the saved state.json. Only an unlock for a session survives; a staff unlock
    /// without a session and every unknown value give a locked PC. A file of an older agent (no "unlock") counts as a
    /// session when the PC was unlocked (or its session time was over) and the last heartbeat had an active session.
    /// </summary>
    public static AgentLock Restore(SavedAgentLock? saved)
    {
        if (saved == null) return Initial;
        var forSession = saved.Unlock switch
        {
            UnlockSession => true,
            null => saved.SessionKnown && (!saved.IsLocked || saved.LockReason == SessionExpired),
            _ => false
        };
        if (forSession) return new AgentLock { Unlock = UnlockSession, SessionEndUtc = saved.SessionEndUtc };
        var manual = saved.IsLocked && saved.LockReason == Manual;
        return new AgentLock { Unlock = UnlockNone, LockedBy = manual ? Manual : NoSession, SessionEndUtc = saved.SessionEndUtc };
    }

    /// <summary>The "lock" command.</summary>
    public AgentLock Lock() => this with { Unlock = UnlockNone, LockedBy = Manual };

    /// <summary>
    /// The "unlock" command with the reason of its payload (null = no payload). A staff unlock without a session has
    /// no end time: an end left by an earlier session must not lock it again at once.
    /// </summary>
    public AgentLock UnlockFor(string? reason) =>
        string.Equals(reason, UnlockReasons.Staff, StringComparison.OrdinalIgnoreCase)
            ? this with { Unlock = UnlockStaff, SessionEndUtc = null }
            : this with { Unlock = UnlockSession };

    /// <summary>The "session_set" command: the end time only (null = open-ended or paused). It does not unlock.</summary>
    public AgentLock SessionSet(DateTimeOffset? endUtc) => this with { SessionEndUtc = endUtc };

    /// <summary>
    /// A heartbeat answer of the server for this (approved) PC. No active session there: a PC unlocked for a session
    /// locks (the session ended and the "lock" command was lost or expired). A staff unlock stays.
    /// </summary>
    public AgentLock Heartbeat(bool sessionActive) =>
        !sessionActive && Unlock == UnlockSession ? this with { Unlock = UnlockNone, LockedBy = NoSession } : this;

    /// <summary>Locked or not at this time, why, and the seconds left of the session (0 = no end time).</summary>
    public AgentLockDecision Evaluate(DateTimeOffset nowUtc)
    {
        long remaining = 0;
        var expired = false;
        if (SessionEndUtc is { } end)
        {
            var left = end - nowUtc;
            if (left <= TimeSpan.Zero) expired = true;
            else remaining = (long)Math.Ceiling(left.TotalSeconds);
        }
        if (Unlock == UnlockNone) return new AgentLockDecision(true, LockedBy, remaining);
        if (expired) return new AgentLockDecision(true, SessionExpired, 0);
        return new AgentLockDecision(false, NotLocked, remaining);
    }
}

/// <summary>What the agent read from its state.json at the start. SessionKnown: the last heartbeat had an active session.</summary>
public sealed record SavedAgentLock(bool IsLocked, string? LockReason, string? Unlock, DateTimeOffset? SessionEndUtc, bool SessionKnown);

/// <summary>Reason: "none", "no_session", "manual" or "session_expired" (LockReason in state.json).</summary>
public sealed record AgentLockDecision(bool Locked, string Reason, long RemainingSeconds);
