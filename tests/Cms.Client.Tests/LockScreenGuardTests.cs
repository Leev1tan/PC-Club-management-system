using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// The guard of the locked screen. Windows test 2026-11-27, defect 8, and the night re-test, defects 1 and 2: the Windows
/// "Open with" dialog stayed above the locked screen and kept the keyboard; the staff PIN dialog lost the activation to
/// the main window. Security review of 3aa3619: Windows programs are trusted by full path, not by name.
/// </summary>
public sealed class LockScreenGuardTests
{
    private static readonly WindowsFolders Folders = new(@"C:\Windows", @"C:\Program Files\Common Files");
    private const int Own = 4242;
    private const int Session = 2;
    private static readonly GuardContext Context = new(Own, Session, @"C:\ClubLauncher\Cms.Launcher.exe", Folders);
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);

    private static GuardWindow Window(int pid, string? path, bool topMost = true, bool minimized = false, bool sameUser = true, int session = Session, nint? handle = null) =>
        new(handle ?? 1000 + pid, pid, path, sameUser, session, topMost, minimized);

    // ---- trusted programs of Windows: by full path only ----

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData(@"c:\windows\EXPLORER.EXE", false)]
    [InlineData(@"C:\Windows\System32\osk.exe", true)]
    [InlineData(@"C:\Windows\System32\ctfmon.exe", false)]
    [InlineData(@"C:\Windows\System32\InputMethod\CHS\ChsIME.exe", true)]
    [InlineData(@"C:\Windows\System32\Magnify.exe", true)]
    [InlineData(@"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe", true)]
    [InlineData(@"C:\Windows\SystemApps\Microsoft.LockApp_cw5n1h2txyewy\LockApp.exe", false)]
    [InlineData(@"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe", false)]                // a host process of Windows, never ended
    [InlineData(@"C:\Windows\System32\sihost.exe", false)]
    [InlineData(@"\\?\C:\Windows\System32\osk.exe", true)]
    public void Real_programs_of_windows_are_trusted(string path, bool mayHoldForeground)
    {
        Assert.True(WindowsParts.IsTrusted(path, Folders), path);
        Assert.Equal(mayHoldForeground, WindowsParts.MayHoldForeground(path, Folders));
    }

    [Theory]
    [InlineData(@"C:\Users\player\Desktop\explorer.exe")]                    // a program of the player with the name of Windows
    [InlineData(@"C:\Users\player\AppData\Local\Temp\osk.exe")]
    [InlineData(@"D:\Windows\System32\osk.exe")]                             // another drive
    [InlineData(@"\\server\share\Windows\System32\osk.exe")]                 // network path
    [InlineData(@"\\?\UNC\server\share\osk.exe")]
    [InlineData(@"C:\Windows\System32\osk.exe:evil.exe")]                    // alternate data stream
    [InlineData(@"C:\Windows\System32\..\..\Users\player\osk.exe")]          // ".." out of the folder
    [InlineData(@"C:\Windows\System32\OpenWith.exe")]                        // a Windows program, but not a helper: the defect
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Users\player\svchost.exe")]                           // the name of a host process elsewhere
    [InlineData(@"C:\Windows\osk.exe")]                                      // the right name in the wrong folder
    [InlineData(@"C:\Windows\SystemApps\TextInputHost.exe")]                 // no package folder
    [InlineData(@"C:\Windows\SystemApps\a\b\TextInputHost.exe")]             // two folder levels
    [InlineData(@"C:\Program Files\Common Files\TabTip.exe")]
    [InlineData(@"osk.exe")]                                                 // relative
    [InlineData(null)]                                                       // not readable
    [InlineData("")]
    public void Other_paths_are_not_trusted(string? path)
    {
        Assert.False(WindowsParts.IsTrusted(path, Folders), path);
        Assert.False(WindowsParts.MayHoldForeground(path, Folders), path);
    }

    [Fact]
    public void Without_known_folders_nothing_is_trusted()
    {
        Assert.False(WindowsParts.IsTrusted(@"C:\Windows\explorer.exe", new WindowsFolders(null, null)));
        Assert.False(WindowsParts.IsTrusted(@"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe", new WindowsFolders(@"C:\Windows", "")));
        Assert.All(WindowsParts.Keep, p => Assert.False(string.IsNullOrWhiteSpace(p.Why)));
    }

    // ---- which windows the guard may act on ----

    [Fact]
    public void Only_programs_of_the_player_in_this_session_are_foreign()
    {
        Assert.True(LockScreenGuard.IsForeign(Window(77, @"C:\Windows\System32\OpenWith.exe"), Context));
        Assert.True(LockScreenGuard.IsForeign(Window(78, @"C:\Users\player\Desktop\explorer.exe"), Context)); // a spoofed name
        Assert.True(LockScreenGuard.IsForeign(Window(79, null), Context));                                    // unknown path
        Assert.False(LockScreenGuard.IsForeign(Window(Own, @"C:\ClubLauncher\Cms.Launcher.exe"), Context));   // own dialogs
        Assert.False(LockScreenGuard.IsForeign(Window(80, @"C:\ClubLauncher\Cms.Launcher.exe"), Context));   // the watchdog
        Assert.False(LockScreenGuard.IsForeign(Window(81, @"C:\Windows\explorer.exe"), Context));
        Assert.False(LockScreenGuard.IsForeign(Window(82, @"C:\Tools\admin.exe", sameUser: false), Context));  // another user
        Assert.False(LockScreenGuard.IsForeign(Window(83, @"C:\Games\cs2.exe", session: 3), Context));         // another session
        Assert.False(LockScreenGuard.IsForeign(Window(4, @"C:\Windows\System32\ntoskrnl.exe"), Context));
    }

    [Fact]
    public void The_mode_closes_programs_only_in_kiosk_mode_without_a_session()
    {
        var long_ = TimeSpan.FromMinutes(1);
        Assert.Equal(GuardMode.Off, LockScreenGuard.Mode(false, true, true, true, long_));                  // a session runs
        Assert.Equal(GuardMode.PushDown, LockScreenGuard.Mode(true, false, true, false, long_));            // not a kiosk account
        Assert.Equal(GuardMode.KeepPrograms, LockScreenGuard.Mode(true, true, true, true, long_));          // paused session
        Assert.Equal(GuardMode.KeepPrograms, LockScreenGuard.Mode(true, true, false, false, long_));        // agent silent
        Assert.Equal(GuardMode.KeepPrograms, LockScreenGuard.Mode(true, true, true, false, TimeSpan.FromSeconds(9))); // teardown first
        Assert.Equal(GuardMode.ClosePrograms, LockScreenGuard.Mode(true, true, true, false, TimeSpan.FromSeconds(10)));
    }

    // ---- what the guard does with the windows ----

    [Fact]
    public void Paused_session_the_open_with_dialog_is_pushed_down_then_closed_then_ended()
    {
        var closer = new ForeignWindowCloser();
        var openWith = Window(77, @"C:\Windows\System32\OpenWith.exe");
        var game = Window(78, @"C:\Games\cs2.exe", topMost: false);           // a paused game: a normal window
        var both = new[] { openWith, game };

        var plan = closer.Plan(both, Context, GuardMode.KeepPrograms, T0);
        Assert.Equal(new[] { openWith.Handle }, plan.PushDown);
        Assert.Empty(plan.Close);
        // The push-down had no effect: 1 s later it is still above the lock screen
        plan = closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(1));
        Assert.Empty(plan.PushDown);
        Assert.Equal(new[] { openWith.Handle }, plan.Close);
        Assert.Empty(closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(2)).Terminate);
        plan = closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(3));     // 2 s after WM_CLOSE
        Assert.Equal(new[] { 77 }, plan.Terminate);
        Assert.Empty(closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(4)).Terminate); // once
        Assert.DoesNotContain(game.Handle, plan.Close);                                // the game is never closed in a pause
    }

    [Fact]
    public void A_window_that_stays_down_after_the_push_down_is_not_closed_but_a_third_return_is()
    {
        var closer = new ForeignWindowCloser();
        var dialog = Window(77, @"C:\Windows\System32\OpenWith.exe");
        var below = dialog with { TopMost = false, Minimized = true };
        Assert.Equal(new[] { dialog.Handle }, closer.Plan(new[] { dialog }, Context, GuardMode.KeepPrograms, T0).PushDown);
        var plan = closer.Plan(new[] { below }, Context, GuardMode.KeepPrograms, T0.AddSeconds(1));
        Assert.Empty(plan.Close);
        Assert.Empty(plan.PushDown);
        // A game that comes back above the lock screen an hour later is pushed down again, not closed
        Assert.Equal(new[] { dialog.Handle }, closer.Plan(new[] { dialog }, Context, GuardMode.KeepPrograms, T0.AddHours(1)).PushDown);
        closer.Plan(new[] { below }, Context, GuardMode.KeepPrograms, T0.AddHours(1).AddSeconds(1));
        Assert.Equal(new[] { dialog.Handle }, closer.Plan(new[] { dialog }, Context, GuardMode.KeepPrograms, T0.AddHours(2)).PushDown);
        closer.Plan(new[] { below }, Context, GuardMode.KeepPrograms, T0.AddHours(2).AddSeconds(1));
        // The third return: it makes itself top-most again and again, so it is closed at once
        plan = closer.Plan(new[] { dialog }, Context, GuardMode.KeepPrograms, T0.AddHours(3));
        Assert.Equal(new[] { dialog.Handle }, plan.Close);
        Assert.Empty(plan.PushDown);
    }

    [Fact]
    public void Paused_session_a_process_is_ended_only_while_its_window_is_still_above_the_lock_screen()
    {
        var closer = new ForeignWindowCloser();
        var overlay = Window(78, @"C:\Games\game.exe", handle: 5);            // a top-most overlay of a game
        var main = Window(78, @"C:\Games\game.exe", topMost: false, handle: 6); // the game itself
        closer.Plan(new[] { overlay, main }, Context, GuardMode.KeepPrograms, T0);
        Assert.Equal(new nint[] { 5 }, closer.Plan(new[] { overlay, main }, Context, GuardMode.KeepPrograms, T0.AddSeconds(1)).Close);
        // WM_CLOSE did not close the overlay, but it went below the lock screen: the game keeps running
        var down = overlay with { TopMost = false, Minimized = true };
        Assert.Empty(closer.Plan(new[] { down, main }, Context, GuardMode.KeepPrograms, T0.AddSeconds(4)).Terminate);
        // Still above the lock screen 2 s after WM_CLOSE: pushed down while waiting, then the process is ended
        var other = new ForeignWindowCloser();
        other.Plan(new[] { overlay }, Context, GuardMode.KeepPrograms, T0);
        other.Plan(new[] { overlay }, Context, GuardMode.KeepPrograms, T0.AddSeconds(1));
        Assert.Equal(new nint[] { 5 }, other.Plan(new[] { overlay }, Context, GuardMode.KeepPrograms, T0.AddSeconds(2)).PushDown);
        Assert.Equal(new[] { 78 }, other.Plan(new[] { overlay }, Context, GuardMode.KeepPrograms, T0.AddSeconds(3)).Terminate);
    }

    [Fact]
    public void A_process_id_that_windows_uses_again_is_ended_again()
    {
        var closer = new ForeignWindowCloser();
        var first = Window(70, @"C:\Apps\x.exe", topMost: false, handle: 1);
        closer.Plan(new[] { first }, Context, GuardMode.ClosePrograms, T0);
        Assert.Equal(new[] { 70 }, closer.Plan(new[] { first }, Context, GuardMode.ClosePrograms, T0.AddSeconds(2)).Terminate);
        closer.Plan(Array.Empty<GuardWindow>(), Context, GuardMode.ClosePrograms, T0.AddSeconds(3));          // ended: no window
        var reused = Window(70, @"C:\Apps\y.exe", topMost: false, handle: 9);                              // a new program, same id
        Assert.Equal(new nint[] { 9 }, closer.Plan(new[] { reused }, Context, GuardMode.ClosePrograms, T0.AddSeconds(4)).Close);
        Assert.Equal(new[] { 70 }, closer.Plan(new[] { reused }, Context, GuardMode.ClosePrograms, T0.AddSeconds(6)).Terminate);
        // A process that could not be ended (its window stays) is not tried every second
        Assert.Empty(closer.Plan(new[] { reused }, Context, GuardMode.ClosePrograms, T0.AddSeconds(7)).Terminate);
    }

    [Fact]
    public void Without_a_session_every_foreign_window_is_closed_but_trusted_and_other_users_stay()
    {
        var closer = new ForeignWindowCloser();
        var windows = new[]
        {
            Window(70, @"C:\Windows\System32\notepad.exe", topMost: false),
            Window(71, @"C:\Users\player\explorer.exe", topMost: false, minimized: true),
            Window(72, @"C:\Windows\explorer.exe", topMost: false),
            Window(73, @"C:\Windows\System32\osk.exe"),
            Window(74, @"C:\Tools\admin.exe", topMost: false, sameUser: false),
            Window(Own, @"C:\ClubLauncher\Cms.Launcher.exe")
        };
        var plan = closer.Plan(windows, Context, GuardMode.ClosePrograms, T0);
        Assert.Equal(new nint[] { 1070, 1071 }, plan.Close);
        Assert.Empty(plan.PushDown);
        Assert.Equal(new[] { 70, 71 }, closer.Plan(windows, Context, GuardMode.ClosePrograms, T0.AddSeconds(2)).Terminate);
    }

    [Fact]
    public void Two_windows_of_one_process_end_it_once_and_a_closed_window_is_forgotten()
    {
        var closer = new ForeignWindowCloser();
        var a = Window(70, @"C:\Apps\x.exe", topMost: false, handle: 1);
        var b = Window(70, @"C:\Apps\x.exe", topMost: false, handle: 2);
        Assert.Equal(2, closer.Plan(new[] { a, b }, Context, GuardMode.ClosePrograms, T0).Close.Count);
        Assert.Equal(new[] { 70 }, closer.Plan(new[] { a, b }, Context, GuardMode.ClosePrograms, T0.AddSeconds(2)).Terminate);
        // Closed by WM_CLOSE: gone from the list; a new window with the same handle and another process starts again
        Assert.Empty(closer.Plan(Array.Empty<GuardWindow>(), Context, GuardMode.ClosePrograms, T0.AddSeconds(3)).Close);
        var reused = Window(90, @"C:\Apps\y.exe", topMost: false, handle: 1);
        Assert.Equal(new nint[] { 1 }, closer.Plan(new[] { reused }, Context, GuardMode.ClosePrograms, T0.AddSeconds(4)).Close);
    }

    [Fact]
    public void An_unlock_cancels_every_pending_close()
    {
        var closer = new ForeignWindowCloser();
        var w = Window(70, @"C:\Apps\x.exe", topMost: false);
        closer.Plan(new[] { w }, Context, GuardMode.ClosePrograms, T0);
        Assert.Same(GuardPlan.Nothing, closer.Plan(new[] { w }, Context, GuardMode.Off, T0.AddSeconds(1)));   // the session started
        Assert.Empty(closer.Plan(new[] { w with { TopMost = false } }, Context, GuardMode.KeepPrograms, T0.AddSeconds(5)).Terminate);
    }

    [Fact]
    public void Not_in_kiosk_mode_windows_are_only_pushed_down()
    {
        var closer = new ForeignWindowCloser();
        var w = Window(77, @"C:\Windows\System32\OpenWith.exe");
        for (var s = 0; s < 5; s++)
        {
            var plan = closer.Plan(new[] { w, Window(78, @"C:\Apps\x.exe", topMost: false) }, Context, GuardMode.PushDown, T0.AddSeconds(s));
            Assert.Equal(new[] { w.Handle }, plan.PushDown);
            Assert.Empty(plan.Close);
            Assert.Empty(plan.Terminate);
        }
    }

    // ---- windows of a band above the desktop band (Windows re-test 2026-11-27 late, defect 1) ----

    private const int NotificationBand = 4;                // ZBID_IMMERSIVE_NOTIFICATION: the "Open with" dialog of Windows 11
    private const string OpenWith = @"C:\Windows\System32\OpenWith.exe";

    private static GuardWindow Banded(int pid, string? path, int band = NotificationBand, bool topMost = true, bool minimized = false,
        bool cloaked = false, bool sameUser = true, int session = Session) =>
        Window(pid, path, topMost, minimized, sameUser, session) with { Band = band, Cloaked = cloaked };

    [Theory]
    [InlineData(WindowBands.Unknown, false)]
    [InlineData(WindowBands.Desktop, false)]
    [InlineData(2, true)]                                  // ZBID_UIACCESS
    [InlineData(NotificationBand, true)]
    [InlineData(16, true)]                                 // ZBID_SYSTEM_TOOLS
    public void Bands_above_the_desktop_band(int band, bool above) => Assert.Equal(above, WindowBands.IsAboveDesktop(band));

    [Fact]
    public void A_shown_window_of_a_higher_band_covers_the_lock_screen_also_when_it_is_not_top_most()
    {
        Assert.True(Banded(77, OpenWith).Covers);
        Assert.True(Banded(77, OpenWith, topMost: false).Covers);
        Assert.False(Banded(77, OpenWith, topMost: false, cloaked: true).Covers);   // hidden by Windows
        Assert.False(Banded(77, OpenWith, minimized: true).Covers);
        Assert.False(Window(78, @"C:\Apps\x.exe", topMost: false).Covers);           // a normal desktop window
        Assert.True(Window(78, @"C:\Apps\x.exe").Covers);                            // top-most: as before
        Assert.True((Window(78, @"C:\Apps\x.exe") with { Cloaked = true }).Covers);             // desktop band: as before
    }

    [Fact]
    public void Paused_session_the_open_with_dialog_in_band_4_is_closed_at_once_and_ended_2_s_later()
    {
        var closer = new ForeignWindowCloser();
        var openWith = Banded(77, OpenWith);
        var notepad = Window(78, @"C:\Windows\System32\notepad.exe", topMost: false);   // the program of the player stays
        var both = new[] { openWith, notepad };

        var plan = closer.Plan(both, Context, GuardMode.KeepPrograms, T0);
        Assert.Equal(new[] { openWith.Handle }, plan.Close);                             // no push-down: it cannot work across bands
        Assert.Empty(plan.PushDown);
        Assert.Empty(closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(1)).Terminate);
        plan = closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(2));   // still there 2 s after WM_CLOSE
        Assert.Equal(new[] { 77 }, plan.Terminate);
        Assert.DoesNotContain(notepad.Handle, plan.Close);
        Assert.Empty(closer.Plan(both, Context, GuardMode.KeepPrograms, T0.AddSeconds(3)).Terminate); // once
    }

    [Fact]
    public void A_dialog_of_a_higher_band_that_closes_on_wm_close_is_not_ended()
    {
        var closer = new ForeignWindowCloser();
        var openWith = Banded(77, OpenWith, topMost: false);
        Assert.Equal(new[] { openWith.Handle }, closer.Plan(new[] { openWith }, Context, GuardMode.KeepPrograms, T0).Close);
        // Closed by WM_CLOSE: gone from the list, so nothing is ended
        Assert.Empty(closer.Plan(Array.Empty<GuardWindow>(), Context, GuardMode.KeepPrograms, T0.AddSeconds(3)).Terminate);
    }

    [Fact]
    public void After_the_end_of_a_session_a_window_of_a_higher_band_is_closed_and_ended()
    {
        var closer = new ForeignWindowCloser();
        var openWith = Banded(77, OpenWith);
        Assert.Equal(new[] { openWith.Handle }, closer.Plan(new[] { openWith }, Context, GuardMode.ClosePrograms, T0).Close);
        Assert.Equal(new[] { 77 }, closer.Plan(new[] { openWith }, Context, GuardMode.ClosePrograms, T0.AddSeconds(2)).Terminate);
    }

    [Fact]
    public void Hidden_windows_of_a_higher_band_are_never_touched()
    {
        // A suspended Store app or a hidden part of the shell: EnumWindows never returned these, the guard leaves them alone
        var closer = new ForeignWindowCloser();
        var hidden = Banded(77, @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_8wekyb3d8bbwe\CalculatorApp.exe", topMost: false, cloaked: true);
        foreach (var mode in new[] { GuardMode.PushDown, GuardMode.KeepPrograms, GuardMode.ClosePrograms })
        {
            for (var s = 0; s < 4; s++)
            {
                var plan = closer.Plan(new[] { hidden }, Context, mode, T0.AddSeconds(s));
                Assert.Empty(plan.PushDown);
                Assert.Empty(plan.Close);
                Assert.Empty(plan.Terminate);
            }
        }
        Assert.False(LockScreenGuard.Sees(hidden));
        Assert.True(LockScreenGuard.Sees(Window(78, @"C:\Apps\x.exe") with { Cloaked = true })); // desktop band: as before
    }

    [Fact]
    public void Higher_band_windows_of_windows_parts_other_users_and_other_sessions_are_never_touched()
    {
        var closer = new ForeignWindowCloser();
        var windows = new[]
        {
            Banded(70, @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe", band: 3),  // touch keyboard
            Banded(71, @"C:\Windows\SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe", band: 5),
            Banded(72, @"C:\Windows\System32\Magnify.exe", band: 2),
            Banded(73, OpenWith, sameUser: false),                                                                         // another user
            Banded(74, OpenWith, session: 3),                                                                              // another session
        };
        foreach (var mode in new[] { GuardMode.KeepPrograms, GuardMode.ClosePrograms })
        {
            for (var s = 0; s < 4; s++)
            {
                var plan = closer.Plan(windows, Context, mode, T0.AddSeconds(s));
                Assert.Empty(plan.PushDown);
                Assert.Empty(plan.Close);
                Assert.Empty(plan.Terminate);
            }
        }
    }

    [Fact]
    public void Not_in_kiosk_mode_a_window_of_a_higher_band_is_only_minimized_never_closed()
    {
        var closer = new ForeignWindowCloser();
        var openWith = Banded(77, OpenWith);
        for (var s = 0; s < 5; s++)
        {
            var plan = closer.Plan(new[] { openWith }, Context, GuardMode.PushDown, T0.AddSeconds(s));
            Assert.Equal(new[] { openWith.Handle }, plan.PushDown);
            Assert.Empty(plan.Close);
            Assert.Empty(plan.Terminate);
        }
    }

    // ---- the foreground ----

    [Fact]
    public void A_modal_dialog_of_the_lock_screen_is_the_target_not_the_disabled_main_window()
    {
        var main = new OwnWindow(1, IsMain: true, Visible: true, Enabled: true, CanActivate: true);
        Assert.Equal((nint)1, GuardFocus.Target(new[] { main }));
        var disabled = main with { Enabled = false };
        var pin = new OwnWindow(2, false, true, true, true);
        var toast = new OwnWindow(3, false, true, true, CanActivate: false);   // the warning toast never takes the focus
        Assert.Equal((nint)2, GuardFocus.Target(new[] { disabled, pin, toast }));
        Assert.Null(GuardFocus.Target(new[] { disabled }));                    // ShowDialog has not shown the dialog yet
        var message = new OwnWindow(4, false, true, true, true);
        Assert.Equal((nint)4, GuardFocus.Target(new[] { disabled, pin with { Enabled = false }, message })); // a message over the PIN dialog
        Assert.Equal((nint)1, GuardFocus.Target(new[] { main, toast, pin with { Visible = false } }));
    }

    [Fact]
    public void The_guard_never_takes_the_activation_from_an_own_window_that_takes_input()
    {
        nint main = 1, pin = 2, other = 77;
        // Defect 1: the disabled main window had the foreground while the PIN dialog was open: the dialog gets it
        Assert.Equal(ForegroundStep.ActivateOwn, GuardFocus.Decide(main, isOwn: true, ownEnabled: false, mayHold: false, target: pin));
        Assert.Equal(ForegroundStep.None, GuardFocus.Decide(pin, true, true, false, pin));
        Assert.Equal(ForegroundStep.None, GuardFocus.Decide(pin, true, true, false, main));      // an own dialog keeps it
        // Another program: the target (the dialog, when one is open) takes the foreground back
        Assert.Equal(ForegroundStep.TakeFromOther, GuardFocus.Decide(other, false, true, false, pin));
        Assert.Equal(ForegroundStep.TakeFromOther, GuardFocus.Decide(other, false, true, false, main));
        Assert.Equal(ForegroundStep.None, GuardFocus.Decide(other, false, true, mayHold: true, main)); // the touch keyboard
        Assert.Equal(ForegroundStep.ActivateOwn, GuardFocus.Decide(0, false, false, false, main));       // nobody has it
        Assert.Equal(ForegroundStep.None, GuardFocus.Decide(other, false, true, false, null));          // no target yet
    }
}
