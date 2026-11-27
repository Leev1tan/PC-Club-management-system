namespace Cms.Client;

/// <summary>The folders of Windows on this PC (known folders, not environment variables that a player could change).</summary>
public sealed record WindowsFolders(string? Windows, string? CommonProgramFiles)
{
    public static WindowsFolders Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
}

/// <summary>
/// The programs of Windows that the lock screen never closes and never pushes down, by their FULL PATH (security review of
/// 3aa3619: an allow-list by process name let a player call any program explorer.exe or osk.exe and keep its window above
/// the locked screen). A path counts only after LauncherPath.Normalize (no network path, no alternate data stream, no ".."
/// left) and only in folders that a player cannot write to: %SystemRoot%, %SystemRoot%\System32, one package folder in
/// %SystemRoot%\SystemApps, and %CommonProgramFiles%\microsoft shared\ink. An unknown or unreadable path is not trusted.
/// Other programs of Windows (OpenWith.exe, notepad.exe, cmd.exe, SystemSettings.exe ...) are programs of the player.
/// </summary>
public static class WindowsParts
{
    public enum Place { Windows, System32, SystemApps, Ink }

    /// <summary>A trusted program: where it is, its file, whether it may keep the foreground over the lock screen, and why it stays.</summary>
    public sealed record Part(Place Place, string File, bool MayHoldForeground, string Why);

    public static readonly IReadOnlyList<Part> Keep = new Part[]
    {
        // The shell: ending one of these restarts the shell or Windows starts it again at once; none covers the lock screen
        new(Place.Windows, "explorer.exe", false, "the Windows shell (desktop, taskbar, File Explorer); its windows are below the top-most lock screen"),
        new(Place.SystemApps, "ShellExperienceHost.exe", false, "part of the shell (notifications, flyouts); Windows starts it again at once"),
        new(Place.SystemApps, "StartMenuExperienceHost.exe", false, "the Start menu; Windows starts it again at once, and the Win key is blocked while locked"),
        new(Place.SystemApps, "SearchHost.exe", false, "Windows search; Windows starts it again at once"),
        new(Place.SystemApps, "LockApp.exe", false, "the Windows lock screen (Win+L), above all windows anyway"),
        // Host processes of Windows that run as the player: system processes; ending one breaks the Windows session
        new(Place.System32, "svchost.exe", false, "the services of the player's session (clipboard, notifications, sync)"),
        new(Place.System32, "sihost.exe", false, "Shell Infrastructure Host of the session"),
        new(Place.System32, "taskhostw.exe", false, "host of the scheduled tasks of the session"),
        new(Place.System32, "RuntimeBroker.exe", false, "permission broker of the Windows apps"),
        // Text input: the player types the login and staff type the PIN with these
        new(Place.SystemApps, "TextInputHost.exe", true, "Windows 11 touch keyboard, emoji panel and IME candidate window"),
        new(Place.Ink, "TabTip.exe", true, "Windows 10 touch keyboard"),
        new(Place.System32, "osk.exe", true, "On-Screen Keyboard"),
        new(Place.System32, "ctfmon.exe", false, "text input framework (keyboard layouts, IME); without it no input language works"),
        new(Place.System32, @"InputMethod\CHS\ChsIME.exe", true, "Chinese (Simplified) IME and its candidate window"),
        new(Place.System32, @"InputMethod\CHT\ChtIME.exe", true, "Chinese (Traditional) IME and its candidate window"),
        // Accessibility: a player who sees badly or is blind needs these on the lock screen too
        new(Place.System32, "Magnify.exe", true, "Magnifier"),
        new(Place.System32, "Narrator.exe", true, "Narrator"),
        new(Place.System32, "AtBroker.exe", false, "starts the accessibility tools in the player's session"),
    };

