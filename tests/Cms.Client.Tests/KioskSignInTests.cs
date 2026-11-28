using Cms.Client;
using Xunit;
using static Cms.Client.KioskRestartAction;
using static Cms.Client.KioskRestartReason;
using State = Cms.Client.WindowsSessionState;

namespace Cms.Client.Tests;

/// <summary>
/// Windows re-test 2026-11-28 night, defect 1 (HIGH): without ForceAutoLogon, Windows 11 does not sign ClubUser in again
/// after a sign-out (staff "Log off", the update, ClubAdmin signs out). Only a restart signs ClubUser in. On a kiosk PC the
/// agent now restarts Windows when no user is signed in for 120 s (and Windows runs 5 min), at once after its own staff
/// "Log off", and at most 3 times in 60 min.
/// </summary>
public sealed class KioskRestartPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);

    // What WTSEnumerateSessions shows on a club PC
    private static readonly WindowsSession Services = new(0, "", State.Disconnected);
    private static readonly WindowsSession SignInScreen = new(2, "", State.Connected);      // the Windows sign-in screen at the console
    private static readonly WindowsSession RdpListener = new(65536, "", State.Listen);
    private static readonly WindowsSession ClubUser = new(1, "ClubUser", State.Active);
    private static readonly WindowsSession ClubUserDisconnected = new(1, "ClubUser", State.Disconnected);
    private static readonly WindowsSession ClubAdmin = new(3, "ClubAdmin", State.Active);

    private static KioskRestartInput NoUser(TimeSpan uptime, TimeSpan withoutSessions, IReadOnlyList<DateTimeOffset>? history = null) => new()
    {
        KioskMode = true,
        KioskUser = "ClubUser",
        Sessions = new[] { Services, SignInScreen, RdpListener },
        Uptime = uptime,
        WithoutSessions = withoutSessions,
        RestartHistory = history ?? Array.Empty<DateTimeOffset>(),
        Now = Now
    };

    private static KioskRestartDecision Decide(KioskRestartInput input) => KioskRestartPolicy.Decide(input);

    [Fact]
    public void A_PC_without_kiosk_mode_is_never_restarted()
    {
        var input = NoUser(TimeSpan.FromHours(3), TimeSpan.FromHours(1)) with { KioskMode = false };
        Assert.Equal(new KioskRestartDecision(Wait, NotKiosk), Decide(input));
        Assert.Equal(new KioskRestartDecision(Wait, NotKiosk), Decide(input with { StaffLogoffPending = true }));
    }

    [Fact]
    public void No_user_for_120_s_after_5_min_of_Windows_restarts_it()
    {
        Assert.Equal(new KioskRestartDecision(Wait, Counting), Decide(NoUser(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(119))));
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), Decide(NoUser(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(120))));
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), Decide(NoUser(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void A_boot_before_the_automatic_sign_in_never_restarts()
    {
        // The automatic sign-in comes about 1 min after the boot: before 5 min of Windows the agent only waits
        Assert.Equal(new KioskRestartDecision(Wait, WindowsStarting), Decide(NoUser(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10))));
        Assert.Equal(new KioskRestartDecision(Wait, WindowsStarting), Decide(NoUser(TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(4.5))));
    }

    [Theory]
    [MemberData(nameof(SignedIn))]
    public void A_signed_in_user_active_or_disconnected_stops_every_restart(WindowsSession[] sessions)
    {
        var input = NoUser(TimeSpan.FromHours(2), TimeSpan.FromHours(1)) with { Sessions = sessions };
        Assert.Equal(new KioskRestartDecision(Wait, UserSignedIn), Decide(input));
        Assert.Equal(new KioskRestartDecision(Wait, UserSignedIn), Decide(input with { StaffLogoffPending = true }));
    }

    public static IEnumerable<object[]> SignedIn() => new[]
    {
        new object[] { new[] { Services, ClubUser } },                                        // the normal kiosk
        new object[] { new[] { Services, ClubUserDisconnected, ClubAdmin } },                 // the admin route
        new object[] { new[] { Services, ClubUserDisconnected, new WindowsSession(3, "ClubAdmin", State.Disconnected), SignInScreen } },
        new object[] { new[] { Services, ClubAdmin } },                                       // only ClubAdmin
        new object[] { new[] { Services, new WindowsSession(3, "ClubAdmin", State.Disconnected), SignInScreen } },
        new object[] { new[] { Services, new WindowsSession(4, "tech", State.Active) } },     // an RDP user
        new object[] { new[] { Services, new WindowsSession(2, null, State.Connected) } },    // user not readable: fail safe
        new object[] { new[] { Services, new WindowsSession(2, "ClubUser", State.Connected) } } // a sign-in in progress
    };

    // Coordinator decision 2026-11-28: the admin route (Switch user > ClubAdmin, sign out) and a player's Switch user on a
    // locked PC leave only the disconnected session of ClubUser. On a locked PC that counts as nobody signed in.
    [Fact]
    public void A_disconnected_ClubUser_alone_on_a_locked_PC_counts_as_nobody()
    {
        var switchUser = new[] { Services, ClubUserDisconnected, SignInScreen };
        var input = NoUser(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(119)) with { Sessions = switchUser, Locked = true };
        Assert.False(KioskRestartPolicy.SomebodySignedIn(switchUser, "ClubUser", locked: true));
        Assert.Equal(new KioskRestartDecision(Wait, Counting), Decide(input));
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), Decide(input with { WithoutSessions = TimeSpan.FromSeconds(120) }));
        Assert.Equal(new KioskRestartDecision(Wait, WindowsStarting), Decide(input with { Uptime = TimeSpan.FromMinutes(2) }));
    }

    [Fact]
    public void A_disconnected_ClubUser_during_a_running_session_is_signed_in()
    {
        // The player's paid time runs (the agent unlocked the PC): a restart would end it; staff decide (Log off)
        var switchUser = new[] { Services, ClubUserDisconnected, SignInScreen };
        var input = NoUser(TimeSpan.FromHours(2), TimeSpan.FromHours(1)) with { Sessions = switchUser, Locked = false };
        Assert.True(KioskRestartPolicy.SomebodySignedIn(switchUser, "ClubUser", locked: false));
        Assert.Equal(new KioskRestartDecision(Wait, UserSignedIn), Decide(input));
    }

    [Fact]
    public void A_disconnected_ClubUser_next_to_another_user_is_signed_in()
    {
        var withAdmin = new[] { Services, ClubUserDisconnected, ClubAdmin };
        var withAdminDisconnected = new[] { Services, ClubUserDisconnected, new WindowsSession(3, "ClubAdmin", State.Disconnected), SignInScreen };
        var twoClubUsers = new[] { Services, ClubUserDisconnected, new WindowsSession(4, "ClubUser", State.Active) };
        foreach (var sessions in new[] { withAdmin, withAdminDisconnected, twoClubUsers })
            Assert.Equal(new KioskRestartDecision(Wait, UserSignedIn),
                Decide(NoUser(TimeSpan.FromHours(2), TimeSpan.FromHours(1)) with { Sessions = sessions, Locked = true }));
    }

    [Fact]
    public void Only_the_kiosk_user_of_kiosk_json_counts_as_nobody_when_disconnected()
    {
        var other = new[] { Services, new WindowsSession(1, "ClubAdmin", State.Disconnected), SignInScreen };
        var unreadable = new[] { Services, new WindowsSession(1, null, State.Disconnected), SignInScreen };
        var lowerCase = new[] { Services, new WindowsSession(1, "clubuser", State.Disconnected), SignInScreen };
        Assert.True(KioskRestartPolicy.SomebodySignedIn(other, "ClubUser", locked: true));
        Assert.True(KioskRestartPolicy.SomebodySignedIn(unreadable, "ClubUser", locked: true));
        Assert.False(KioskRestartPolicy.SomebodySignedIn(lowerCase, "ClubUser", locked: true));   // Windows names: no case
        Assert.True(KioskRestartPolicy.SomebodySignedIn(lowerCase, null, locked: true));
        Assert.True(KioskRestartPolicy.SomebodySignedIn(null, "ClubUser", locked: true));          // unknown list: fail safe
        Assert.False(KioskRestartPolicy.SomebodySignedIn(new[] { Services, SignInScreen }, "ClubUser", locked: false));
    }

    [Fact]
    public void Session_0_listeners_ended_sessions_and_the_sign_in_screen_are_no_user()
    {
        var sessions = new[]
        {
            new WindowsSession(0, "SYSTEM", State.Disconnected),        // session 0 never counts
            new WindowsSession(2, "", State.Connected),
            new WindowsSession(65536, null, State.Listen),
            new WindowsSession(1, "ClubUser", State.Down),               // the session that is ending
            new WindowsSession(5, "ClubUser", State.Reset)
        };
        Assert.All(sessions, s => Assert.False(KioskRestartPolicy.IsUserSession(s)));
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn),
            Decide(NoUser(TimeSpan.FromHours(1), TimeSpan.FromMinutes(3)) with { Sessions = sessions }));
    }

    [Fact]
    public void A_session_list_that_cannot_be_read_never_restarts()
    {
        Assert.Equal(new KioskRestartDecision(Wait, SessionsUnknown),
            Decide(NoUser(TimeSpan.FromHours(1), TimeSpan.FromHours(1)) with { Sessions = null, StaffLogoffPending = true }));
    }

    [Fact]
    public void The_staff_log_off_restarts_at_once_when_the_session_is_gone()
    {
        Assert.Equal(new KioskRestartDecision(RestartNow, StaffLogoff),
            Decide(NoUser(TimeSpan.FromMinutes(2), TimeSpan.Zero) with { StaffLogoffPending = true }));
    }

    [Fact]
    public void At_most_3_restarts_in_60_min()
    {
        var two = new[] { Now.AddMinutes(-50), Now.AddMinutes(-20) };
        var three = new[] { Now.AddMinutes(-50), Now.AddMinutes(-20), Now.AddMinutes(-6) };
        var threeOneOld = new[] { Now.AddMinutes(-61), Now.AddMinutes(-20), Now.AddMinutes(-6) };
        var due = NoUser(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), Decide(due with { RestartHistory = two }));
        Assert.Equal(new KioskRestartDecision(Blocked, GuardFull), Decide(due with { RestartHistory = three }));
        Assert.Equal(new KioskRestartDecision(Blocked, GuardFull), Decide(due with { RestartHistory = three, StaffLogoffPending = true }));
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), Decide(due with { RestartHistory = threeOneOld }));
        // Not due yet: the guard does not show before the restart would come
        Assert.Equal(new KioskRestartDecision(Wait, Counting),
            Decide(NoUser(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(30)) with { RestartHistory = three }));
    }

    [Fact]
    public void The_guard_opens_60_min_after_the_oldest_of_the_last_3_restarts()
    {
        var three = new[] { Now.AddMinutes(-50), Now.AddMinutes(-20), Now.AddMinutes(-6) };
        Assert.Equal(Now.AddMinutes(10), KioskRestartPolicy.GuardOpensUtc(three, Now));
        Assert.Null(KioskRestartPolicy.GuardOpensUtc(three.Skip(1).ToArray(), Now));
        var four = new[] { Now.AddMinutes(-55), Now.AddMinutes(-50), Now.AddMinutes(-20), Now.AddMinutes(-6) };
        Assert.Equal(Now.AddMinutes(10), KioskRestartPolicy.GuardOpensUtc(four, Now));
    }

    [Fact]
    public void A_restart_time_far_in_the_future_is_a_clock_error_and_does_not_count()
    {
        Assert.Equal(1, KioskRestartPolicy.RecentRestarts(new[] { Now.AddMinutes(30), Now.AddHours(3), Now.AddDays(-1) }, Now));
    }

    [Fact]
    public void A_restart_history_that_cannot_be_read_never_restarts()
    {
        Assert.Equal(new KioskRestartDecision(Wait, HistoryUnknown),
            Decide(NoUser(TimeSpan.FromHours(1), TimeSpan.FromMinutes(3)) with { RestartHistory = null }));
        Assert.Equal(new KioskRestartDecision(Wait, HistoryUnknown),
            Decide(NoUser(TimeSpan.FromHours(1), TimeSpan.Zero) with { RestartHistory = null, StaffLogoffPending = true }));
    }
}

