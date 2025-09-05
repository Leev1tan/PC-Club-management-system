using System;
using System.Runtime.InteropServices;

namespace Cms.Launcher;

internal static class KeyboardBlocker
{
    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    private static bool _enabled;

    public static void Enable()
    {
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
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (ShouldBlock(kb))
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

    private static bool IsKeyDown(int vk)
    {
        short s = GetKeyState(vk);
        return (s & 0x8000) != 0;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

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
    private static extern short GetKeyState(int nVirtKey);
}


