using System;
using System.Runtime.InteropServices;

namespace Cms.Launcher;

/// <summary>
/// The keyboard hook of the locked screen (on only while locked): it blocks the Windows key, Alt+Tab, Ctrl+Esc and Alt+F4,
/// and it turns Ctrl+Shift+U into the staff PIN prompt whatever window has the keyboard (Cms.Client.StaffChord; Windows
/// test 2026-11-27 night, defects 2 and 3). The key state comes from GetAsyncKeyState: GetKeyState knows only the keys
/// that this thread read, so it missed Ctrl and Shift while another window had the keyboard.
/// </summary>
internal static class KeyboardBlocker
{
    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    private static bool _enabled;
    private static Action? _onStaffChord;
    private static readonly Cms.Client.StaffChord Chord = new();

    /// <summary>onStaffChord: called in the hook on Ctrl+Shift+U; it must only queue the prompt (a hook call must return at once).</summary>
    public static void Enable(Action onStaffChord)
    {
        _onStaffChord = onStaffChord;
        if (_enabled) return;
        _proc = HookCallback;
        _hookId = SetHook(_proc);
        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _proc = null;
        _enabled = false;
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            if (down || msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Ctrl+Shift+U: the staff PIN prompt, queued on the window thread; the key does not reach any window
                var (fire, swallow) = Chord.OnKey(kb.vkCode, down, IsKeyDown(VK_CONTROL), IsKeyDown(VK_SHIFT), IsKeyDown(VK_MENU),
                    IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN));
                if (fire)
                {
                    try { _onStaffChord?.Invoke(); } catch { /* the hook must return */ }
                }
                if (swallow || (down && ShouldBlock(kb)))
                {
                    return (IntPtr)1; // swallow
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(KBDLLHOOKSTRUCT kb)
    {

        // Windows keys
        if (kb.vkCode == VK_LWIN || kb.vkCode == VK_RWIN) return true;

        // Alt+Tab
        if (kb.vkCode == VK_TAB && IsKeyDown(VK_MENU)) return true;

        // Ctrl+Esc
        if (kb.vkCode == VK_ESCAPE && (IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL))) return true;

        // Alt+F4
        if (kb.vkCode == VK_F4 && IsKeyDown(VK_MENU)) return true;

        return false;
    }

    /// <summary>The key is held now (GetAsyncKeyState: the real state, also while another window has the keyboard).</summary>
    private static bool IsKeyDown(int vk)
    {
        short s = GetAsyncKeyState(vk);
        return (s & 0x8000) != 0;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_TAB = 0x09;
    private const int VK_MENU = 0x12;      // Alt
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_F4 = 0x73;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int nVirtKey);
}


