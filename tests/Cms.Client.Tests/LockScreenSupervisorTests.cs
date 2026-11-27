using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// Windows test 2026-11-27, defect 5: nothing restarted a killed lock screen, and the agent did not notice. The watchdog
/// restarts it; the agent logs a lock screen that does not report while the PC must be locked.
/// </summary>
public sealed class LockScreenSupervisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_watchdog_restarts_the_lock_screen_within_a_second_and_slows_down_only_in_a_crash_loop()
    {
        var policy = new RestartPolicy();
        Assert.True(RestartPolicy.NormalDelay <= TimeSpan.FromSeconds(2));
        for (var i = 0; i < RestartPolicy.CrashLoopEnds; i++)
            Assert.Equal(RestartPolicy.NormalDelay, policy.OnExit(Now.AddSeconds(i)));
        Assert.Equal(RestartPolicy.CrashLoopDelay, policy.OnExit(Now.AddSeconds(5)));      // the 6th end within 60 s
        Assert.Equal(RestartPolicy.CrashLoopDelay, policy.OnExit(Now.AddSeconds(20)));
        Assert.Equal(RestartPolicy.NormalDelay, policy.OnExit(Now.AddMinutes(10)));       // calm again: never gives up

        var killedOnce = new RestartPolicy();
        Assert.Equal(RestartPolicy.NormalDelay, killedOnce.OnExit(Now));
        Assert.Equal(RestartPolicy.NormalDelay, killedOnce.OnExit(Now.AddMinutes(30)));
    }

    [Fact]
    public void The_watchdog_mode_is_the_argument_watchdog()
    {
        Assert.True(LauncherModes.IsWatchdog(new[] { "--watchdog" }));
        Assert.True(LauncherModes.IsWatchdog(new[] { "x", " --WATCHDOG " }));
        Assert.False(LauncherModes.IsWatchdog(Array.Empty<string>()));
        Assert.False(LauncherModes.IsWatchdog(null));
        Assert.False(LauncherModes.IsWatchdog(new[] { "--watchdogs" }));
        Assert.StartsWith(@"Local\", LauncherModes.WatchdogMutex);   // per Windows session
        Assert.StartsWith(@"Local\", LauncherModes.LockScreenMutex);
    }

    [Fact]
    public void The_watchdog_watches_a_lock_screen_that_runs_already_in_its_session()
    {
        const string exe = @"C:\ClubLauncher\Cms.Launcher.exe";
        var processes = new[]
        {
            new LauncherProcess(10, 1, exe, Now.AddSeconds(-5)),                                    // the watchdog itself
            new LauncherProcess(11, 2, exe, Now.AddMinutes(-9)),                                    // another session
            new LauncherProcess(12, 1, @"C:\Users\ClubUser\Cms.Launcher.exe", Now.AddMinutes(-9)), // a copy of the player
            new LauncherProcess(13, 1, exe, Now.AddMilliseconds(-300)),                             // a watchdog that ends at once
            new LauncherProcess(14, 1, @"c:\clublauncher\cms.launcher.exe", Now.AddMinutes(-3)),   // the lock screen
            new LauncherProcess(15, 1, exe, Now.AddMinutes(-1)),
        };
        Assert.Equal(14, LockScreenProcesses.PickRunningLockScreen(processes, ownPid: 10, sessionId: 1, exe, Now));
        Assert.Null(LockScreenProcesses.PickRunningLockScreen(processes.Take(4), 10, 1, exe, Now)); // none: start one
        Assert.Null(LockScreenProcesses.PickRunningLockScreen(new[] { new LauncherProcess(16, 1, null, Now.AddMinutes(-1)) }, 10, 1, exe, Now));
    }

    [Fact]
    public void The_agent_logs_a_silent_lock_screen_only_while_the_pc_must_be_locked()
    {
        var watch = new LockScreenWatch(Now);
        // After the agent start the player may not be signed in yet
        Assert.Equal(LockScreenStates.Unknown, watch.State(Now.AddSeconds(30)));
        Assert.Null(watch.Check(Now.AddSeconds(30), mustBeLocked: true));
        // Two minutes without any report: silent
        Assert.Equal(LockScreenStates.Silent, watch.State(Now.AddMinutes(2)));
        Assert.Equal(LockScreenEvent.SilentWhileLocked, watch.Check(Now.AddMinutes(2), true));
        Assert.Null(watch.Check(Now.AddMinutes(3), true));                                     // once, not every second
        Assert.Equal(LockScreenEvent.SilentWhileLocked, watch.Check(Now.AddMinutes(18), true)); // again after 15 minutes

        watch.Report(Now.AddMinutes(19), locked: true, staffUnlock: false);
        Assert.Equal(LockScreenEvent.Back, watch.Check(Now.AddMinutes(19), true));
        Assert.Equal(LockScreenStates.Ok, watch.State(Now.AddMinutes(19).AddSeconds(15)));
        Assert.Null(watch.Check(Now.AddMinutes(19).AddSeconds(15), true));

        // The lock screen is killed (no report for 20 s) while the PC must be locked
        var killed = Now.AddMinutes(19).AddSeconds(21);
        Assert.Equal(LockScreenEvent.SilentWhileLocked, watch.Check(killed, true));
    }

    [Fact]
    public void A_silent_lock_screen_during_a_session_is_not_logged()
    {
        var watch = new LockScreenWatch(Now);
        watch.Report(Now, locked: false, staffUnlock: false);
        Assert.Equal(LockScreenStates.Silent, watch.State(Now.AddSeconds(25)));
        Assert.Null(watch.Check(Now.AddSeconds(25), mustBeLocked: false));
        Assert.Equal(LockScreenEvent.SilentWhileLocked, watch.Check(Now.AddSeconds(26), mustBeLocked: true)); // the session ended
    }

    [Fact]
    public void A_staff_pin_unlock_on_the_screen_is_reported_to_the_server_as_unlocked()
    {
        var watch = new LockScreenWatch(Now);
        Assert.True(watch.ReportedLocked(agentLocked: true, Now));
        watch.Report(Now, locked: false, staffUnlock: true);
        Assert.Equal(LockScreenStates.StaffUnlock, watch.State(Now.AddSeconds(5)));
        Assert.False(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(5)));
        Assert.True(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(30)));  // the screen is silent: report the agent's state
        Assert.False(watch.ReportedLocked(agentLocked: false, Now.AddSeconds(5)));
        watch.Report(Now.AddSeconds(40), locked: true, staffUnlock: false);       // a lock command ended the staff unlock
        Assert.True(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(41)));
    }

    [Fact]
    public void The_status_report_is_a_pipe_request()
    {
        var req = AgentPipe.ParseRequest("{\"op\":\"status\",\"locked\":false,\"staffUnlock\":true}");
        Assert.NotNull(req);
        Assert.Equal((PipeOps.Status, (bool?)false, (bool?)true), (req!.Op, req.Locked, req.StaffUnlock));
        Assert.Null(req.LockGeneration);                                                        // an older lock screen
        Assert.Contains(PipeOps.Status, PipeOps.All);
        Assert.Equal(7L, AgentPipe.ParseRequest("{\"op\":\"status\",\"locked\":true,\"staffUnlock\":false,\"lockGeneration\":7}")!.LockGeneration);
    }

    /// <summary>Windows test 2026-11-27 night, defect 5: for about 5 s after the lock the admin API showed "unlocked" again.</summary>
    [Fact]
    public void The_newest_lock_wins_over_an_older_staff_unlock_report()
    {
        var watch = new LockScreenWatch(Now);
        watch.LockGeneration(1);                                                                 // state.json at the agent start
        Assert.True(watch.Report(Now, locked: false, staffUnlock: true, lockGeneration: 1));      // staff PIN unlock on the screen
        Assert.False(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(1)));

        // The admin locks: generation 2. The report of the staff unlock ends at once
        watch.LockGeneration(2);
        Assert.Equal(2, watch.Generation);
        Assert.True(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(2)));
        Assert.Equal(LockScreenStates.Ok, watch.State(Now.AddSeconds(2)));

        // A report made before the lock screen read the new state.json: a sign of life only
        Assert.False(watch.Report(Now.AddSeconds(3), locked: false, staffUnlock: true, lockGeneration: 1));
        Assert.True(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(4)));
        Assert.Equal(Now.AddSeconds(3), watch.LastReportUtc);

        // The report of the new state, and a new staff unlock after this lock (it counts again)
        Assert.True(watch.Report(Now.AddSeconds(5), locked: true, staffUnlock: false, lockGeneration: 2));
        Assert.True(watch.Report(Now.AddSeconds(30), locked: false, staffUnlock: true, lockGeneration: 2));
        Assert.False(watch.ReportedLocked(agentLocked: true, Now.AddSeconds(31)));

        // An older generation never lowers the known one; a report without a generation (older lock screen) is used
        watch.LockGeneration(1);
        Assert.Equal(2, watch.Generation);
        Assert.True(watch.Report(Now.AddSeconds(40), locked: false, staffUnlock: true));
        Assert.Equal(LockScreenStates.StaffUnlock, watch.State(Now.AddSeconds(41)));
    }

    [Fact]
    public void An_old_report_still_ends_the_silent_warning()
    {
        var watch = new LockScreenWatch(Now);
        watch.LockGeneration(3);
        Assert.Equal(LockScreenEvent.SilentWhileLocked, watch.Check(Now.AddMinutes(3), mustBeLocked: true));
        Assert.False(watch.Report(Now.AddMinutes(3).AddSeconds(1), locked: true, staffUnlock: false, lockGeneration: 2));
        Assert.Equal(LockScreenEvent.Back, watch.Check(Now.AddMinutes(3).AddSeconds(1), true));
    }
}
