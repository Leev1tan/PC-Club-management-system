namespace Cms.Client;

/// <summary>A visible top-level window of the player's desktop, as the lock screen sees it.</summary>
public sealed record DesktopWindow(nint Handle, int ProcessId, string? ProcessName, bool Visible, bool TopMost, bool Minimized);

/// <summary>
/// While the PC is locked, the lock screen stays above every window: every second it puts itself on top and in the
/// foreground, and it pushes down the top-most windows of other programs (Windows test 2026-11-27, defect 8: the Windows
/// "Open with" dialog stayed visible and clickable above the locked screen). The windows of the lock screen itself (the
/// staff PIN dialog, its message windows) and the input and accessibility helpers of Windows are never touched.
/// </summary>
public static class LockScreenGuard
{
    /// <summary>Processes of Windows that a player may need on the lock screen (touch keyboard, IME, magnifier ...).</summary>
    public static readonly IReadOnlySet<string> KeepProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "TabTip", "osk", "ctfmon", "ChsIME", "ChtIME", "Magnify", "Narrator", "AtBroker",
        "dwm", "csrss", "winlogon", "LogonUI", "LockApp", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost"
    };

    /// <summary>True for a visible, not minimized, top-most window of another program that is not a Windows helper.</summary>
    public static bool ShouldPushDown(DesktopWindow window, int ownProcessId) =>
        window.Visible && window.TopMost && !window.Minimized && window.ProcessId != ownProcessId && window.ProcessId > 4 &&
        !string.IsNullOrEmpty(window.ProcessName) && !KeepProcesses.Contains(window.ProcessName);

    /// <summary>
    /// The lock screen takes the foreground back only when another program has it: a window of the lock screen itself (the
    /// staff PIN dialog, a message) keeps it, and so does a Windows helper (for example the touch keyboard).
    /// </summary>
    public static bool ShouldTakeForeground(int foregroundProcessId, string? foregroundProcessName, int ownProcessId) =>
        foregroundProcessId != ownProcessId && !(foregroundProcessName != null && KeepProcesses.Contains(foregroundProcessName));
}
