namespace Cms.Client;

/// <summary>The kinds of own dialogs of the lock screen (Cms.Launcher ClubDialog and StaffPinDialog).</summary>
public enum OwnDialogKind
{
    /// <summary>A message with OK ("Unlocked by staff", "Wrong PIN", a game that did not start ...).</summary>
    Message,
    /// <summary>A question with Yes / No (end of the session): closing it means No.</summary>
    Question,
    /// <summary>The staff PIN prompt: closing it means Cancel.</summary>
    StaffPin
}

/// <summary>
/// Which own dialogs close when the lock state changes (Windows re-test 2026-11-27 late, defects 5 and 6).
///  - A new lock closes every message, question and PIN prompt. Defect 5: the message "Done / Unlocked by staff" of a
///    staff PIN unlock stayed open after a later admin lock: it stayed above the locked screen, the main window stayed
///    disabled (no player login), and Ctrl+Shift+U did nothing until somebody pressed OK.
///  - An unlock (a session starts or resumes, a staff unlock) closes a PIN prompt. Defect 6: a PIN prompt opened in a
///    pause stayed above the running session after the resume. Messages stay: "Unlocked by staff" comes with the unlock.
/// </summary>
public static class LockScreenDialogs
{
    public static bool CloseOnChange(OwnDialogKind kind, bool wasLocked, bool locked) =>
        (!wasLocked && locked) || (wasLocked && !locked && kind == OwnDialogKind.StaffPin);
}