/// <summary>The agent's view over time (KioskSignInWatch): the count, the staff log off and the boot loop.</summary>
public sealed class KioskSignInWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly WindowsSession Services = new(0, "", State.Disconnected);
    private static readonly WindowsSession SignInScreen = new(2, "", State.Connected);
    private static readonly WindowsSession[] NoUser = { Services, SignInScreen };
    private static readonly WindowsSession[] ClubUser = { Services, new(1, "ClubUser", State.Active) };
    private static readonly WindowsSession[] ClubAdminOnly = { Services, new(3, "ClubAdmin", State.Active) };
    private static readonly WindowsSession[] ClubAdminAndClubUser = { Services, new(1, "ClubUser", State.Disconnected), new(3, "ClubAdmin", State.Active) };
    private static readonly WindowsSession[] ClubUserDisconnectedOnly = { Services, new(1, "ClubUser", State.Disconnected), SignInScreen };
    private static readonly DateTimeOffset[] NoRestarts = Array.Empty<DateTimeOffset>();

    private sealed class Pc
    {
        public readonly KioskSignInWatch Watch = new();
        public TimeSpan Uptime = TimeSpan.FromMinutes(30);
        public bool Kiosk = true;
        public bool Locked = true;
        public IReadOnlyList<DateTimeOffset>? History = NoRestarts;
        public DateTimeOffset BootUtc = Start;
        public DateTimeOffset Now => BootUtc + Uptime;

        public KioskSignInStep Tick(IReadOnlyList<WindowsSession>? sessions, double seconds = 2)
        {
            Uptime += TimeSpan.FromSeconds(seconds);
            return Watch.Next(Kiosk, Kiosk ? "ClubUser" : null, Locked, sessions, Uptime, History, Now);
        }

        /// <summary>Ticks every 2 s for the given seconds; the last step.</summary>
        public KioskSignInStep For(double seconds, IReadOnlyList<WindowsSession>? sessions)
        {
            KioskSignInStep last = Tick(sessions);
            for (double t = 2; t < seconds; t += 2) last = Tick(sessions);
            return last;
        }
    }

    [Fact]
    public void The_count_starts_when_the_last_user_signs_out_and_restarts_after_120_s()
    {
        var pc = new Pc();
        Assert.Equal(new KioskRestartDecision(Wait, UserSignedIn), pc.Tick(ClubUser).Decision);
        var first = pc.Tick(NoUser);
        Assert.Equal(new KioskRestartDecision(Wait, Counting), first.Decision);
        Assert.True(first.Changed);                      // "the count starts" is logged once
        Assert.False(pc.Tick(NoUser).Changed);
        Assert.Equal(Counting, pc.For(114, NoUser).Decision.Reason);
        var restart = pc.For(4, NoUser);
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), restart.Decision);
        Assert.True(restart.Changed);
        Assert.Equal(TimeSpan.FromSeconds(120), restart.WithoutSessions);
    }

    [Fact]
    public void The_120_s_must_be_without_a_break()
    {
        var pc = new Pc();
        pc.For(100, NoUser);
        pc.Tick(ClubUser);                               // ClubUser signed in for a moment
        Assert.Equal(Counting, pc.For(100, NoUser).Decision.Reason);
        pc.Tick(null);                                   // the session list could not be read: the count starts again
        Assert.Equal(Counting, pc.For(100, NoUser).Decision.Reason);
        Assert.Equal(RestartNow, pc.For(30, NoUser).Decision.Action);
    }

    [Fact]
    public void ClubAdmin_signed_in_means_no_restart_and_its_sign_out_starts_the_count()
    {
        var pc = new Pc();
        Assert.Equal(UserSignedIn, pc.For(600, ClubAdminOnly).Decision.Reason);
        Assert.Equal(UserSignedIn, pc.For(600, ClubAdminAndClubUser).Decision.Reason);
        Assert.Equal(Counting, pc.For(120, NoUser).Decision.Reason);   // 118 s without a user
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), pc.Tick(NoUser).Decision);
    }

    [Fact]
    public void The_admin_route_comes_back_120_s_after_ClubAdmin_signs_out()
    {
        var pc = new Pc();
        pc.Tick(ClubUser);
        Assert.Equal(UserSignedIn, pc.For(600, ClubAdminAndClubUser).Decision.Reason);     // Switch user > ClubAdmin
        var signOut = pc.Tick(ClubUserDisconnectedOnly);                                     // ClubAdmin signs out
        Assert.Equal(new KioskRestartDecision(Wait, Counting), signOut.Decision);
        Assert.True(signOut.Changed);
        Assert.Equal(Counting, pc.For(116, ClubUserDisconnectedOnly).Decision.Reason);
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), pc.For(4, ClubUserDisconnectedOnly).Decision);
    }

    [Fact]
    public void A_player_Switch_user_on_a_locked_PC_restarts_after_120_s()
    {
        var pc = new Pc();
        pc.Tick(ClubUser);
        Assert.Equal(Counting, pc.For(120, ClubUserDisconnectedOnly).Decision.Reason);       // 118 s without a user
        Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), pc.Tick(ClubUserDisconnectedOnly).Decision);
    }

    [Fact]
    public void A_player_Switch_user_during_a_running_session_never_restarts_until_the_PC_locks()
    {
        var pc = new Pc { Locked = false };
        pc.Tick(ClubUser);
        Assert.Equal(new KioskRestartDecision(Wait, UserSignedIn), pc.For(3600, ClubUserDisconnectedOnly).Decision);
        pc.Locked = true;                                                                    // the session ends: locked
        var locked = pc.Tick(ClubUserDisconnectedOnly);
        Assert.Equal(new KioskRestartDecision(Wait, Counting), locked.Decision);
        Assert.True(locked.Changed);
        Assert.Equal(Counting, pc.For(116, ClubUserDisconnectedOnly).Decision.Reason);
        Assert.Equal(RestartNow, pc.For(4, ClubUserDisconnectedOnly).Decision.Action);
    }

    [Fact]
    public void A_session_that_starts_during_the_count_resets_it()
    {
        var pc = new Pc();
        Assert.Equal(Counting, pc.For(100, ClubUserDisconnectedOnly).Decision.Reason);
        pc.Locked = false;                                                                   // staff start a session
        Assert.Equal(UserSignedIn, pc.Tick(ClubUserDisconnectedOnly).Decision.Reason);
        pc.Locked = true;
        Assert.Equal(Counting, pc.For(100, ClubUserDisconnectedOnly).Decision.Reason);       // 100 s, not 200 s
        Assert.Equal(Counting, pc.For(18, ClubUserDisconnectedOnly).Decision.Reason);
        Assert.Equal(RestartNow, pc.For(4, ClubUserDisconnectedOnly).Decision.Action);
    }

    [Fact]
    public void The_staff_log_off_restarts_at_once_after_the_session_is_gone()
    {
        var pc = new Pc();
        pc.Tick(ClubUser);
        pc.Watch.StaffLogoff(pc.Uptime);
        Assert.Equal(UserSignedIn, pc.Tick(ClubUser).Decision.Reason);   // the lock screen signs the session out
        var step = pc.Tick(NoUser);
        Assert.Equal(new KioskRestartDecision(RestartNow, StaffLogoff), step.Decision);
    }

    [Fact]
    public void The_staff_log_off_restarts_at_once_also_shortly_after_a_boot()
    {
        var pc = new Pc { Uptime = TimeSpan.FromMinutes(1.5) };
        pc.Tick(ClubUser);
        pc.Watch.StaffLogoff(pc.Uptime);
        Assert.Equal(new KioskRestartDecision(RestartNow, StaffLogoff), pc.Tick(NoUser).Decision);
    }

    [Fact]
    public void A_staff_log_off_without_a_session_does_not_restart_a_PC_that_still_starts()
    {
        // The agent did not end a session: the boot rule applies (the automatic sign-in may still come)
        var pc = new Pc { Uptime = TimeSpan.FromMinutes(1) };
        pc.Tick(NoUser);
        pc.Watch.StaffLogoff(pc.Uptime);
        Assert.Equal(new KioskRestartDecision(Wait, WindowsStarting), pc.Tick(NoUser).Decision);
    }

    [Fact]
    public void A_staff_log_off_ends_after_2_min_when_another_user_stays()
    {
        var pc = new Pc();
        pc.Tick(ClubAdminAndClubUser);
        pc.Watch.StaffLogoff(pc.Uptime);
        pc.Tick(ClubAdminOnly);                          // ClubUser signed out, ClubAdmin stays
        Assert.Equal(UserSignedIn, pc.For(130, ClubAdminOnly).Decision.Reason);
        Assert.Equal(new KioskRestartDecision(Wait, Counting), pc.Tick(NoUser).Decision);   // the normal 120 s
    }

    [Fact]
    public void A_PC_without_kiosk_mode_never_restarts_also_after_a_staff_log_off()
    {
        var pc = new Pc { Kiosk = false };
        pc.Tick(ClubUser);
        pc.Watch.StaffLogoff(pc.Uptime);
        Assert.Equal(new KioskRestartDecision(Wait, NotKiosk), pc.Tick(NoUser).Decision);
        Assert.Equal(new KioskRestartDecision(Wait, NotKiosk), pc.For(600, NoUser).Decision);
    }

    [Fact]
    public void A_failed_restart_is_tried_again_only_after_a_new_count()
    {
        var pc = new Pc();
        Assert.Equal(RestartNow, pc.For(122, NoUser).Decision.Action);
        pc.Watch.RestartRequested(pc.Uptime);            // Windows refused it
        Assert.Equal(Counting, pc.Tick(NoUser).Decision.Reason);
        Assert.Equal(Counting, pc.For(116, NoUser).Decision.Reason);
        Assert.Equal(RestartNow, pc.For(4, NoUser).Decision.Action);
    }

    [Fact]
    public void A_boot_loop_stops_after_3_restarts_in_60_min()
    {
        IReadOnlyList<DateTimeOffset> history = NoRestarts;
        var clock = Start;
        for (var boot = 1; boot <= 4; boot++)
        {
            // Every boot: the automatic sign-in never comes; the agent starts 20 s after the boot
            var pc = new Pc { BootUtc = clock, Uptime = TimeSpan.FromSeconds(20), History = history };
            var step = pc.Tick(NoUser);
            Assert.Equal(WindowsStarting, step.Decision.Reason);
            for (var i = 0; i < 1000 && step.Decision.Reason == WindowsStarting; i++) step = pc.Tick(NoUser);
            Assert.Equal(TimeSpan.FromMinutes(5), pc.Uptime);     // never before 5 min of Windows
            var now = pc.Now;
            if (boot <= 3)
            {
                Assert.Equal(new KioskRestartDecision(RestartNow, NoUserSignedIn), step.Decision);
                history = KioskRestartHistory.Parse(KioskRestartHistory.With(history, now))!;
                clock = now.AddSeconds(40);                     // the next boot
            }
            else
            {
                Assert.Equal(new KioskRestartDecision(Blocked, GuardFull), step.Decision);
                Assert.True(step.Changed);                      // the ERROR line is written once
                Assert.False(pc.Tick(NoUser).Changed);
            }
        }
        Assert.Equal(3, history.Count);
    }
}