    /// <summary>The trusted program at this full path, or null (an unknown, relative, network or invalid path).</summary>
    public static Part? Find(string? imagePath, WindowsFolders folders)
    {
        var path = LauncherPath.Normalize(imagePath);
        var windows = LauncherPath.Normalize(folders.Windows);
        if (path == null || windows == null) return null;
        var ink = LauncherPath.Normalize(folders.CommonProgramFiles) is { } common ? common + @"\microsoft shared\ink" : null;
        var systemApps = windows + @"\SystemApps\";
        foreach (var part in Keep)
        {
            if (part.Place == Place.SystemApps)
            {
                // %SystemRoot%\SystemApps\<package folder>\<file>: exactly one folder level (the folder name changes with the build)
                if (!path.StartsWith(systemApps, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = path[systemApps.Length..].Split('\\');
                if (rest.Length == 2 && string.Equals(rest[1], part.File, StringComparison.OrdinalIgnoreCase)) return part;
                continue;
            }
            var folder = part.Place switch
            {
                Place.Windows => windows,
                Place.System32 => windows + @"\System32",
                _ => ink
            };
            if (folder != null && string.Equals(path, folder + @"\" + part.File, StringComparison.OrdinalIgnoreCase)) return part;
        }
        return null;
    }

    public static bool IsTrusted(string? imagePath, WindowsFolders folders) => Find(imagePath, folders) != null;

    /// <summary>A text input or accessibility program of Windows: the lock screen does not take the foreground from it.</summary>
    public static bool MayHoldForeground(string? imagePath, WindowsFolders folders) => Find(imagePath, folders)?.MayHoldForeground == true;
}

/// <summary>
/// A visible top-level window of the player's desktop, as the lock screen sees it: its process, the full path of the
/// program (null when it cannot be read), whether the process runs as the same Windows user as the lock screen, and its
/// Windows session.
/// </summary>
public sealed record GuardWindow(nint Handle, int ProcessId, string? ImagePath, bool SameUser, int SessionId, bool TopMost, bool Minimized);

/// <summary>The lock screen itself: its process, Windows session and program (the watchdog is the same program).</summary>
public sealed record GuardContext(int OwnProcessId, int OwnSessionId, string? OwnImagePath, WindowsFolders Folders);

/// <summary>What the guard of the lock screen does while the PC is locked (LockScreenGuard.Mode).</summary>
public enum GuardMode
{
    /// <summary>Not locked (a session runs, or a staff PIN unlock): the guard does nothing; the games and their dialogs stay.</summary>
    Off,
    /// <summary>
    /// Locked, not in kiosk mode (the lock screen may run in an account of staff or of an administrator): foreign top-most
    /// windows are pushed down and the lock screen takes the foreground back; nothing is closed.
    /// </summary>
    PushDown,
    /// <summary>
    /// Kiosk mode, locked, and the programs of the player must stay (a paused session, a lock during a session, the agent
    /// silent, the first seconds of a lock): as PushDown, and a foreign top-most window that is above the lock screen again
    /// after its push-down is closed (Windows test 2026-11-27, defect 8: the "Open with" dialog ignored the push-down).
    /// </summary>
    KeepPrograms,
    /// <summary>Kiosk mode, locked without a session for 10 s (the agent answers): every foreign window is closed.</summary>
    ClosePrograms
}

/// <summary>
/// The decisions of the guard of the lock screen (Cms.Launcher MainWindow.Guard.cs keeps only the Win32 calls). Every second
/// while the PC is locked, the lock screen puts itself on top, takes the foreground back and deals with the windows of
/// other programs of the player. It never touches a process of another Windows user, of the system or of another Windows
/// session, its own windows, or a trusted program of Windows (WindowsParts, by full path).
/// </summary>
public static class LockScreenGuard
{
    /// <summary>A lock without a session closes the programs of the player after this time (the teardown runs first).</summary>
    public static readonly TimeSpan CloseProgramsAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The mode of the guard. agentAnswers: the lock comes from the agent (not "agent silent" or "no state", when the lock
    /// screen cannot know if a session runs). sessionOnPc: state.json has a session (also a paused one).
    /// </summary>
    public static GuardMode Mode(bool locked, bool kiosk, bool agentAnswers, bool sessionOnPc, TimeSpan lockedFor)
    {
        if (!locked) return GuardMode.Off;
        if (!kiosk) return GuardMode.PushDown;
        return agentAnswers && !sessionOnPc && lockedFor >= CloseProgramsAfter ? GuardMode.ClosePrograms : GuardMode.KeepPrograms;
    }

    /// <summary>
    /// A window of a program of the player: a process of the same Windows user in the same Windows session, not the lock
    /// screen or its watchdog, and not a trusted program of Windows. An unknown program path counts as foreign.
    /// </summary>
    public static bool IsForeign(GuardWindow window, GuardContext context) =>
        window.ProcessId > 4 && window.ProcessId != context.OwnProcessId && window.SameUser && window.SessionId == context.OwnSessionId &&
        !LauncherPath.Same(window.ImagePath, context.OwnImagePath) && !WindowsParts.IsTrusted(window.ImagePath, context.Folders);
}

/// <summary>What the guard does in this tick: push down (no longer top-most, minimized), close (WM_CLOSE), end the process.</summary>
public sealed record GuardPlan(IReadOnlyList<nint> PushDown, IReadOnlyList<nint> Close, IReadOnlyList<int> Terminate)
{
    public static readonly GuardPlan Nothing = new(Array.Empty<nint>(), Array.Empty<nint>(), Array.Empty<int>());
}

/// <summary>
/// The windows of other programs while the PC is locked (Windows test 2026-11-27, defect 8, and the night re-test, defect 2:
/// the Windows "Open with" dialog stayed above the locked screen and kept the keyboard for more than 3 minutes).
///  - PushDown mode: a foreign top-most window is pushed down every tick.
///  - KeepPrograms (a paused session): a foreign top-most window is pushed down. It gets WM_CLOSE when it is still above
///    the lock screen 1 s or more after the push-down (the push-down did not work), or when it comes back above the lock
///    screen for the 3rd time. Normal windows (a paused game) stay. 2 s after WM_CLOSE its process is ended only when the
///    window is still ABOVE the lock screen; a window that went below stays (a game with an overlay window keeps running).
///  - ClosePrograms: every foreign window gets WM_CLOSE; a window that is still there 2 s later ends its process.
///  - A process is ended once; a process id is forgotten when it has no window any more (Windows uses ids again).
/// Off (unlocked) forgets everything: nothing is closed or ended after the PC is unlocked.
/// </summary>
public sealed class ForeignWindowCloser
{
    public static readonly TimeSpan CloseAfterPushDown = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan TerminateAfter = TimeSpan.FromSeconds(2);
    /// <summary>KeepPrograms: a window that comes back above the lock screen this many times is closed at once.</summary>
    public const int MaxPushDowns = 3;

    private sealed class Seen
    {
        public required int ProcessId { get; init; }
        public DateTimeOffset? PushedDownUtc { get; set; }
        public int PushDowns { get; set; }
        public DateTimeOffset? CloseSentUtc { get; set; }
    }

    private readonly Dictionary<nint, Seen> _windows = new();
    private readonly HashSet<int> _ended = new();

    /// <summary>The plan of this tick. windows: every visible top-level window of the desktop now.</summary>
    public GuardPlan Plan(IReadOnlyList<GuardWindow> windows, GuardContext context, GuardMode mode, DateTimeOffset now)
    {
        if (mode == GuardMode.Off)
        {
            _windows.Clear();
            _ended.Clear();
            return GuardPlan.Nothing;
        }
        var push = new List<nint>();
        var close = new List<nint>();
        var end = new List<int>();
        var present = new HashSet<nint>();
        var presentProcesses = new HashSet<int>();
        foreach (var w in windows)
        {
            if (!LockScreenGuard.IsForeign(w, context)) continue;
            present.Add(w.Handle);
            presentProcesses.Add(w.ProcessId);
            if (!_windows.TryGetValue(w.Handle, out var seen) || seen.ProcessId != w.ProcessId)
                _windows[w.Handle] = seen = new Seen { ProcessId = w.ProcessId };
            var above = w.TopMost && !w.Minimized;
            if (!above) seen.PushedDownUtc = null; // the push-down holds; when it comes back it is pushed down again (counted)

            if (seen.CloseSentUtc is { } sent)
            {
                var mustEnd = mode == GuardMode.ClosePrograms || above;
                if (mustEnd && now - sent >= TerminateAfter)
                {
                    if (_ended.Add(w.ProcessId)) end.Add(w.ProcessId);
                }
                else if (above && mode != GuardMode.ClosePrograms)
                {
                    push.Add(w.Handle);
                }
                continue;
            }
            switch (mode)
            {
                case GuardMode.PushDown:
                    if (above) push.Add(w.Handle);
                    break;
                case GuardMode.KeepPrograms:
                    if (!above) break;
                    if (seen.PushDowns >= MaxPushDowns || (seen.PushedDownUtc is { } pushed && now - pushed >= CloseAfterPushDown))
                    {
                        seen.CloseSentUtc = now;
                        close.Add(w.Handle);
                        break;
                    }
                    if (seen.PushedDownUtc == null)
                    {
                        seen.PushedDownUtc = now;
                        seen.PushDowns++;
                    }
                    push.Add(w.Handle);
                    break;
                case GuardMode.ClosePrograms:
                    seen.CloseSentUtc = now;
                    close.Add(w.Handle);
                    break;
            }
        }
        foreach (var gone in _windows.Keys.Where(h => !present.Contains(h)).ToList()) _windows.Remove(gone);
        _ended.RemoveWhere(pid => !presentProcesses.Contains(pid));
        return new GuardPlan(push, close, end);
    }
}

/// <summary>A window of the lock screen process: the main window or one of its dialogs (staff PIN, messages, questions).</summary>
public sealed record OwnWindow(nint Handle, bool IsMain, bool Visible, bool Enabled, bool CanActivate);

/// <summary>What the guard does with the foreground in this tick.</summary>
public enum ForegroundStep
{
    None,
    /// <summary>Give the foreground to the target window; the lock screen has it already (or nobody has it), so Windows allows it.</summary>
    ActivateOwn,
    /// <summary>Take the foreground from another program (Cms.Launcher: AttachThreadInput to its input thread for the moment of the call).</summary>
    TakeFromOther
}

/// <summary>
/// Which window of the lock screen must have the foreground while the PC is locked (Windows test 2026-11-27 night, defect 1:
/// the guard gave the activation back to the main window while the modal staff PIN dialog was open, so the dialog was not
/// drawn and did not take Enter). The guard never takes the activation away from an own window that takes input; while a
/// modal dialog is open (the main window is disabled), the dialog is the target.
/// </summary>
public static class GuardFocus
{
    /// <summary>The newest visible, enabled own dialog that can be activated; else the main window when it is enabled; else null.</summary>
    public static nint? Target(IReadOnlyList<OwnWindow> own)
    {
        var dialog = own.LastOrDefault(w => !w.IsMain && w.Visible && w.Enabled && w.CanActivate);
        if (dialog != null) return dialog.Handle;
        var main = own.FirstOrDefault(w => w.IsMain);
        return main is { Visible: true, Enabled: true } ? main.Handle : null; // null: a modal dialog is not shown yet
    }

    /// <summary>
    /// foreground: the foreground window (0 = none); isOwn: it is a window of the lock screen; ownEnabled: it takes input;
    /// mayHold: a text input or accessibility program of Windows (WindowsParts.MayHoldForeground).
    /// </summary>
    public static ForegroundStep Decide(nint foreground, bool isOwn, bool ownEnabled, bool mayHold, nint? target)
    {
        if (target is not { } t || foreground == t) return ForegroundStep.None;
        if (isOwn) return ownEnabled ? ForegroundStep.None : ForegroundStep.ActivateOwn;
        if (foreground == 0) return ForegroundStep.ActivateOwn;
        return mayHold ? ForegroundStep.None : ForegroundStep.TakeFromOther;
    }
}
