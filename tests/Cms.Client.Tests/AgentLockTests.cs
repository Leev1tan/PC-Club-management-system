using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>The lock rule of the agent: a club PC is locked unless a session runs on it or staff unlocked it.</summary>
public sealed class AgentLockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);

    /// <summary>The commands of SessionService at a session start: the end, then "unlock" without a payload.</summary>
    private static AgentLock Started(AgentLock state, DateTimeOffset? end) => state.SessionSet(end).UnlockFor(null);

    private static void AssertLocked(AgentLock state, string reason, DateTimeOffset? at = null)
    {
        var d = state.Evaluate(at ?? Now);
        Assert.True(d.Locked, $"expected locked ({reason}), got unlocked");
        Assert.Equal(reason, d.Reason);
    }

    private static void AssertUnlocked(AgentLock state, DateTimeOffset? at = null)
    {
        var d = state.Evaluate(at ?? Now);
        Assert.False(d.Locked, $"expected unlocked, got locked ({d.Reason})");
        Assert.Equal(AgentLock.NotLocked, d.Reason);
    }

    [Fact]
    public void A_new_pc_without_a_state_file_is_locked()
    {
        AssertLocked(AgentLock.Restore(null), AgentLock.NoSession);
        AssertLocked(AgentLock.Initial, AgentLock.NoSession);
        // The defect of v0.8.0-beta.1: "session_set" alone or nothing at all must not unlock
        AssertLocked(AgentLock.Initial.SessionSet(null), AgentLock.NoSession);
        AssertLocked(AgentLock.Initial.SessionSet(Now.AddHours(1)), AgentLock.NoSession);
    }

    [Fact]
    public void A_restored_state_without_a_session_is_locked()
    {
        AssertLocked(AgentLock.Restore(new SavedAgentLock(true, AgentLock.NoSession, AgentLock.UnlockNone, null, false)), AgentLock.NoSession);
        AssertLocked(AgentLock.Restore(new SavedAgentLock(true, AgentLock.Manual, AgentLock.UnlockNone, null, false)), AgentLock.Manual);
        // The state.json that v0.8.0-beta.1 wrote on a new PC (unlocked, no session, no "unlock" field)
        AssertLocked(AgentLock.Restore(new SavedAgentLock(false, "none", null, null, false)), AgentLock.NoSession);
        // An unknown value of "unlock"
        AssertLocked(AgentLock.Restore(new SavedAgentLock(false, "none", "everything", null, true)), AgentLock.NoSession);
    }

    [Fact]
    public void An_active_session_unlocks_and_counts_down()
    {
        var timed = Started(AgentLock.Initial, Now.AddMinutes(30));
        AssertUnlocked(timed);
        Assert.Equal(30 * 60, timed.Evaluate(Now).RemainingSeconds);
        Assert.Equal(AgentLock.UnlockSession, timed.Unlock);

        var openEnded = Started(AgentLock.Initial, null);
        AssertUnlocked(openEnded);
        Assert.Equal(0, openEnded.Evaluate(Now).RemainingSeconds);

        // The heartbeat confirms the session: nothing changes
        AssertUnlocked(timed.Heartbeat(sessionActive: true));
    }

    [Fact]
    public void A_session_that_expired_locks_also_without_the_server_and_an_extension_unlocks_again()
    {
        var state = Started(AgentLock.Initial, Now.AddMinutes(10));
        AssertLocked(state, AgentLock.SessionExpired, Now.AddMinutes(10));
        AssertLocked(state, AgentLock.SessionExpired, Now.AddMinutes(11));

        // Extended at the desk while the server had not ended the session yet: session_set alone
        AssertUnlocked(state.SessionSet(Now.AddMinutes(40)), Now.AddMinutes(11));

        // The server ends it: session_set(now), then lock
        var ended = state.SessionSet(Now.AddMinutes(11)).Lock();
        AssertLocked(ended, AgentLock.Manual, Now.AddMinutes(11));
    }

    [Fact]
    public void A_staff_unlock_without_a_session_lasts_until_the_agent_restarts()
    {
        // The PC had a session before: its end time is in the past
        var afterSession = Started(AgentLock.Initial, Now.AddMinutes(-30)).SessionSet(Now.AddMinutes(-30)).Lock();
        AssertLocked(afterSession, AgentLock.Manual);

        var staff = afterSession.UnlockFor(UnlockReasons.Staff);
        AssertUnlocked(staff);                                   // the old end time does not lock it again
        Assert.Equal(AgentLock.UnlockStaff, staff.Unlock);
        AssertUnlocked(staff.Heartbeat(sessionActive: false));   // the server has no session: the staff unlock stays
        AssertUnlocked(staff, Now.AddHours(8));

        // Agent restart or reboot: state.json says "staff", the PC locks
        var saved = new SavedAgentLock(false, AgentLock.NotLocked, staff.Unlock, staff.SessionEndUtc, false);
        AssertLocked(AgentLock.Restore(saved), AgentLock.NoSession);

        // A "lock" command ends the staff unlock at once
        AssertLocked(staff.Lock(), AgentLock.Manual);
    }

    [Fact]
    public void An_unlock_without_a_payload_is_an_unlock_for_a_session()
    {
        // Session start, resume, move, a staff unlock while a session runs, or an older server
        var state = AgentLock.Initial.UnlockFor(null);
        Assert.Equal(AgentLock.UnlockSession, state.Unlock);
        AssertLocked(state.Heartbeat(sessionActive: false), AgentLock.NoSession);
        Assert.Equal(AgentLock.UnlockSession, AgentLock.Initial.UnlockFor("session").Unlock);
        Assert.Equal(AgentLock.UnlockStaff, AgentLock.Initial.UnlockFor("STAFF").Unlock);
    }

    [Fact]
    public void A_heartbeat_without_a_session_locks_a_pc_unlocked_for_a_session()
    {
        // The session ended on the server, but the agent missed the "lock" (expired in the queue)
        var state = Started(AgentLock.Initial, null).Heartbeat(sessionActive: false);
        AssertLocked(state, AgentLock.NoSession);
        AssertLocked(state.Heartbeat(sessionActive: true), AgentLock.NoSession); // a heartbeat never unlocks
    }

    [Fact]
    public void A_pause_locks_and_a_resume_unlocks()
    {
        var running = Started(AgentLock.Initial, Now.AddMinutes(60));
        var paused = running.SessionSet(null).Lock();
        AssertLocked(paused, AgentLock.Manual);
        AssertLocked(paused.Heartbeat(sessionActive: true), AgentLock.Manual); // the paused session is still active

        var resumed = paused.SessionSet(Now.AddMinutes(75)).UnlockFor(null);
        AssertUnlocked(resumed);
        Assert.Equal(75 * 60, resumed.Evaluate(Now).RemainingSeconds);
    }

    [Fact]
    public void A_staff_lock_during_a_session_is_not_undone_by_a_new_end_time()
    {
        var locked = Started(AgentLock.Initial, Now.AddMinutes(30)).Lock();
        AssertLocked(locked.SessionSet(Now.AddMinutes(60)), AgentLock.Manual); // extension or new balance end
        AssertUnlocked(locked.SessionSet(Now.AddMinutes(60)).UnlockFor(null));   // staff unlock while the session runs
    }

    [Fact]
    public void A_session_survives_an_agent_restart_with_its_end_time()
    {
        var state = Started(AgentLock.Initial, Now.AddMinutes(20));
        var saved = new SavedAgentLock(false, AgentLock.NotLocked, state.Unlock, state.SessionEndUtc, true);
        var restored = AgentLock.Restore(saved);
        AssertUnlocked(restored);
        Assert.Equal(Now.AddMinutes(20), restored.SessionEndUtc);
        AssertLocked(restored, AgentLock.SessionExpired, Now.AddMinutes(21));

        // The heartbeat after the restart says that the session is over
        AssertLocked(restored.Heartbeat(sessionActive: false), AgentLock.NoSession);

        // Restarted after the end time: locked at once
        var late = new SavedAgentLock(true, AgentLock.SessionExpired, AgentLock.UnlockSession, Now.AddMinutes(-1), true);
        AssertLocked(AgentLock.Restore(late), AgentLock.SessionExpired);
    }

    [Fact]
    public void An_update_from_an_older_agent_keeps_a_running_session()
    {
        // state.json of v0.8.0-beta.1 during a session: unlocked, the heartbeat had the session, no "unlock" field
        var running = AgentLock.Restore(new SavedAgentLock(false, "none", null, Now.AddMinutes(45), true));
        AssertUnlocked(running);
        Assert.Equal(AgentLock.UnlockSession, running.Unlock);

        var expired = AgentLock.Restore(new SavedAgentLock(true, AgentLock.SessionExpired, null, Now.AddMinutes(-5), true));
        AssertLocked(expired, AgentLock.SessionExpired);
        AssertUnlocked(expired.SessionSet(Now.AddMinutes(30)));      // extended: the session continues

        var paused = AgentLock.Restore(new SavedAgentLock(true, AgentLock.Manual, null, null, true));
        AssertLocked(paused, AgentLock.Manual);
    }

    /// <summary>Windows test 2026-11-27, defect 2: a lock command on a PC that the agent has locked already is still a new lock event.</summary>
    [Fact]
    public void Every_lock_command_is_a_new_lock_event_also_on_a_locked_pc()
    {
        var locked = AgentLock.Initial.Lock();
        Assert.Equal(1, locked.Generation);
        var again = locked.Lock();
        Assert.Equal(2, again.Generation);
        AssertLocked(again, AgentLock.Manual);
        Assert.Equal(locked.Evaluate(Now), again.Evaluate(Now)); // the agent's decision stays the same: only the generation moves

        // Unlock, session_set and heartbeat do not change it; the next lock does
        var other = again.UnlockFor(UnlockReasons.Staff).SessionSet(null).Heartbeat(sessionActive: false).UnlockFor(null);
        Assert.Equal(2, other.Generation);
        Assert.Equal(3, other.Lock().Generation);
    }

    [Fact]
    public void The_lock_generation_survives_an_agent_restart()
    {
        var restored = AgentLock.Restore(new SavedAgentLock(true, AgentLock.Manual, AgentLock.UnlockNone, null, false, Generation: 41));
        Assert.Equal(41, restored.Generation);
        Assert.Equal(42, restored.Lock().Generation);
        Assert.Equal(7, AgentLock.Restore(new SavedAgentLock(false, "none", AgentLock.UnlockSession, Now.AddHours(1), true, 7)).Generation);
        Assert.Equal(0, AgentLock.Restore(new SavedAgentLock(true, AgentLock.Manual, AgentLock.UnlockNone, null, false)).Generation); // older file
        Assert.Equal(0, AgentLock.Restore(new SavedAgentLock(true, AgentLock.Manual, AgentLock.UnlockNone, null, false, -5)).Generation);
    }
}