public sealed class KioskRestartHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_file_is_no_restart_and_a_broken_file_is_null()
    {
        Assert.Empty(KioskRestartHistory.Parse(null)!);
        Assert.Empty(KioskRestartHistory.Parse("  ")!);
        Assert.Null(KioskRestartHistory.Parse("not json"));
        Assert.Null(KioskRestartHistory.Parse("[1,2]"));
        Assert.Null(KioskRestartHistory.Parse("""{"restartsUtc": 5}"""));
    }

    [Fact]
    public void A_restart_is_added_and_entries_older_than_a_day_go()
    {
        var text = KioskRestartHistory.With(new[] { Now.AddDays(-2), Now.AddHours(-3) }, Now);
        var parsed = KioskRestartHistory.Parse(text)!;
        Assert.Equal(new[] { Now.AddHours(-3), Now }, parsed);
        Assert.Contains("\"restartsUtc\"", text);
    }

    [Fact]
    public void The_file_stays_small()
    {
        var many = Enumerable.Range(1, 50).Select(i => Now.AddMinutes(-i)).ToArray();
        var parsed = KioskRestartHistory.Parse(KioskRestartHistory.With(many, Now))!;
        Assert.Equal(KioskRestartHistory.MaxEntries, parsed.Count);
        Assert.Equal(Now, parsed[^1]);
    }

    [Fact]
    public void Entries_that_are_not_times_are_skipped()
    {
        var parsed = KioskRestartHistory.Parse("""{"restartsUtc": ["2026-11-28T01:00:00Z", 7, "x", null]}""")!;
        Assert.Equal(new[] { Now.AddHours(-1) }, parsed);
    }
}

