using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Windows re-test 2026-11-27 late, defects 5 and 6: own dialogs that stayed open over a changed lock state.</summary>
public sealed class LockScreenDialogsTests
{
    [Theory]
    [InlineData(OwnDialogKind.Message)]    // defect 5: "Done / Unlocked by staff", then an admin lock
    [InlineData(OwnDialogKind.Question)]   // "End the session?" when the session ends or pauses
    [InlineData(OwnDialogKind.StaffPin)]   // a PIN prompt opened in the session
    public void A_new_lock_closes_every_own_dialog(OwnDialogKind kind) =>
        Assert.True(LockScreenDialogs.CloseOnChange(kind, wasLocked: false, locked: true));

    [Fact]
    public void An_unlock_closes_only_the_pin_prompt()
    {
        Assert.True(LockScreenDialogs.CloseOnChange(OwnDialogKind.StaffPin, wasLocked: true, locked: false));   // defect 6: resume
        Assert.False(LockScreenDialogs.CloseOnChange(OwnDialogKind.Message, wasLocked: true, locked: false));   // "Unlocked by staff" stays
        Assert.False(LockScreenDialogs.CloseOnChange(OwnDialogKind.Question, wasLocked: true, locked: false));
    }

    [Theory]
    [InlineData(OwnDialogKind.Message)]
    [InlineData(OwnDialogKind.Question)]
    [InlineData(OwnDialogKind.StaffPin)]
    public void Without_a_change_nothing_closes(OwnDialogKind kind)
    {
        Assert.False(LockScreenDialogs.CloseOnChange(kind, wasLocked: true, locked: true));    // the PIN prompt of a locked PC stays
        Assert.False(LockScreenDialogs.CloseOnChange(kind, wasLocked: false, locked: false));
    }
}
