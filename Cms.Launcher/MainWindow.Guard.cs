using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Cms.Client;

namespace Cms.Launcher;

/// <summary>
/// While the PC is locked, the lock screen stays above everything (Cms.Client.LockScreenGuard; Windows test 2026-11-27,
/// defect 8): every second it puts its window on top of the top-most band, takes the foreground back from another
/// program, and pushes down the top-most windows of other programs in the player's session (no longer top-most, then
/// minimized). Its own windows (the staff PIN dialog, messages) stay usable.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _guardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly int _ownProcessId = Environment.ProcessId;

    private static readonly nint HwndTopMost = -1;
    private static readonly nint HwndNoTopMost = -2;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    private const int GwlExStyle = -20;
    private const long WsExTopMost = 0x00000008;
    private const int SwMinimize = 6;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    private void StartLockGuard()
    {
        _guardTimer.Tick += (_, _) => GuardTick();
        _guardTimer.Start();
    }

    private void GuardTick()
    {
        if (_lastDecision is not { Locked: true }) return;
        try
        {
            var own = new WindowInteropHelper(this).Handle;
            if (own == 0) return;
            foreach (var window in TopLevelWindows())
                if (LockScreenGuard.ShouldPushDown(window, _ownProcessId)) PushDown(window.Handle);

            // On top of the top-most band (owned windows such as the staff PIN dialog stay above their owner)
            SetWindowPos(own, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (foreground != 0 && LockScreenGuard.ShouldTakeForeground((int)foregroundPid, ProcessName((int)foregroundPid), _ownProcessId))
            {
                Activate();
                SetForegroundWindow(own);
            }
        }
        catch (Exception)
        {
            // best effort: the next tick tries again
        }
    }

    /// <summary>No longer top-most, then minimized: the window cannot cover the lock screen or take a click.</summary>
    private static void PushDown(nint hwnd)
    {
        SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        ShowWindowAsync(hwnd, SwMinimize);
    }

    private static List<DesktopWindow> TopLevelWindows()
    {
        var windows = new List<DesktopWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var topMost = (GetWindowLongPtr(hwnd, GwlExStyle) & WsExTopMost) != 0;
            if (!topMost) return true; // only a top-most window can be above the locked screen
            GetWindowThreadProcessId(hwnd, out var pid);
            windows.Add(new DesktopWindow(hwnd, (int)pid, ProcessName((int)pid), true, true, IsIconic(hwnd)));
            return true;
        }, 0);
        return windows;
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