public sealed class KioskInstallTests
{
    [Fact]
    public void A_kiosk_install_has_kiosk_json_with_the_kiosk_user()
    {
        const string installer = """{ "BlockWinKey": true, "KioskUser": "ClubUser", "KioskMode": true, "BlockAltF4": true }""";
        Assert.True(KioskAccount.IsKioskInstall(installer));
        Assert.True(KioskAccount.IsKioskInstall("""{"KioskUser":"ClubUser"}"""));
        Assert.False(KioskAccount.IsKioskInstall("""{"KioskUser":"ClubUser","KioskMode":false}"""));
        Assert.False(KioskAccount.IsKioskInstall("""{"KioskMode":true}"""));
        Assert.False(KioskAccount.IsKioskInstall("not json"));
        Assert.False(KioskAccount.IsKioskInstall(null));
    }

    [Theory]
    [InlineData(@"C:\ClubLauncher\Cms.Launcher.exe", @"C:\ClubLauncher\kiosk.json")]
    [InlineData(@"D:\Club\Launcher\Cms.Launcher.exe", @"D:\Club\Launcher\kiosk.json")]
    [InlineData(null, @"C:\ClubLauncher\kiosk.json")]
    [InlineData("  ", @"C:\ClubLauncher\kiosk.json")]
    [InlineData("Cms.Launcher.exe", @"C:\ClubLauncher\kiosk.json")]
    public void Kiosk_json_is_next_to_the_launcher_of_the_agent_settings(string? launcherExe, string expected)
    {
        Assert.Equal(expected, KioskAccount.ConfigPathFor(launcherExe));
    }
}
