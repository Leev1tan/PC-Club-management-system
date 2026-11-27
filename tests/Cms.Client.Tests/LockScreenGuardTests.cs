using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Windows test 2026-11-27, defect 8: the Windows "Open with" dialog stayed clickable above the locked screen.</summary>
public sealed class LockScreenGuardTests
{
    private const int Own = 4242;

    private static DesktopWindow Window(int pid, string? name, bool topMost = true, bool visible = true, bool minimized = false) =>
        new(1000 + pid, pid, name, visible, topMost, minimized);

    [Fact]
    public void A_top_most_window_of_another_program_is_pushed_down()
    {
        Assert.True(LockScreenGuard.ShouldPushDown(Window(77, "OpenWith"), Own));       // the dialog of the Windows test
        Assert.True(LockScreenGuard.ShouldPushDown(Window(78, "cs2"), Own));            // a game in full screen
        Assert.True(LockScreenGuard.ShouldPushDown(Window(79, "notepad"), Own));
    }

    [Fact]
    public void The_lock_screen_windows_and_the_windows_helpers_are_never_touched()
    {
        Assert.False(LockScreenGuard.ShouldPushDown(Window(Own, "Cms.Launcher"), Own));  // the staff PIN dialog, messages
        foreach (var helper in new[] { "TextInputHost", "osk", "ctfmon", "Magnify", "Narrator", "explorer", "dwm", "LogonUI" })
            Assert.False(LockScreenGuard.ShouldPushDown(Window(90, helper), Own), helper);
        Assert.False(LockScreenGuard.ShouldPushDown(Window(4, "System"), Own));
        Assert.False(LockScreenGuard.ShouldPushDown(Window(91, null), Own));             // ended already
    }

    [Fact]
    public void Hidden_normal_or_minimized_windows_stay_as_they_are()
    {
        Assert.False(LockScreenGuard.ShouldPushDown(Window(77, "OpenWith", topMost: false), Own));
        Assert.False(LockScreenGuard.ShouldPushDown(Window(77, "OpenWith", visible: false), Own));
        Assert.False(LockScreenGuard.ShouldPushDown(Window(77, "OpenWith", minimized: true), Own));
    }

    [Fact]
    public void The_foreground_goes_back_to_the_lock_screen_but_not_from_its_own_dialog_or_the_touch_keyboard()
    {
        Assert.True(LockScreenGuard.ShouldTakeForeground(77, "OpenWith", Own));
        Assert.True(LockScreenGuard.ShouldTakeForeground(78, null, Own));
        Assert.False(LockScreenGuard.ShouldTakeForeground(Own, "Cms.Launcher", Own));   // the staff PIN dialog has the focus
        Assert.False(LockScreenGuard.ShouldTakeForeground(90, "TextInputHost", Own));
    }
}
