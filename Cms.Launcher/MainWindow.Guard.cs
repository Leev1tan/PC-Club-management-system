using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cms.Client;
using Cms.Shared;
using Microsoft.Win32.SafeHandles;

namespace Cms.Launcher;

/// <summary>
/// While the PC is locked, the lock screen stays above everything and keeps the keyboard. The decisions are in Cms.Client
/// (LockScreenGuard, ForeignWindowCloser, GuardFocus, WindowsParts; unit-tested); this part only calls Windows. Every
/// second, only while locked (a running session is never touched):
///  - it deals with the windows of other programs of the player: push down, close, end (ForeignWindowCloser), also the
///    windows of the bands above the desktop band, which EnumWindows does not list (TopLevelWindows);
///  - it puts its window on top of the top-most band;
///  - it gives the foreground to its own window that must have it: the open dialog (staff PIN, message) or the main
///    window. From another program it takes the foreground with AttachThreadInput (see BringToFront).
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _guardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ForeignWindowCloser _closer = new();
    private readonly WindowsFolders _windowsFolders = WindowsFolders.Current();
    private readonly SecurityIdentifier? _ownUser = CurrentUser();
    private GuardContext? _guardContext;
    private DateTimeOffset _lockedSinceUtc = DateTimeOffset.UtcNow;

    private static readonly nint HwndTopMost = -1;
    private static readonly nint HwndNoTopMost = -2;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpAsyncWindowPos = 0x4000;
    private const int GwlExStyle = -20;
    private const long WsExTopMost = 0x00000008;
    private const int SwMinimize = 6;
    private const uint WmClose = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(nint hwnd, out uint band); // Windows 8 and later, not documented
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char[] name, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, nint info, int length, out int returned);

    /// <summary>What the guard knows of a process: the full path of its program, its Windows user (same as the lock screen?) and session.</summary>
    private readonly record struct ProcessFacts(string? ImagePath, bool SameUser, int SessionId);

    private void StartLockGuard()
    {
        _guardTimer.Tick += (_, _) => GuardTick();
        _guardTimer.Start();
    }

    private void GuardTick()
    {
        var decision = _lastDecision;
        var mode = LockScreenGuard.Mode(decision is { Locked: true }, _kioskMode, decision?.Reason == LockReason.LockedByAgent,
            _lastState?.Session != null, DateTimeOffset.UtcNow - _lockedSinceUtc);
        if (mode == GuardMode.Off)
        {
            _closer.Plan(Array.Empty<GuardWindow>(), OwnGuardContext(), mode, DateTimeOffset.UtcNow); // forget pending closes
            return;
        }
        try
        {
            var own = new WindowInteropHelper(this).Handle;
            if (own == 0) return;
            var facts = new Dictionary<uint, ProcessFacts>();
            var plan = _closer.Plan(DesktopWindows(facts), OwnGuardContext(), mode, DateTimeOffset.UtcNow);
            foreach (var hwnd in plan.PushDown) PushDown(hwnd);
            foreach (var hwnd in plan.Close) PostMessage(hwnd, WmClose, 0, 0);
            foreach (var pid in plan.Terminate) _processes.Kill(pid, entireTree: false);

            // On top of the top-most band (owned windows such as the staff PIN dialog stay above their owner)
            SetWindowPos(own, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            KeepForeground(facts);
        }
        catch (Exception)
        {
            // best effort: the next tick tries again
        }
    }

    /// <summary>The lock screen (and its watchdog: the same program) for the guard decisions.</summary>
    private GuardContext OwnGuardContext() =>
        _guardContext ??= new GuardContext(Environment.ProcessId, _processes.CurrentSessionId, Environment.ProcessPath, _windowsFolders);

    private static SecurityIdentifier? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    /// <summary>The own window that must have the foreground gets it (GuardFocus): a modal dialog, not the disabled main window.</summary>
    private void KeepForeground(Dictionary<uint, ProcessFacts> facts)
    {
        var target = GuardFocus.Target(OwnWindows());
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundPid);
        var isOwn = foreground != 0 && foregroundPid == Environment.ProcessId;
        var mayHold = foreground != 0 && !isOwn && WindowsParts.MayHoldForeground(Facts(foregroundPid, facts).ImagePath, _windowsFolders);
        switch (GuardFocus.Decide(foreground, isOwn, isOwn && IsWindowEnabled(foreground), mayHold, target))
        {
            case ForegroundStep.ActivateOwn:
                SetForegroundWindow(target!.Value);
                break;
            case ForegroundStep.TakeFromOther:
                BringToFront(target!.Value);
                break;
        }
    }

    /// <summary>The windows of this process: the main window and its dialogs (staff PIN, messages, the warning toast).</summary>
    private List<OwnWindow> OwnWindows()
    {
        var list = new List<OwnWindow>();
        if (Application.Current == null) return list;
        foreach (Window w in Application.Current.Windows)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == 0) continue;
            list.Add(new OwnWindow(hwnd, ReferenceEquals(w, this), w.IsVisible && IsWindowVisible(hwnd), IsWindowEnabled(hwnd), w.ShowActivated && w.Focusable));
        }
        return list;
    }

    /// <summary>
    /// A dialog of the lock screen was shown (ContentRendered of StaffPinDialog and ClubDialog). While the PC is locked it takes
    /// the foreground at once, also when another program has it (Ctrl+Shift+U comes from the keyboard hook then).
    /// </summary>
    internal void OnDialogShown(Window dialog)
    {
        if (_lastDecision is not { Locked: true }) return;
        var hwnd = new WindowInteropHelper(dialog).Handle;
        if (hwnd != 0) BringToFront(hwnd);
    }

    private static int _bringBusy;

    /// <summary>
    /// Gives the foreground to a window of this process, also when another program has it. Windows refuses
    /// SetForegroundWindow to a program that is not in the foreground (the foreground lock: the guard of 3aa3619 could not
    /// take it back from the "Open with" dialog). For the moment of the call a helper thread attaches its input to the
    /// input thread of the foreground window (AttachThreadInput), so Windows treats the call as one of the foreground
    /// program. Chosen over a synthetic Alt key press (keybd_event): no key reaches the other program (Alt opens menus and
    /// is a key of many games) and the own keyboard hooks see nothing. The helper thread, not the window thread, waits when
    /// the other program is slow to give up the activation: the lock screen and its keyboard hook never stop. One helper
    /// at a time; never attached to a hung window (the guard ends a hung program instead). Called only while locked.
    /// </summary>
    private static void BringToFront(nint target)
    {
        if (GetForegroundWindow() == target) return;
        if (Interlocked.Exchange(ref _bringBusy, 1) == 1) return; // the last helper still waits for a slow program
        var helper = new Thread(() =>
        {
            try { BringToFrontNow(target); }
            catch (Exception) { /* best effort: the next guard tick tries again */ }
            finally { Volatile.Write(ref _bringBusy, 0); }
        })
        { IsBackground = true, Name = "Lock screen foreground" };
        helper.Start();
    }

    private static void BringToFrontNow(nint target)
    {
        var foreground = GetForegroundWindow();
        if (foreground == target) return;
        if (foreground != 0 && !IsHungAppWindow(foreground))
        {
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var self = GetCurrentThreadId();
            if (foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true))
            {
                try
                {
                    BringWindowToTop(target);
                    SetForegroundWindow(target);
                }
                finally
                {
                    AttachThreadInput(self, foregroundThread, false);
                }
                return;
            }
        }
        SetForegroundWindow(target); // nobody has the foreground, or the attach was refused (another desktop or integrity level)
    }

    /// <summary>
    /// No longer top-most, then minimized: the window cannot cover the lock screen or take a click. Both calls are posted to
    /// the thread of the window (SWP_ASYNCWINDOWPOS, ShowWindowAsync): a program that does not answer cannot stop the lock screen.
    /// </summary>
    private static void PushDown(nint hwnd)
    {
        SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpAsyncWindowPos);
        ShowWindowAsync(hwnd, SwMinimize);
    }

    /// <summary>
    /// Every visible top-level window of the desktop except the own ones, with the facts of its process, its band and
    /// whether Windows hides it. The decisions (which ones to look at, close or end) are in ForeignWindowCloser.
    /// </summary>
    private List<GuardWindow> DesktopWindows(Dictionary<uint, ProcessFacts> facts)
    {
        var windows = new List<GuardWindow>();
        var ownPid = (uint)Environment.ProcessId;
        foreach (var hwnd in TopLevelWindows())
        {
            if (!IsWindowVisible(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == ownPid || pid <= 4) continue;
            var f = Facts(pid, facts);
            var topMost = (GetWindowLongPtr(hwnd, GwlExStyle) & WsExTopMost) != 0;
            var band = Band(hwnd);
            windows.Add(new GuardWindow(hwnd, (int)pid, f.ImagePath, f.SameUser, f.SessionId, topMost, IsIconic(hwnd),
                band, WindowBands.IsAboveDesktop(band) && IsCloaked(hwnd)));
        }
        return windows;
    }

    private const int MaxTopLevelWindows = 20000;

    /// <summary>
    /// The top-level windows of every band (Windows re-test 2026-11-27 late, defect 1). EnumWindows returns only the windows
    /// of the desktop band (documented for Windows 8 and later): it never returned the "Open with" dialog of Windows 11 in
    /// band 4. FindWindowEx with the desktop as the parent returns the windows of every band. The walk stops early when a
    /// window closes during it (the next one cannot be found after it); the windows of EnumWindows are added, so the list
    /// never has less than before. The next guard tick walks again.
    /// </summary>
    private static List<nint> TopLevelWindows()
    {
        var list = new List<nint>();
        var seen = new HashSet<nint>();
        nint after = 0;
        for (var i = 0; i < MaxTopLevelWindows; i++)
        {
            var hwnd = FindWindowEx(0, after, null, null);
            if (hwnd == 0 || !seen.Add(hwnd)) break;
            list.Add(hwnd);
            after = hwnd;
        }
        EnumWindows((hwnd, _) =>
        {
            if (seen.Add(hwnd)) list.Add(hwnd);
            return true;
        }, 0);
        return list;
    }

    private static bool _bandUnavailable;

    /// <summary>The z-order band of a window (WindowBands), or WindowBands.Unknown (Windows 7, or the call failed).</summary>
    private static int Band(nint hwnd)
    {
        if (_bandUnavailable) return WindowBands.Unknown;
        try
        {
            return GetWindowBand(hwnd, out var band) ? (int)band : WindowBands.Unknown;
        }
        catch (EntryPointNotFoundException)
        {
            _bandUnavailable = true; // Windows 7: no bands, every window is in the desktop band
            return WindowBands.Unknown;
        }
    }

    private const int DwmaCloaked = 14;

    /// <summary>Windows hides the window (DWMWA_CLOAKED): it is not drawn, so it covers nothing.</summary>
    private static bool IsCloaked(nint hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The program path, user and session of a process (once per tick). A process that cannot be opened, or whose token
    /// cannot be read (another user, the system), counts as "not the same user": the guard never touches it.
    /// </summary>
    private ProcessFacts Facts(uint pid, Dictionary<uint, ProcessFacts> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;
        string? path = null;
        var sameUser = false;
        var session = ProcessIdToSessionId(pid, out var s) ? (int)s : -1;
        try
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (!process.IsInvalid)
            {
                var buffer = new char[1024];
                var size = (uint)buffer.Length;
                if (QueryFullProcessImageName(process, 0, buffer, ref size)) path = new string(buffer, 0, (int)size);
                sameUser = _ownUser != null && TokenUser(process) is { } user && _ownUser.Equals(user);
            }
        }
        catch (Exception)
        {
            sameUser = false;
        }
        return cache[pid] = new ProcessFacts(path, sameUser, session);
    }

    /// <summary>The Windows user of a process (TOKEN_USER), or null when its token cannot be read.</summary>
    private static SecurityIdentifier? TokenUser(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        using (token)
        {
            GetTokenInformation(token, TokenUserClass, 0, 0, out var length);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                // TOKEN_USER starts with SID_AND_ATTRIBUTES: the pointer to the SID comes first
                return GetTokenInformation(token, TokenUserClass, buffer, length, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
